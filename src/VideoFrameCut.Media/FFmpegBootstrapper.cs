using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace VideoFrameCut.Media;

/// <summary>
/// First-run, user-local portable bootstrapper for FFmpeg essentials.
/// Downloads the publisher's ZIP and SHA256 checksum over HTTPS, validates
/// the complete archive, then extracts only ffmpeg.exe and ffprobe.exe.
/// Never modifies PATH, the registry, the source video or system directories.
/// </summary>
public sealed class FFmpegBootstrapper
{
    public const string ArchiveUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    public const string ChecksumUrl = ArchiveUrl + ".sha256";
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(20)
    };

    public static bool HasTools(string? applicationDirectory = null)
    {
        var directory = applicationDirectory ?? AppContext.BaseDirectory;
        return IsAvailable("ffmpeg", directory) && IsAvailable("ffprobe", directory);
    }

    private static bool IsAvailable(string tool, string directory)
    {
        var fileName = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        if (File.Exists(Path.Combine(directory, fileName))) return true;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(folder =>
            {
                try { return File.Exists(Path.Combine(folder.Trim().Trim('"'), fileName)); }
                catch (ArgumentException) { return false; }
            });
    }

    public static string ParseChecksum(string checksumText)
    {
        var match = Regex.Match(checksumText, @"\b[a-fA-F0-9]{64}\b",
            RegexOptions.CultureInvariant);
        if (!match.Success) throw new InvalidDataException("공급처의 SHA-256 검증값이 올바르지 않습니다.");
        return match.Value.ToUpperInvariant();
    }

    public static void VerifyChecksum(string zipFilePath, string expectedSha256)
    {
        using var stream = File.OpenRead(zipFilePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("FFmpeg ZIP SHA-256 검증에 실패했습니다. 파일을 설치하지 않습니다.");
    }

    /// <summary>Chooses the two expected tools; rejects ambiguous and oversized archives.</summary>
    public static (ZipArchiveEntry Ffmpeg, ZipArchiveEntry Ffprobe) FindTools(ZipArchive archive)
    {
        ZipArchiveEntry One(string name)
        {
            var candidates = archive.Entries.Where(e =>
                e.FullName.Replace('\\', '/').EndsWith("/bin/" + name,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length != 1 ||
                candidates[0].Length is < 4096 or > 350_000_000)
                throw new InvalidDataException($"FFmpeg 압축파일에서 {name}을(를) 확인할 수 없습니다.");
            return candidates[0];
        }
        return (One("ffmpeg.exe"), One("ffprobe.exe"));
    }

    public async Task<bool> EnsureAsync(string? directory = null,
        IProgress<(double Fraction, string Message)>? progress = null,
        CancellationToken cancellationToken = default,
        HttpClient? client = null)
    {
        directory ??= AppContext.BaseDirectory;
        if (HasTools(directory)) return false;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("자동 FFmpeg 설치는 Windows에서만 지원합니다.");

        Directory.CreateDirectory(directory);
        var http = client ?? Http;
        var temporaryZip = Path.Combine(Path.GetTempPath(), "FrameCut-FFmpeg-" +
            Guid.NewGuid().ToString("N") + ".zip");
        var staged = new List<(string Temp, string Target, string Backup)>();

        try
        {
            progress?.Report((0, "FFmpeg 배포처에서 SHA-256 검증값을 확인하는 중..."));
            using (var response = await http.GetAsync(ChecksumUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var checksum = ParseChecksum(await response.Content.ReadAsStringAsync(cancellationToken));

                progress?.Report((0, "FFmpeg essentials 다운로드 중..."));
                using var download = await http.GetAsync(ArchiveUrl,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                download.EnsureSuccessStatusCode();
                var contentLength = download.Content.Headers.ContentLength;
                if (contentLength is > 350_000_000)
                    throw new InvalidDataException("예상보다 큰 FFmpeg 다운로드입니다.");

                await using (var output = new FileStream(temporaryZip, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                await using (var input = await download.Content.ReadAsStreamAsync(cancellationToken))
                {
                    var buffer = new byte[128 * 1024];
                    long total = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        total += count;
                        if (total > 350_000_000)
                            throw new InvalidDataException("다운로드 최대 크기를 초과했습니다.");
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                        if (contentLength is > 0)
                            progress?.Report((Math.Clamp(total / (double)contentLength.Value * .88, 0, .88),
                                $"FFmpeg 다운로드 {total / 1_048_576.0:0.0} / {contentLength.Value / 1_048_576.0:0.0} MiB"));
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((.90, "FFmpeg 파일 SHA-256 검증 중..."));
                VerifyChecksum(temporaryZip, checksum);
            }

            using (var archive = ZipFile.OpenRead(temporaryZip))
            {
                var (ffmpeg, ffprobe) = FindTools(archive);
                foreach (var (entry, fileName) in new[]
                {
                    (ffmpeg, "ffmpeg.exe"),
                    (ffprobe, "ffprobe.exe")
                })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report((fileName == "ffmpeg.exe" ? .93 : .97,
                        $"{fileName} 압축 해제 중..."));
                    var dest = Path.Combine(directory, fileName);
                    var stage = Path.Combine(directory, "." + fileName + "." +
                        Guid.NewGuid().ToString("N") + ".partial");
                    var backup = dest + "." + Guid.NewGuid().ToString("N") + ".backup";
                    staged.Add((stage, dest, backup));
                    await using (var input = entry.Open())
                    await using (var output = new FileStream(stage, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                    {
                        await input.CopyToAsync(output, cancellationToken);
                        await output.FlushAsync(cancellationToken);
                    }
                    if (new FileInfo(stage).Length != entry.Length)
                        throw new InvalidDataException(fileName + " 압축 해제 후 크기가 일치하지 않습니다.");
                    using var peek = File.OpenRead(stage);
                    if (peek.ReadByte() != 'M' || peek.ReadByte() != 'Z')
                        throw new InvalidDataException(fileName + "이(가) 정상 Windows 실행 파일이 아닙니다.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Both binaries are already staged and verified. Keep any existing
            // single binary recoverable if moving the second file fails.
            var committed = new List<(string Target, string Backup, bool HadOriginal)>();
            try
            {
                foreach (var item in staged)
                {
                    var hadOriginal = File.Exists(item.Target);
                    if (hadOriginal) File.Move(item.Target, item.Backup);
                    committed.Add((item.Target, item.Backup, hadOriginal));
                    File.Move(item.Temp, item.Target);
                }
                foreach (var item in committed)
                    if (File.Exists(item.Backup)) File.Delete(item.Backup);
            }
            catch
            {
                foreach (var item in committed.AsEnumerable().Reverse())
                {
                    if (File.Exists(item.Target)) File.Delete(item.Target);
                    if (item.HadOriginal && File.Exists(item.Backup))
                        File.Move(item.Backup, item.Target);
                }
                throw;
            }

            progress?.Report((1, "FFmpeg 및 FFprobe 설치 완료"));
            return true;
        }
        finally
        {
            try { if (File.Exists(temporaryZip)) File.Delete(temporaryZip); }
            catch (IOException) { }
            foreach (var item in staged)
            {
                try { if (File.Exists(item.Temp)) File.Delete(item.Temp); }
                catch (IOException) { }
            }
        }
    }
}
