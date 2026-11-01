namespace Pebble.Services;

public enum FileKind
{
    Text,
    Image,
    Binary
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
        ".editorconfig", ".gitignore", ".gitattributes", ".env", ".csv", ".svg"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"
    };

    public static FileKind Classify(string path)
    {
        var name = Path.GetFileName(path);

        // .gitignore 这类没有扩展名的点文件，按文本处理
        if (name.StartsWith('.') && name.IndexOf('.', 1) < 0) return FileKind.Text;

        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension)) return FileKind.Binary;

        if (TextExtensions.Contains(extension)) return FileKind.Text;
        if (ImageExtensions.Contains(extension)) return FileKind.Image;

        return FileKind.Binary;
    }

    public static bool IsHtml(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);
    }
}
