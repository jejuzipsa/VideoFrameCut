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
    private readonly MediaProbe _probe = new();
    private readonly ClipExporter _exporter = new();
    private readonly PreviewFrameService _framePreview = new();
    private readonly ObservableCollection<ClipRow> _rows = new();
    private readonly DispatcherTimer _playTimer;
    private SourceVideoInfo? _source;
    private DisplayFrameIndex? _index;
    private ClipEditor? _editor;
    private CancellationTokenSource? _loadCancel;
    private CancellationTokenSource? _exportCancel;
    private CancellationTokenSource? _previewCancel;
    private bool _playing;
    private bool _updatingSlider;

    public MainWindow()
    {
        InitializeComponent();
        ClipGrid.ItemsSource = _rows;
        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _playTimer.Tick += (_, _) =>
        {
            if (!_playing || _index is null) return;
            var nearest = _index.NearestBoundary(VideoPreview.Position.TotalSeconds, 0, _index.FrameCount - 1);
            _updatingSlider = true;
            SeekSlider.Value = nearest;
            _updatingSlider = false;
        };
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "영상 파일 열기",
            Filter = "동영상|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v|모든 파일|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        _loadCancel?.Cancel();
        _loadCancel = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        ExportButton.IsEnabled = false;
        try
        {
            StatusText.Text = "스트림 정보를 분석하고 있습니다...";
            var source = await _probe.ProbeAsync(dialog.FileName, _loadCancel.Token);
            var progress = new Progress<int>(frames =>
                StatusText.Text = $"프레임 인덱싱 중: {frames:N0}장");
            var index = await _probe.BuildFrameIndexAsync(dialog.FileName,
                source.NominalFramesPerSecond, progress, _loadCancel.Token);

            _source = source;
            _index = index;
            _editor = new ClipEditor(index);
            SeekSlider.Maximum = Math.Max(0, index.FrameCount - 1);
            SeekSlider.Value = 0;
            InfoText.Text = $"{Path.GetFileName(source.Path)}   {source.Width}×{source.Height}   " +
                $"{source.Codec}   {source.NominalFramesPerSecond:0.###}fps   {index.FrameCount:N0}프레임";
            try
            {
                VideoPreview.Stop();
                VideoPreview.Source = new Uri(Path.GetFullPath(source.Path));
                VideoPreview.Play();
                VideoPreview.Pause();
            }
            catch (Exception) { /* Unsupported Windows decoder; exact FFmpeg still remains available. */ }
            ExactStill.Visibility = Visibility.Collapsed;
            _playing = false;
            _playTimer.Stop();
            RefreshRows();
            StatusText.Text = "분석 완료. 프레임 이동 후 나누거나 시간으로 자동 분할하세요.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "인덱싱 취소됨.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "영상 분석 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "영상 분석 실패";
        }
        finally { CancelButton.IsEnabled = _exportCancel is not null; }
    }

    private void RefreshRows()
    {
        _rows.Clear();
        if (_editor is null || _index is null) return;
        var number = 1;
        foreach (var segment in _editor.Segments)
        {
            var row = new ClipRow(number++, segment, _index);
            row.ExportChanged += Row_ExportChanged;
            _rows.Add(row);
        }
        ExportButton.IsEnabled = _editor.ExportSelection().Count > 0 && _exportCancel is null;
        StatusText.Text = $"총 {_rows.Count}개 구간, 출력 선택 {_editor.ExportSelection().Count}개";
    }

    private void Row_ExportChanged(ClipRow row, bool enabled)
    {
        _editor?.SetExportEnabled(new[] { row.Id }, enabled);
        if (_editor is not null)
        {
            ExportButton.IsEnabled = _editor.ExportSelection().Count > 0 && _exportCancel is null;
            StatusText.Text = $"출력 선택 {_editor.ExportSelection().Count} / {_rows.Count}";
        }
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_index is null) return;
        var frame = Math.Clamp((int)Math.Round(SeekSlider.Value), 0, _index.FrameCount - 1);
        FrameTimeText.Text = $"{TimeSpan.FromSeconds(Math.Max(0, _index.BoundaryTime(frame))):hh\\:mm\\:ss\\.fff} / Frame {frame:N0}";
        if (_updatingSlider) return;
        ExactStill.Visibility = Visibility.Collapsed;
        if (_source is not null && VideoPreview.Source is not null)
        {
            try { VideoPreview.Position = TimeSpan.FromSeconds(Math.Max(0, _index.BoundaryTime(frame))); }
            catch (InvalidOperationException) { }
        }
    }

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null) return;
        if (!_editor.SplitAt((int)Math.Round(SeekSlider.Value)))
            StatusText.Text = "이 위치에서는 나눌 수 없습니다. 구간의 내부 프레임을 선택하세요.";
        else RefreshRows();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null) return;
        var ids = ClipGrid.SelectedItems.Cast<ClipRow>().Select(r => r.Id).ToArray();
        if (ids.Length == 0) { StatusText.Text = "삭제할 구간을 목록에서 선택하세요."; return; }
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
            if (_editor.SplitByTime(seconds)) RefreshRows();
            else StatusText.Text = "현재 구간에 추가할 분할 경계가 없습니다.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "분할 실패"); }
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_editor?.Undo() == true) RefreshRows();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_editor?.Redo() == true) RefreshRows();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null) return;
        ExactStill.Visibility = Visibility.Collapsed;
        if (_playing) { VideoPreview.Pause(); _playTimer.Stop(); }
        else { VideoPreview.Play(); _playTimer.Start(); }
        _playing = !_playing;
    }

    private async void PreviousFrame_Click(object sender, RoutedEventArgs e) => await StepAsync(-1);
    private async void NextFrame_Click(object sender, RoutedEventArgs e) => await StepAsync(+1);

    private async Task StepAsync(int direction)
    {
        if (_index is null) return;
        VideoPreview.Pause();
        _playing = false;
        _playTimer.Stop();
        SeekSlider.Value = Math.Clamp((int)SeekSlider.Value + direction, 0, _index.FrameCount - 1);
        await ShowExactFrameAsync();
    }

    private async void ExactFrame_Click(object sender, RoutedEventArgs e) => await ShowExactFrameAsync();

    private async Task ShowExactFrameAsync()
    {
        if (_source is null || _index is null) return;
        _previewCancel?.Cancel();
        var cancellation = new CancellationTokenSource();
        _previewCancel = cancellation;
        var frame = (int)Math.Round(SeekSlider.Value);
        StatusText.Text = $"정확한 프레임 {frame:N0} 디코딩 중...";
        try
        {
            var bytes = await _framePreview.GrabPngAsync(_source.Path, frame, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
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
        if (_editor is null || _index is null || _source is null || _exportCancel is not null) return;
        var dialog = new OpenFolderDialog { Title = "MP4 파일을 저장할 폴더 선택" };
        if (dialog.ShowDialog(this) != true) return;
        var clips = _editor.ExportSelection();
        if (clips.Count == 0) return;

        _exportCancel = new CancellationTokenSource();
        ExportButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
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
            var report = await _exporter.ExportAsync(_source, _index, clips, dialog.FolderName,
                settings, progress, _exportCancel.Token);
            var succeeded = report.Clips.Count(c => c.Succeeded);
            StatusText.Text = $"출력 검증 완료: 성공 {succeeded} / 요청 {clips.Count}. 매니페스트: framecut_manifest.json";
            if (succeeded != clips.Count)
                MessageBox.Show(this, "일부 구간이 실패했어요. 출력 폴더의 framecut_manifest.json을 확인하세요.",
                    "결과 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "출력 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _exportCancel.Dispose();
            _exportCancel = null;
            CancelButton.IsEnabled = _loadCancel is not null;
            ExportButton.IsEnabled = _editor.ExportSelection().Count > 0;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _exportCancel?.Cancel();
        _loadCancel?.Cancel();
        _previewCancel?.Cancel();
        StatusText.Text = "취소 요청됨";
    }

    private void VideoPreview_MediaEnded(object sender, RoutedEventArgs e)
    {
        _playing = false;
        _playTimer.Stop();
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        var control = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (control && e.Key == Key.Z) Undo_Click(sender, e);
        else if (control && e.Key == Key.Y) Redo_Click(sender, e);
        else if (!control && e.Key == Key.S) Split_Click(sender, e);
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
