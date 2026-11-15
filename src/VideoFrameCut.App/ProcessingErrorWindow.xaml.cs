using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using VideoFrameCut.Media;

namespace VideoFrameCut.App;

/// <summary>Copyable processing failure dialog. Never hides FFmpeg stderr.</summary>
public partial class ProcessingErrorWindow : Window
{
    private readonly ProcessingErrorReport _report;

    public ProcessingErrorWindow(ProcessingErrorReport report)
    {
        InitializeComponent();
        _report = report ?? throw new ArgumentNullException(nameof(report));
        SummaryText.Text = $"{report.Operation}: {report.Message}";
        ErrorCodeBox.Text = report.Code;
        ErrorDetailsBox.Text = report.Details;
        LogLocationText.Text = report.SavedLogPath is null
            ? "로그 파일 저장이 불가능했습니다. [오류 전체 복사]로 내용을 보관하세요."
            : "저장된 로그: " + report.SavedLogPath;
        LogLocationText.ToolTip = report.SavedLogPath ?? report.LogDirectory;
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e) =>
        Copy(_report.Code, "오류 코드를 복사했습니다.");

    private void CopyDetails_Click(object sender, RoutedEventArgs e) =>
        Copy(_report.Details, "오류 코드와 전체 내용을 복사했습니다.");

    private void Copy(string value, string success)
    {
        try
        {
            Clipboard.SetText(value);
            CopyStatusText.Text = success;
        }
        catch (ExternalException ex)
        {
            CopyStatusText.Text = "클립보드 사용 불가: " + ex.Message;
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_report.LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _report.LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            CopyStatusText.Text = "로그 폴더를 열 수 없습니다: " + ex.Message;
        }
    }

    private void CloseError_Click(object sender, RoutedEventArgs e) => Close();
}
