using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using VideoFrameCut.Core;
using VideoFrameCut.Export;
using VideoFrameCut.Media;

namespace VideoFrameCut.App;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".mts", ".ts"
    };

    private readonly MediaProbe _probe = new();
    private readonly ClipExporter _exporter = new();
    private readonly PreviewFrameService _framePreview = new();
    private readonly ObservableCollection<SourceVideoRow> _sources = new();
    private readonly ObservableCollection<ClipRow> _rows = new();
    private readonly DispatcherTimer _playTimer;

    private SourceVideoRow? _activeSource;
    private SourceVideoInfo? _source;
    private DisplayFrameIndex? _index;
    private ClipEditor? _editor;
    private ClipPlaybackBounds? _clipBounds;
    private Guid? _selectedClipId;
    private CancellationTokenSource? _loadCancel;
    private CancellationTokenSource? _exportCancel;
    private CancellationTokenSource? _previewCancel;
    private bool _playing;
    private bool _clipEnded;
    private bool _updatingSlider;
    private bool _suppressSourceSelection;
    private bool _suppressClipSelection;

    public MainWindow()
    {
        InitializeComponent();
        SourceList.ItemsSource = _sources;
        ClipGrid.ItemsSource = _rows;

        // Frequent boundary checks prevent preview from running into the next
        // segment. MediaElement seeking remains approximate, unlike export.
        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        _playTimer.Tick += PlaybackTimer_Tick;
        UpdateControls();
    }

    private static bool IsVideoFile(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "원본 영상 추가 (여러 파일 선택 가능)",
            Filter = "동영상|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.mts;*.ts|모든 파일|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true)
            await ImportVideosAsync(dialog.FileNames);
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        e.Effects = files?.Any(f => File.Exists(f) && IsVideoFile(f)) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files is not null) await ImportVideosAsync(files);
    }

    private async Task ImportVideosAsync(IEnumerable<string> files)
    {
        if (_loadCancel is not null || _exportCancel is not null)
        {
            StatusText.Text = "다른 영상 분석 또는 출력 작업이 진행 중입니다.";
            return;
        }

        var paths = files.Where(f => File.Exists(f) && IsVideoFile(f))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0)
        {
            StatusText.Text = "추가할 수 있는 동영상 파일이 없습니다.";
            return;
        }

        var cancellation = new CancellationTokenSource();
        _loadCancel = cancellation;
        UpdateControls();
        var errors = new List<string>();
        try
        {
            foreach (var path in paths)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var exists = _sources.FirstOrDefault(s =>
                    string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
                if (exists is not null)
                {
                    SourceList.SelectedItem = exists;
                    continue;
                }

                try
                {
                    StatusText.Text = $"{Path.GetFileName(path)}: 미디어 정보 분석 중...";
                    var info = await _probe.ProbeAsync(path, cancellation.Token);
                    var progress = new Progress<int>(n =>
                    {
                        if (!cancellation.IsCancellationRequested)
                            StatusText.Text = $"{Path.GetFileName(path)}: {n:N0}프레임 인덱싱...";
                    });
                    var index = await _probe.BuildFrameIndexAsync(
                        path, info.NominalFramesPerSecond, progress, cancellation.Token);

                    var row = new SourceVideoRow(info, index);
                    _sources.Add(row);
                    SourceList.SelectedItem = row;
                    StatusText.Text = $"원본 {_sources.Count}개 등록됨. {row.Name}";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }

            if (errors.Count > 0)
                MessageBox.Show(this, string.Join(Environment.NewLine, errors),
                    "일부 영상 추가 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "영상 가져오기를 취소했습니다. 이미 추가한 원본은 유지됩니다.";
        }
        finally
        {
            _loadCancel = null;
            cancellation.Dispose();
            UpdateControls();
        }
    }

    private void SourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSourceSelection) return;
        if (_exportCancel is not null)
        {
            _suppressSourceSelection = true;
            SourceList.SelectedItem = _activeSource;
            _suppressSourceSelection = false;
            StatusText.Text = "출력이 끝난 다음 다른 원본을 선택할 수 있습니다.";
            return;
        }

        if (SourceList.SelectedItem is SourceVideoRow selected &&
            !ReferenceEquals(selected, _activeSource))
            ActivateSource(selected);
    }

    private void ActivateSource(SourceVideoRow selected)
    {
        PausePlayback();
        _previewCancel?.Cancel();
        _activeSource = selected;
        _source = selected.Media;
        _index = selected.Index;
        _editor = selected.Editor;
        _clipBounds = null;
        _selectedClipId = null;
        _clipEnded = false;

        _updatingSlider = true;
        SeekSlider.Minimum = 0;
        SeekSlider.Maximum = selected.Index.FrameCount - 1;
        SeekSlider.Value = 0;
        _updatingSlider = false;
        UpdateFrameLabel(0);

        InfoText.Text = $"{selected.Name}   {selected.Media.Width}×{selected.Media.Height}   " +
            $"{selected.Media.Codec}   {selected.Media.NominalFramesPerSecond:0.###}fps   " +
            $"{selected.Index.FrameCount:N0}프레임";
        PreviewModeText.Text = "재생 범위: 원본 전체";
        ExactStill.Visibility = Visibility.Collapsed;

        try
        {
            VideoPreview.Stop();
            VideoPreview.Source = new Uri(selected.Path, UriKind.Absolute);
            VideoPreview.Play();
            VideoPreview.Pause();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Windows 플레이어 준비 실패: {ex.Message}. 정확한 프레임 보기 기능을 사용하세요.";
        }

        RefreshRows();
    }

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is not SourceVideoRow selected) return;
        if (_loadCancel is not null || _exportCancel is not null)
        {
            StatusText.Text = "파일 분석이나 출력이 끝난 다음 목록에서 제거하세요.";
            return;
        }

        var position = _sources.IndexOf(selected);
        _suppressSourceSelection = true;
        _sources.Remove(selected);
        _suppressSourceSelection = false;
        _activeSource = null;

        if (_sources.Count > 0)
            SourceList.SelectedIndex = Math.Min(position, _sources.Count - 1);
        else
            ClearActiveSource();
        StatusText.Text = "목록에서만 제거했습니다. 원본 영상 파일은 삭제하지 않았습니다.";
    }

    private void ClearActiveSource()
    {
        PausePlayback();
        _previewCancel?.Cancel();
        _activeSource = null;
        _source = null;
        _index = null;
        _editor = null;
        _clipBounds = null;
        _selectedClipId = null;
        _clipEnded = false;
        VideoPreview.Stop();
        VideoPreview.Source = null;
        ExactStill.Source = null;
        ExactStill.Visibility = Visibility.Collapsed;
        _suppressClipSelection = true;
        _rows.Clear();
        _suppressClipSelection = false;
        _updatingSlider = true;
        SeekSlider.Minimum = 0;
        SeekSlider.Maximum = 1;
        SeekSlider.Value = 0;
        _updatingSlider = false;
        FrameTimeText.Text = "00:00:00.000 / Frame 0";
        PreviewModeText.Text = "재생 범위: 원본 전체";
        InfoText.Text = "원본 영상 없음";
        UpdateControls();
    }

    private void RefreshRows(Guid? preferredId = null, bool chooseAtPlayhead = false)
    {
        var cursor = (int)Math.Round(SeekSlider.Value);
        _suppressClipSelection = true;
        _rows.Clear();
        if (_editor is not null && _index is not null)
        {
            var number = 1;
            foreach (var segment in _editor.Segments)
            {
                var row = new ClipRow(number++, segment, _index);
                row.ExportChanged += Row_ExportChanged;
                _rows.Add(row);
            }
        }

        ClipRow? target = null;
        if (preferredId.HasValue)
            target = _rows.FirstOrDefault(r => r.Id == preferredId.Value);
        if (target is null && chooseAtPlayhead && _editor is not null)
        {
            var segment = _editor.Segments.FirstOrDefault(
                s => s.SourceRange.Start <= cursor && cursor < s.SourceRange.End);
            target = _rows.FirstOrDefault(r => r.Id == segment?.Id);
        }

        ClipGrid.SelectedItem = target;
        _suppressClipSelection = false;
        if (target is not null) SelectClip(target);
        else LeaveClipMode();

        UpdateControls();
        if (_editor is not null)
            StatusText.Text = $"원본 {_sources.Count}개 · 분할 구간 {_rows.Count}개 · 출력 선택 {_editor.ExportSelection().Count}개";
    }

    private void Row_ExportChanged(ClipRow row, bool enabled)
    {
        _editor?.SetExportEnabled(new[] { row.Id }, enabled);
        UpdateControls();
        if (_editor is not null)
            StatusText.Text = $"이 영상의 출력 선택 {_editor.ExportSelection().Count} / {_rows.Count}개";
    }

    private void ClipGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressClipSelection || _index is null) return;
        if (ClipGrid.SelectedItem is ClipRow selected)
            SelectClip(selected);
        else if (ClipGrid.SelectedItems.Count == 0)
            LeaveClipMode();
    }

    private void ClipGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ClipGrid.SelectedItem is not ClipRow row) return;
        SelectClip(row);
        StartPlayback(restart: true);
    }

    private void SelectClip(ClipRow row)
    {
        if (_index is null || _editor is null) return;
        var segment = _editor.Segments.FirstOrDefault(s => s.Id == row.Id);
        if (segment is null) return;

        PausePlayback();
        _clipBounds = new ClipPlaybackBounds(_index, segment.SourceRange);
        _selectedClipId = segment.Id;
        _clipEnded = false;

        _updatingSlider = true;
        SeekSlider.Minimum = _clipBounds.FirstFrame;
        SeekSlider.Maximum = _clipBounds.LastFrame;
        SeekSlider.Value = _clipBounds.FirstFrame;
        _updatingSlider = false;
        SeekToFrame(_clipBounds.FirstFrame);
        PreviewModeText.Text = $"구간 {row.Number:000} · {row.FromTime} ~ {row.ToTime} " +
            $"({row.FrameCount:N0}프레임)";
        StatusText.Text = "선택한 구간만 재생합니다. 더블클릭 또는 '선택 구간 재생'을 누르세요.";
    }

    private void LeaveClipMode()
    {
        if (_index is null) return;
        PausePlayback();
        _clipBounds = null;
        _selectedClipId = null;
        _clipEnded = false;
        _updatingSlider = true;
        SeekSlider.Minimum = 0;
        SeekSlider.Maximum = _index.FrameCount - 1;
        SeekSlider.Value = Math.Clamp((int)Math.Round(SeekSlider.Value), 0, _index.FrameCount - 1);
        _updatingSlider = false;
        PreviewModeText.Text = "재생 범위: 원본 전체";
        UpdateFrameLabel((int)Math.Round(SeekSlider.Value));
    }

    private void FullSource_Click(object sender, RoutedEventArgs e)
    {
        if (_index is null) return;
        _suppressClipSelection = true;
        ClipGrid.SelectedItem = null;
        _suppressClipSelection = false;
        LeaveClipMode();
        SeekToFrame(0);
        StatusText.Text = "원본 전체 보기로 전환했습니다. 재생 버튼을 누르면 처음부터 재생합니다.";
    }

    private void PlaySelected_Click(object sender, RoutedEventArgs e)
    {
        if (_index is null || _editor is null) return;
        if (ClipGrid.SelectedItem is not ClipRow row)
        {
            StatusText.Text = "아래 분할 구간 목록에서 재생할 구간을 먼저 선택하세요.";
            return;
        }
        SelectClip(row);
        StartPlayback(restart: true);
    }

    private void ClipStart_Click(object sender, RoutedEventArgs e) =>
        _ = ShowClipEdgeAsync(first: true);

    private void ClipEnd_Click(object sender, RoutedEventArgs e) =>
        _ = ShowClipEdgeAsync(first: false);

    private async Task ShowClipEdgeAsync(bool first)
    {
        if (_clipBounds is null)
        {
            StatusText.Text = "먼저 분할 구간을 선택하세요.";
            return;
        }
        PausePlayback();
        SeekToFrame(first ? _clipBounds.FirstFrame : _clipBounds.LastFrame);
        await ShowExactFrameAsync();
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_index is null) return;
        var frame = Math.Clamp((int)Math.Round(SeekSlider.Value), 0, _index.FrameCount - 1);
        UpdateFrameLabel(frame);
        if (_updatingSlider) return;
        _clipEnded = false;
        ExactStill.Visibility = Visibility.Collapsed;
        SeekMediaToFrame(frame);
    }

    private void UpdateFrameLabel(int frame)
    {
        if (_index is null) return;
        var relativeSeconds = Math.Max(0, _index.BoundaryTime(frame) - _index.StartTime);
        FrameTimeText.Text = $"{TimeSpan.FromSeconds(relativeSeconds).ToString(@"hh\:mm\:ss\.fff")} / Frame {frame:N0}";
    }

    private void SeekToFrame(int frame)
    {
        if (_index is null) return;
        frame = _clipBounds?.ClampFrame(frame) ?? Math.Clamp(frame, 0, _index.FrameCount - 1);
        _updatingSlider = true;
        SeekSlider.Value = frame;
        _updatingSlider = false;
        _clipEnded = false;
        UpdateFrameLabel(frame);
        ExactStill.Visibility = Visibility.Collapsed;
        SeekMediaToFrame(frame);
    }

    private void SeekMediaToFrame(int frame)
    {
        if (_index is null || VideoPreview.Source is null) return;
        try
        {
            var seconds = Math.Max(0, _index.BoundaryTime(frame) - _index.StartTime);
            VideoPreview.Position = TimeSpan.FromSeconds(seconds);
        }
        catch (InvalidOperationException) { }
    }

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null) return;
        var frame = (int)Math.Round(SeekSlider.Value);
        if (!_editor.SplitAt(frame))
        {
            StatusText.Text = "이 위치에서는 나눌 수 없습니다. 구간 내부 프레임을 선택하세요.";
            return;
        }
        RefreshRows(chooseAtPlayhead: true);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null) return;
        var ids = ClipGrid.SelectedItems.Cast<ClipRow>().Select(r => r.Id).ToArray();
        if (ids.Length == 0)
        {
            StatusText.Text = "삭제할 구간을 아래 목록에서 선택하세요.";
            return;
        }
        if (_editor.Delete(ids)) RefreshRows();
    }

    private void AutoSplit_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null) return;
        if (!double.TryParse(SplitSecondsBox.Text, NumberStyles.Float,
                CultureInfo.CurrentCulture, out var seconds) &&
            !double.TryParse(SplitSecondsBox.Text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out seconds))
        {
            MessageBox.Show(this, "분할 간격을 숫자(초)로 입력하세요.", "입력 오류");
            return;
        }

        try
        {
            if (_editor.SplitByTime(seconds)) RefreshRows(chooseAtPlayhead: true);
            else StatusText.Text = "현재 구간에 추가할 분할 경계가 없습니다.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "분할 실패"); }
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_editor?.Undo() == true) RefreshRows(_selectedClipId, chooseAtPlayhead: true);
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_editor?.Redo() == true) RefreshRows(_selectedClipId, chooseAtPlayhead: true);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null) return;
        if (_playing) PausePlayback();
        else StartPlayback();
    }

    private void StartPlayback(bool restart = false)
    {
        if (_index is null || VideoPreview.Source is null) return;
        if (_clipBounds is { } bounds)
        {
            var position = VideoPreview.Position.TotalSeconds;
            if (restart || _clipEnded || position < bounds.StartSeconds ||
                position >= bounds.EndSeconds - 0.005)
                SeekToFrame(bounds.FirstFrame);
        }
        else if (restart)
        {
            SeekToFrame(0);
        }

        ExactStill.Visibility = Visibility.Collapsed;
        _clipEnded = false;
        VideoPreview.Play();
        _playing = true;
        _playTimer.Start();
        StatusText.Text = _clipBounds is null ? "원본 전체 재생 중" : "선택 구간만 재생 중";
    }

    private void PausePlayback()
    {
        _playTimer.Stop();
        _playing = false;
        VideoPreview.Pause();
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (!_playing || _index is null) return;
        var seconds = VideoPreview.Position.TotalSeconds;
        if (_clipBounds is { } bounds && seconds >= bounds.EndSeconds)
        {
            PausePlayback();
            _clipEnded = true;
            SeekToFrame(bounds.LastFrame);
            _clipEnded = true;
            StatusText.Text = "선택 구간 재생 완료. 다음 구간으로 넘어가지 않았습니다.";
            return;
        }

        var frame = _index.NearestBoundary(seconds + _index.StartTime, 0, _index.FrameCount - 1);
        if (_clipBounds is not null) frame = _clipBounds.ClampFrame(frame);
        _updatingSlider = true;
        SeekSlider.Value = frame;
        _updatingSlider = false;
        UpdateFrameLabel(frame);
    }

    private async void PreviousFrame_Click(object sender, RoutedEventArgs e) => await StepAsync(-1);
    private async void NextFrame_Click(object sender, RoutedEventArgs e) => await StepAsync(+1);

    private async Task StepAsync(int direction)
    {
        if (_index is null) return;
        PausePlayback();
        SeekToFrame((int)Math.Round(SeekSlider.Value) + direction);
        await ShowExactFrameAsync();
    }

    private async void ExactFrame_Click(object sender, RoutedEventArgs e) => await ShowExactFrameAsync();

    private async Task ShowExactFrameAsync()
    {
        if (_source is null || _index is null) return;
        _previewCancel?.Cancel();
        var cancellation = new CancellationTokenSource();
        _previewCancel = cancellation;
        var sourcePath = _source.Path;
        var frame = (int)Math.Round(SeekSlider.Value);
        StatusText.Text = $"원본 프레임 {frame:N0} 정확한 정지화면 디코딩 중...";
        try
        {
            var bytes = await _framePreview.GrabPngAsync(sourcePath, frame, cancellation.Token);
            if (cancellation.IsCancellationRequested || _source?.Path != sourcePath) return;
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            ExactStill.Source = bitmap;
            ExactStill.Visibility = Visibility.Visible;
            StatusText.Text = $"원본 표시 프레임 {frame:N0} (FFmpeg 정확한 디코딩)";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = $"정확한 미리보기 실패: {ex.Message}"; }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null || _index is null || _source is null ||
            _exportCancel is not null || _loadCancel is not null) return;
        var dialog = new OpenFolderDialog { Title = "MP4 파일을 저장할 폴더 선택" };
        if (dialog.ShowDialog(this) != true) return;

        var clips = _editor.ExportSelection();
        if (clips.Count == 0) return;
        var source = _source;
        var index = _index;
        var cancellation = new CancellationTokenSource();
        _exportCancel = cancellation;
        UpdateControls();
        ExportProgressBar.Value = 0;

        var progress = new Progress<ExportProgress>(p =>
        {
            StatusText.Text = $"{p.Completed}/{p.Total} {p.CurrentOutput}: {p.Status}";
            ExportProgressBar.Value = p.Total == 0 ? 0 : 100.0 * p.Completed / p.Total;
        });

        try
        {
            var settings = new ExportSettings(CpuCheck.IsChecked == true
                ? EncodingMode.Software : EncodingMode.Auto, AudioCheck.IsChecked == true);
            var report = await _exporter.ExportAsync(
                source, index, clips, dialog.FolderName, settings, progress, cancellation.Token);
            var succeeded = report.Clips.Count(c => c.Succeeded);
            StatusText.Text = $"출력 검증 완료: 성공 {succeeded} / 요청 {clips.Count}. framecut_manifest.json 확인";
            if (succeeded != clips.Count)
                MessageBox.Show(this, "일부 구간이 실패했습니다. 출력 폴더의 framecut_manifest.json을 확인하세요.",
                    "결과 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "출력 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _exportCancel = null;
            cancellation.Dispose();
            UpdateControls();
        }
    }

    private void UpdateControls()
    {
        if (ExportButton is null || CancelButton is null) return;
        ExportButton.IsEnabled = _editor?.ExportSelection().Count > 0 &&
            _exportCancel is null && _loadCancel is null;
        CancelButton.IsEnabled = _loadCancel is not null || _exportCancel is not null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _exportCancel?.Cancel();
        _loadCancel?.Cancel();
        _previewCancel?.Cancel();
        StatusText.Text = "작업 취소 요청됨";
    }

    private void VideoPreview_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_index is null) return;
        VideoPreview.Pause();
        SeekMediaToFrame((int)Math.Round(SeekSlider.Value));
    }

    private void VideoPreview_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        PausePlayback();
        StatusText.Text = "Windows 재생기가 이 영상 코덱을 지원하지 않습니다. FFmpeg 정지화면은 사용할 수 있습니다.";
    }

    private void VideoPreview_MediaEnded(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        if (_clipBounds is not null)
        {
            _clipEnded = true;
            SeekToFrame(_clipBounds.LastFrame);
            _clipEnded = true;
        }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        var control = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (control && e.Key == Key.Z) Undo_Click(sender, e);
        else if (control && e.Key == Key.Y) Redo_Click(sender, e);
        else if (!control && e.Key == Key.S) Split_Click(sender, e);
        else if (!control && e.Key == Key.Delete && SourceList.IsKeyboardFocusWithin)
            RemoveSource_Click(sender, e);
        else if (!control && e.Key == Key.Delete) Delete_Click(sender, e);
        else if (!control && e.Key == Key.Space) PlayPause_Click(sender, e);
        else if (!control && e.Key == Key.Left) await StepAsync(-1);
        else if (!control && e.Key == Key.Right) await StepAsync(+1);
        else return;
        e.Handled = true;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _loadCancel?.Cancel();
        _exportCancel?.Cancel();
        _previewCancel?.Cancel();
        _playTimer.Stop();
        VideoPreview.Stop();
    }
}
