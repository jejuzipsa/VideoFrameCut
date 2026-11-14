namespace VideoFrameCut.Media;

/// <summary>
/// Normalizes paths supplied by the Windows picker, Explorer file-drop, or
/// pasted Explorer paths. Original Unicode names are never renamed or modified.
/// Reject invalid inputs explicitly rather than silently ignoring them.
/// </summary>
public static class VideoSourceSelection
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".mts", ".ts"
    };

    public sealed record Result(IReadOnlyList<string> Files, IReadOnlyList<string> Rejected);

    public static bool IsVideoFile(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        VideoExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Converts Explorer "Copy as path" text, including quoted full paths,
    /// multi-line selections and hidden direction markers, to distinct inputs.
    /// A literal filename is not altered except for its surrounding quotation
    /// marks/whitespace or invisible clipboard formatting at the edges.
    /// </summary>
    public static IReadOnlyList<string> ParsePathText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        return text.Split(new[] { "\r\n", "\n", "\r" },
                StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeInput)
            .Where(value => value.Length > 0)
            .ToArray();
    }

    private static string NormalizeInput(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var value = raw.Trim();
        // Windows Explorer and messaging apps sometimes surround Copy-as-path
        // values with directional formatting characters.
        const string invisible = "\u200E\u200F\u202A\u202B\u202C\u202D\u202E\u2066\u2067\u2068\u2069\uFEFF";
        value = value.Trim(invisible.ToCharArray()).Trim();
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '“' && value[^1] == '”')))
            value = value[1..^1];
        return value.Trim(invisible.ToCharArray()).Trim();
    }

    public static Result Resolve(IEnumerable<string> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var found = new List<string>();
        var rejected = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Accept(string file)
        {
            try
            {
                var full = Path.GetFullPath(file);
                if (!IsVideoFile(full))
                    rejected.Add("지원하지 않는 영상 확장자: " + Path.GetFileName(full));
                else if (!File.Exists(full))
                    rejected.Add("파일이 존재하지 않습니다: " + full);
                else if (seen.Add(full))
                    found.Add(full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                rejected.Add("파일 경로가 올바르지 않습니다: " + file);
            }
        }

        foreach (var raw in inputs)
        {
            var candidate = NormalizeInput(raw);
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            try
            {
                if (Directory.Exists(candidate))
                {
                    var count = found.Count;
                    foreach (var file in Directory.EnumerateFiles(candidate, "*", SearchOption.TopDirectoryOnly)
                                 .Where(IsVideoFile))
                        Accept(file);
                    if (count == found.Count)
                        rejected.Add("선택한 폴더에 추가할 동영상이 없습니다: " + candidate);
                }
                else
                {
                    Accept(candidate);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or
                PathTooLongException or UnauthorizedAccessException or IOException)
            {
                rejected.Add("경로를 확인할 수 없습니다: " + candidate + " (" + ex.Message + ")");
            }
        }

        return new Result(found, rejected);
    }
}
