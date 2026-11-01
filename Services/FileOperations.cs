using System.IO.Compression;

namespace Pebble.Services;

/// <summary>纯文件系统操作：复制 / 移动 / 删除 / 重命名 / 压缩。全部基于 System.IO，不依赖平台 API。</summary>
public static class FileOperations
{
    public static string CopyTo(string sourcePath, string targetDirectory)
    {
        var target = UniquePath(Path.Combine(targetDirectory, Path.GetFileName(sourcePath)));

        if (Directory.Exists(sourcePath)) CopyDirectory(sourcePath, target);
        else File.Copy(sourcePath, target, overwrite: false);

        return target;
    }

    public static string MoveTo(string sourcePath, string targetDirectory)
    {
        var target = UniquePath(Path.Combine(targetDirectory, Path.GetFileName(sourcePath)));

        if (Directory.Exists(sourcePath)) Directory.Move(sourcePath, target);
        else File.Move(sourcePath, target);

        return target;
    }

    public static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }

    public static string Rename(string path, string newName)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"无法解析父目录：{path}");
        var target = Path.Combine(directory, newName);

        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException($"同名项已存在：{newName}");

        if (Directory.Exists(path)) Directory.Move(path, target);
        else File.Move(path, target);

        return target;
    }

    /// <summary>压到 targetDirectory 下的 zip，返回 zip 的完整路径。</summary>
    public static string Compress(IReadOnlyList<string> paths, string targetDirectory)
    {
        var zipName = paths.Count == 1
            ? Path.GetFileName(paths[0]) + ".zip"
            : $"archive-{DateTime.Now:yyyyMMdd-HHmmss}.zip";

        var zipPath = UniquePath(Path.Combine(targetDirectory, zipName));

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        foreach (var path in paths)
        {
            var entryName = Path.GetFileName(path);

            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
                    archive.CreateEntryFromFile(file, $"{entryName}/{relative}");
                }
            }
            else if (File.Exists(path))
            {
                archive.CreateEntryFromFile(path, entryName);
            }
        }

        return zipPath;
    }

    /// <summary>文件返回字节数，目录递归求和。</summary>
    public static long SizeOf(string path)
    {
        if (File.Exists(path)) return new FileInfo(path).Length;
        if (!Directory.Exists(path)) return 0;

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { /* 单个文件读不到不影响整体统计 */ }
        }
        return total;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
    }

    /// <summary>目标已存在时追加 " (n)"，避免覆盖用户数据。</summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }

        throw new IOException("无法生成不冲突的目标文件名");
    }
}
