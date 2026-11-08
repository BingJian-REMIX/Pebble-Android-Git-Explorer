using System.IO.Compression;
using Pebble.Models;

namespace Pebble.Services;

/// <summary>复制/移动/解压遇到同名目标时的处理策略。</summary>
public enum ConflictAction
{
    Replace,      // 覆盖
    KeepBoth,     // 保留两者（自动重命名）
    Skip          // 跳过
}

/// <summary>纯文件系统操作：复制 / 移动 / 删除 / 重命名 / 压缩 / 解压。全部基于 System.IO，不依赖平台 API。</summary>
public static class FileOperations
{
    /// <summary>
    /// 列目录，不查 Git 状态，供浏览手机任意目录用。
    /// Android 上 Android/data、Android/obb 即使有全盘权限也读不了，
    /// 所以逐项 try，读不了的目录直接跳过而不是让整个列表失败。
    /// showHidden=false 时过滤掉以 . 开头的项。
    /// </summary>
    public static async Task<IReadOnlyList<FileItem>> ListAsync(string root, string relativePath, bool showHidden = false)
    {
        return await Task.Run(() =>
        {
            var directory = string.IsNullOrEmpty(relativePath)
                ? root
                : Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

            var list = new List<FileItem>();

            foreach (var path in SafeEnumerate(Directory.EnumerateDirectories(directory)))
            {
                var name = Path.GetFileName(path);
                if (!showHidden && name.StartsWith('.')) continue;

                list.Add(new FileItem
                {
                    Name = name,
                    FullPath = path,
                    IsDirectory = true,
                    RelativePath = ToRelative(root, path)
                });
            }

            foreach (var path in SafeEnumerate(Directory.EnumerateFiles(directory)))
            {
                var name = Path.GetFileName(path);
                if (!showHidden && name.StartsWith('.')) continue;

                list.Add(new FileItem
                {
                    Name = name,
                    FullPath = path,
                    IsDirectory = false,
                    RelativePath = ToRelative(root, path)
                });
            }

            return (IReadOnlyList<FileItem>)list
                .OrderBy(i => !i.IsDirectory)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });
    }

    /// <summary>在 root/relativeDirectory 下建文件夹，同名则自动追加序号。</summary>
    public static string CreateDirectory(string root, string relativeDirectory, string name)
    {
        var target = Path.Combine(root, relativeDirectory, name.Trim());
        if (Directory.Exists(target) || File.Exists(target)) target = UniquePath(target);

        Directory.CreateDirectory(target);
        return target;
    }

    private static IEnumerable<string> SafeEnumerate(IEnumerable<string> source)
    {
        using var iterator = source.GetEnumerator();

        while (true)
        {
            bool moved;
            try
            {
                moved = iterator.MoveNext();
            }
            catch (Exception)
            {
                yield break;   // 无权限的子目录，跳过即可
            }

            if (!moved) yield break;
            yield return iterator.Current;
        }
    }

    private static string ToRelative(string root, string full)
        => Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');

    public static string CopyTo(string sourcePath, string targetDirectory)
    {
        var target = UniquePath(Path.Combine(targetDirectory, Path.GetFileName(sourcePath)));

        if (Directory.Exists(sourcePath)) CopyDirectory(sourcePath, target);
        else File.Copy(sourcePath, target, overwrite: false);

        return target;
    }

    /// <summary>
    /// 复制到目标目录，遇到同名时回调 resolve 决定策略。
    /// resolve 返回 Replace 覆盖、KeepBoth 重命名、Skip 跳过（返回 null 表示跳过该项）。
    /// </summary>
    public static string? CopyToWithConflict(string sourcePath, string targetDirectory, Func<string, string, ConflictAction> resolve)
    {
        var target = Path.Combine(targetDirectory, Path.GetFileName(sourcePath));

        if (File.Exists(target) || Directory.Exists(target))
        {
            var action = resolve(sourcePath, target);
            switch (action)
            {
                case ConflictAction.Skip: return null;
                case ConflictAction.KeepBoth: target = UniquePath(target); break;
                case ConflictAction.Replace: break;
            }
        }

        if (Directory.Exists(sourcePath))
        {
            if (Directory.Exists(target) && File.Exists(target))
            {
                // 目标是文件、源是目录，覆盖时先删目标文件
                File.Delete(target);
            }
            CopyDirectory(sourcePath, target);
        }
        else
        {
            File.Copy(sourcePath, target, overwrite: true);
        }

        return target;
    }

    /// <summary>移动：目标已存在时回调决定策略。Skip 返回 null。目录移动用 Copy+Delete 实现，跨文件系统也能跑。</summary>
    public static string? MoveToWithConflict(string sourcePath, string targetDirectory, Func<string, string, ConflictAction> resolve)
    {
        var target = Path.Combine(targetDirectory, Path.GetFileName(sourcePath));

        if (File.Exists(target) || Directory.Exists(target))
        {
            var action = resolve(sourcePath, target);
            switch (action)
            {
                case ConflictAction.Skip: return null;
                case ConflictAction.KeepBoth: target = UniquePath(target); break;
                case ConflictAction.Replace: break;
            }
        }

        if (Directory.Exists(sourcePath))
        {
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);

            // 目录跨卷 Move 会抛 IOException，用 Copy + Delete 兜底
            try { Directory.Move(sourcePath, target); }
            catch (IOException)
            {
                CopyDirectory(sourcePath, target);
                Directory.Delete(sourcePath, recursive: true);
            }
        }
        else
        {
            if (File.Exists(target)) File.Delete(target);
            File.Move(sourcePath, target);
        }

        return target;
    }

    public static string MoveTo(string sourcePath, string targetDirectory)
    {
        var target = UniquePath(Path.Combine(targetDirectory, Path.GetFileName(sourcePath)));

        if (Directory.Exists(sourcePath)) Directory.Move(sourcePath, target);
        else File.Move(sourcePath, target);

        return target;
    }

    /// <summary>解压 zip 到 targetDirectory。同名时回调 resolve 决定策略（Skip 返回 null）。</summary>
    public static List<string> ExtractZip(string zipPath, string targetDirectory, Func<string, string, ConflictAction> resolve)
    {
        var extracted = new List<string>();

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            // 跳过目录项（下面会随文件自动创建）和路径穿越的恶意项
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var destination = Path.GetFullPath(Path.Combine(targetDirectory, entry.FullName));

            // 防 Zip Slip：解出的路径必须仍在 targetDirectory 内
            var prefix = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var dir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (File.Exists(destination))
            {
                var action = resolve(entry.FullName, destination);
                if (action == ConflictAction.Skip) continue;
                if (action == ConflictAction.KeepBoth) destination = UniquePath(destination);
                // Replace 直接覆盖（extractToFile overwrite=true）
            }

            entry.ExtractToFile(destination, overwrite: true);
            extracted.Add(destination);
        }

        return extracted;
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
        try
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
        catch (Exception)
        {
            // 外部存储里 Android/data 等目录会抛 UnauthorizedAccessException，
            // 这里返回 0 让界面显示"未知"，而不是把异常抛回 UI 线程导致闪退
            return 0;
        }
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
    public static string UniquePath(string path)
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
