namespace Pebble.Services;

/// <summary>一个书签：名称 + 绝对路径。</summary>
public sealed record Bookmark(string Name, string Path);

/// <summary>快捷书签：内置三个 + 用户自定义，持久化到 Preferences（JSON）。</summary>
public static class BookmarkStore
{
    private const string Key = "bookmarks";

    public static string DownloadPath => Path.Combine(StorageAccess.ExternalRoot, "Download");
    public static string DesktopPath => Path.Combine(StorageAccess.ExternalRoot, "Desktop");

    public static IReadOnlyList<Bookmark> BuiltIn { get; } = new[]
    {
        new Bookmark("手机存储", StorageAccess.ExternalRoot),
        new Bookmark("下载 Download", DownloadPath),
        new Bookmark("桌面", DesktopPath)
    };

    public static List<Bookmark> Custom
    {
        get
        {
            var raw = Preferences.Default.Get(Key, string.Empty);
            if (string.IsNullOrWhiteSpace(raw)) return new List<Bookmark>();

            try
            {
                var entries = System.Text.Json.JsonSerializer.Deserialize<List<BookmarkEntry>>(raw);
                return (entries ?? new List<BookmarkEntry>())
                    .Select(e => new Bookmark(e.Name, e.Path))
                    .ToList();
            }
            catch (Exception)
            {
                return new List<Bookmark>();
            }
        }
    }

    public static void Add(string name, string path)
    {
        var list = Custom;
        list.RemoveAll(b => b.Path == path);
        list.Add(new Bookmark(name.Trim(), path));

        Save(list);
    }

    public static void Remove(string path)
    {
        var list = Custom;
        list.RemoveAll(b => b.Path == path);
        Save(list);
    }

    private static void Save(List<Bookmark> list)
    {
        var entries = list.Select(b => new BookmarkEntry(b.Name, b.Path)).ToList();
        Preferences.Default.Set(Key, System.Text.Json.JsonSerializer.Serialize(entries));
    }

    private sealed record BookmarkEntry(string Name, string Path);
}
