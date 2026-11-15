using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace VideoFrameCut.Media;

/// <summary>
/// Records processing failures locally; no network transmission or source-file
/// modification. Every user-visible error gets a stable copyable report ID.
/// FFmpeg stderr is preserved through exception.ToString().
/// Logging must never replace or crash the original error UI.
/// </summary>
public static class ProcessingDiagnostics
{
    private static readonly object Sync = new();
    private const int MaxErrorCharacters = 40000;
    private const long MaxDailyLogBytes = 5 * 1024 * 1024;
    private const int KeepDays = 14;

    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FrameCut", "logs");

    public static ProcessingErrorReport Record(
        string category, string operation, Exception? exception,
        string? context = null, string? logDirectory = null)
    {
        var timestamp = DateTimeOffset.Now;
        var categoryCode = new string(category.ToUpperInvariant()
            .Where(char.IsLetterOrDigit).Take(8).ToArray());
        if (categoryCode.Length == 0) categoryCode = "ERROR";
        var id = $"FC-{categoryCode}-{timestamp:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..(3 + categoryCode.Length + 1 + 8 + 1 + 6 + 1 + 8)];
        var version = typeof(ProcessingDiagnostics).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ProcessingDiagnostics).Assembly.GetName().Version?.ToString() ?? "unknown";
        var details = new StringBuilder()
            .AppendLine("FrameCut 처리 오류 보고서")
            .AppendLine($"오류 코드: {id}")
            .AppendLine($"발생 시각: {timestamp:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine($"작업: {operation}")
            .AppendLine($"버전: {version}")
            .AppendLine($"Windows/OS: {RuntimeInformation.OSDescription}")
            .AppendLine($".NET: {Environment.Version}")
            .AppendLine($"실행 프로세스: {Environment.ProcessId}")
            .AppendLine($"유형: {exception?.GetType().FullName ?? "NoException"}")
            .AppendLine($"HRESULT: 0x{exception?.HResult ?? 0:X8}")
            .AppendLine($"메시지: {exception?.Message ?? context ?? "알 수 없는 작업 오류"}");
        if (!string.IsNullOrWhiteSpace(context))
            details.AppendLine().AppendLine("작업 정보:").AppendLine(context);
        if (exception is not null)
            details.AppendLine().AppendLine("상세 예외 및 FFmpeg 오류 출력:")
                .AppendLine(exception.ToString());
        var body = details.ToString();
        if (body.Length > MaxErrorCharacters)
            body = body[..MaxErrorCharacters] + Environment.NewLine +
                "[긴 오류 출력은 표시 및 저장 길이 제한으로 잘렸습니다.]";

        var targetDirectory = logDirectory ?? LogDirectory;
        string? filePath = null;
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(targetDirectory);
                var basename = $"framecut-errors-{timestamp:yyyy-MM-dd}";
                var dayLog = Path.Combine(targetDirectory, basename + ".log");
                var number = 1;
                while (File.Exists(dayLog) && new FileInfo(dayLog).Length >= MaxDailyLogBytes)
                    dayLog = Path.Combine(targetDirectory, $"{basename}-{number++}.log");

                File.AppendAllText(dayLog, body + Environment.NewLine +
                    new string('-', 74) + Environment.NewLine, Encoding.UTF8);
                filePath = dayLog;
                // Cleanup is best-effort and bounded to FrameCut's own named files.
                foreach (var file in Directory.EnumerateFiles(targetDirectory,
                    "framecut-errors-*.log"))
                {
                    if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-KeepDays))
                    {
                        try { File.Delete(file); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new ProcessingErrorReport(id, operation, exception?.Message ??
            context ?? "작업을 완료하지 못했습니다.", body, filePath, targetDirectory);
    }
}

public sealed record ProcessingErrorReport(
    string Code, string Operation, string Message, string Details,
    string? SavedLogPath, string LogDirectory);
