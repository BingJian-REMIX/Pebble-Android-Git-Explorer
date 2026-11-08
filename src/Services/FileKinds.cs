namespace Pebble.Services;

public enum FileKind
{
    Text,
    Image,
    Archive,
    Font,
    Media,
    Unknown
}

/// <summary>按扩展名决定点击文件后的去向。</summary>
public static class FileKinds
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".jsonc", ".cs", ".js", ".mjs", ".ts", ".tsx", ".jsx",
        ".html", ".htm", ".css", ".scss", ".xml", ".xaml", ".yml", ".yaml", ".toml", ".ini", ".cfg",
        ".conf", ".log", ".sh", ".bat", ".cmd", ".ps1", ".py", ".java", ".kt", ".c", ".h", ".cpp",
        ".hpp", ".go", ".rs", ".rb", ".php", ".sql", ".gradle", ".properties", ".csproj", ".sln",
        ".editorconfig", ".gitignore", ".gitattributes", ".env", ".csv", ".bak"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg"
    };

    // 明确识别的归档格式；分卷（.001 等纯数字扩展名）由 IsNumericVolume 兜底归类。
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".gz", ".tgz", ".tar", ".bz2", ".xz", ".z01", ".z02", ".z03",
        ".r00", ".r01", ".r02"
    };

    private static readonly HashSet<string> FontExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ttf", ".otf", ".woff", ".woff2"
    };

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".mp4", ".m4a", ".m4v", ".ogg", ".wav", ".webm", ".mov", ".avi", ".flac"
    };

    public static FileKind Classify(string path)
    {
        var name = Path.GetFileName(path);

        // .gitignore 这类没有扩展名的点文件，按文本处理
        if (name.StartsWith('.') && name.IndexOf('.', 1) < 0) return FileKind.Text;

        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension)) return FileKind.Unknown;   // 无扩展名 → 弹「打开方式」

        if (TextExtensions.Contains(extension)) return FileKind.Text;
        if (ImageExtensions.Contains(extension)) return FileKind.Image;
        if (ArchiveExtensions.Contains(extension)) return FileKind.Archive;
        if (FontExtensions.Contains(extension)) return FileKind.Font;
        if (MediaExtensions.Contains(extension)) return FileKind.Media;
        if (IsNumericVolume(extension)) return FileKind.Archive;        // .001 / .z04 / .r03 等分卷

        return FileKind.Unknown;                                        // 未知扩展名 → 弹「打开方式」
    }

    /// <summary>纯数字扩展名（1~3 位，如 .001）大多是大体积压缩包的分卷，归为归档。</summary>
    private static bool IsNumericVolume(string extension)
    {
        var body = extension.TrimStart('.');
        return body.Length is >= 1 and <= 3 && body.All(char.IsDigit);
    }

    public static bool IsSvg(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".svg", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHtml(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMarkdown(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>是否需要进入「打开方式」弹窗：未知扩展名 / 无扩展名。</summary>
    public static bool NeedsOpenWith(string path) => Classify(path) == FileKind.Unknown;
}
