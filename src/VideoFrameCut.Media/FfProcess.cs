using System.Diagnostics;
using System.Text;

namespace VideoFrameCut.Media;

/// <summary>Safe process invocation: argument list (not shell), stderr capture, cancellation.</summary>
public static class FfProcess
{
    private static Process Create(string executable, IEnumerable<string> args)
    {
        // A portable package can ship ffmpeg.exe and ffprobe.exe next to the app.
        var localName = OperatingSystem.IsWindows() ? executable + ".exe" : executable;
        if (!Path.IsPathRooted(executable) && !executable.Contains(Path.DirectorySeparatorChar))
        {
            var sibling = Path.Combine(AppContext.BaseDirectory, localName);
            if (File.Exists(sibling)) executable = sibling;
        }
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return new Process { StartInfo = info };
    }

    public static async Task<string> CaptureAsync(string executable, IEnumerable<string> args,
        CancellationToken cancellation = default)
    {
        using var process = Create(executable, args);
        process.Start();
        using var registration = cancellation.Register(() => Kill(process));
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
        var stderr = process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);
        var output = await stdout;
        var errors = await stderr;
        if (process.ExitCode != 0) throw Failure(executable, process.ExitCode, errors);
        return output;
    }

    public static async Task ForEachLineAsync(string executable, IEnumerable<string> args,
        Action<string> onLine, CancellationToken cancellation = default)
    {
        using var process = Create(executable, args);
        process.Start();
        using var registration = cancellation.Register(() => Kill(process));
        var stderr = process.StandardError.ReadToEndAsync(cancellation);
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancellation)) is not null)
                onLine(line);
            await process.WaitForExitAsync(cancellation);
            var errors = await stderr;
            if (process.ExitCode != 0) throw Failure(executable, process.ExitCode, errors);
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private static Exception Failure(string executable, int exitCode, string stderr)
    {
        var excerpt = stderr.Length > 5000 ? stderr[^5000..] : stderr;
        return new InvalidOperationException($"{executable} exited with code {exitCode}: {excerpt}");
    }
}
