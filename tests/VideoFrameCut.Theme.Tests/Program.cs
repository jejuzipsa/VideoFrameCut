using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using VideoFrameCut.App;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            // Create the editor XAML without showing a window. Catches late
            // resource/template lookup failures that 'dotnet build' can miss.
            var app = new App();
            app.InitializeComponent();
            // Validate native VLC engine availability, not only XAML loading.
            LibVLCSharp.Shared.Core.Initialize();
            using (var vlc = new LibVLCSharp.Shared.LibVLC("--no-video-title-show"))
            using (var player = new LibVLCSharp.Shared.MediaPlayer(vlc))
            {
                if (player.IsPlaying) throw new Exception("Unexpected active VLC playback.");
            }
            var window = new MainWindow();
            if (window.FindName("FFmpegInstallButton") is not Button)
                throw new Exception("FFmpeg first-run installer control not found.");
            if (window.FindName("VideoPreview") is not LibVLCSharp.WPF.VideoView)
                throw new Exception("Native LibVLC WPF video view was not initialized.");
            if (window.FindName("PlaybackNotice") is not Border ||
                window.FindName("ExactStill") is not Image)
                throw new Exception("VLC video overlays were not loaded.");
            if (window.Icon is null || window.Icon.Width < 16)
                throw new Exception("Film-scissors WPF window icon is missing or invalid.");
            if (window.FindName("MergeQueue") is not ListBox mergeQueue)
                throw new Exception("Cross-source merge queue was not initialized.");
            if (window.FindName("MergeTransitionCombo") is not ComboBox mergeMode ||
                mergeMode.Items.Count != 3 || mergeMode.SelectedIndex != 0)
                throw new Exception("Merge transition selector must have three choices with cut as the default.");
            if (mergeMode.Template is null || window.FindName("TransitionSecondsBox") is not TextBox seconds)
                throw new Exception("Merge transition selector or fade duration setting is missing.");
            if (seconds.Text != "0.5")
                throw new Exception("Default crossfade duration should be 0.5 seconds.");
            if (window.FindName("MergeExportButton") is not Button)
                throw new Exception("Merge MP4 output action is missing.");
            mergeMode.ApplyTemplate();
            if (mergeMode.Template is null || mergeQueue.ItemsSource is null)
                throw new Exception("Merge UI dark style and list binding are not loaded.");
            if (window.FindName("ClipGrid") is not DataGrid grid)
                throw new Exception("ClipGrid was not initialized.");
            if (grid.Columns.Count != 7)
                throw new Exception("Expected all seven segment columns.");

            AssertResource<Style>(window, "DarkDataGridHeaderStyle");
            AssertResource<Style>(window, "DarkDataGridCellStyle");
            AssertResource<Style>(window, "DarkDataGridRowStyle");
            AssertResource<Style>(window, "DarkGridCheckBoxStyle");
            AssertResource<Style>(window, "DarkDataGridTextStyle");

            if (grid.ColumnHeaderStyle is null || grid.CellStyle is null || grid.RowStyle is null)
                throw new Exception("DataGrid dark styles were not applied.");
            if (grid.Columns.OfType<DataGridTextColumn>().Any(c => c.ElementStyle is null))
                throw new Exception("One or more grid text columns have no dark foreground style.");
            var checkColumn = grid.Columns.OfType<DataGridCheckBoxColumn>().Single();
            if (checkColumn.ElementStyle is null || checkColumn.EditingElementStyle is null)
                throw new Exception("Checkbox column needs both display and edit dark styles.");

            var header = new DataGridColumnHeader
            {
                Content = "원본 프레임",
                Style = grid.ColumnHeaderStyle
            };
            header.ApplyTemplate();
            if (header.Template.FindName("HeaderSurface", header) is not Border)
                throw new Exception("Custom dark header template did not load.");

            var row = new DataGridRow { Style = grid.RowStyle };
            row.IsSelected = true;
            var background = row.Background as SolidColorBrush;
            if (background is null || background.Color == Colors.White)
                throw new Exception("Selected DataGrid row is light rather than dark.");

            var progress = window.FindName("ExportProgressBar") as ProgressBar
                ?? throw new Exception("Missing progress bar.");
            if (progress.Template is null || progress.Foreground is not SolidColorBrush fill ||
                fill.Color == Colors.Lime || fill.Color == Colors.Green)
                throw new Exception("ProgressBar dark template or fill is missing.");
            progress.ApplyTemplate();

            var playButton = window.FindName("TransportPlayPauseButton") as Button
                ?? throw new Exception("Playback button not found.");
            playButton.ApplyTemplate();
            if (playButton.Template is null)
                throw new Exception("Play button dark template missing.");

            Console.WriteLine("PASS WPF + LibVLC native engine: window loaded, headers, selection, text, checkboxes, buttons and progress");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL WPF dark theme: {ex}");
            return 1;
        }
    }

    private static T AssertResource<T>(FrameworkElement element, string key) where T : class
    {
        if (element.TryFindResource(key) is not T value)
            throw new Exception($"Theme resource not found: {key}");
        return value;
    }
}
