using LibGit2Sharp;
using Microsoft.Maui.Storage;

namespace Pebble.Services;

/// <summary>
/// 首次启动时在 App 私有目录里造一个可离线演示的仓库：
/// 有 origin（本地 bare 仓库）、有已提交历史、有三种未提交状态。
/// </summary>
public static class TestRepoFactory
{
    public static string DefaultPath => Path.Combine(FileSystem.AppDataDirectory, "test-repo").Replace('\\', '/');

    public static string OriginPath => Path.Combine(FileSystem.AppDataDirectory, "test-repo-origin.git").Replace('\\', '/');

    public static Task<string> EnsureAsync() => Task.Run(() =>
    {
        var root = DefaultPath;
        if (GitService.IsRepository(root)) return root;

        Directory.CreateDirectory(root);
        Repository.Init(root);

        using (var repo = new Repository(root))
        {
            repo.Config.Set("user.name", "Pebble", ConfigurationLevel.Local);
            repo.Config.Set("user.email", "pebble@localhost", ConfigurationLevel.Local);

            WriteFile(root, "README.md", "# Pebble test repo\n\n这是 App 私有目录里的离线演示仓库。\n");
            WriteFile(root, "src/main.cs", "Console.WriteLine(\"hello\");\n");
            WriteFile(root, "src/utils.cs", "static int Add(int a, int b) => a + b;\n");
            WriteFile(root, "docs/notes.md", "- note 1\n");
            WriteFile(root, "docs/guide.md", "- guide\n");

            Commands.Stage(repo, "*");
            var signature = new Signature("Pebble", "pebble@localhost", DateTimeOffset.Now);
            repo.Commit("init: seed test repo", signature, signature);

            // 本地 bare 仓库当 origin，这样 Commit & Push 全链路不用联网也能跑。
            if (!Repository.IsValid(OriginPath))
            {
                Repository.Init(OriginPath, isBare: true);
            }
            if (repo.Network.Remotes["origin"] is null)
            {
                repo.Network.Remotes.Add("origin", OriginPath);
            }

            var head = repo.Head;
            repo.Branches.Update(head,
                b => b.Remote = "origin",
                b => b.UpstreamBranch = head.CanonicalName);

            try { repo.Network.Push(repo.Head); }
            catch (Exception) { /* 推送失败不影响本地演示 */ }

            // 制造 Modified / New / Deleted 三种状态，让首屏就能看到徽章。
            WriteFile(root, "src/main.cs", "Console.WriteLine(\"hello, pebble\");\n");
            WriteFile(root, "notes/todo.md", "- [ ] try commit & push\n");
            File.Delete(Path.Combine(root, "docs", "notes.md"));
        }

        return root;
    });

    private static void WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
    }
}
