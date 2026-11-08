using System.Collections.ObjectModel;
using Microsoft.Maui.Storage;
using Pebble.Models;
using Pebble.Services;

namespace Pebble;

/// <summary>
/// 不解压预览：打开压缩包后逐层浏览内部文件夹 / 文件（支持多层钻取），不向磁盘解压。
/// 点文件夹 → 进入更深一层；点文本条目 → 抽到缓存用只读编辑器看；点图片 → 缓存后看图。
/// </summary>
public partial class ArchivePreviewPage : ContentPage
{
    private readonly FileItem _archive;
    private string _currentPath = string.Empty;   // 归档内当前文件夹，空串=根

    public ObservableCollection<ArchiveNode> Entries { get; } = new();

    private bool _isLoading = true;
    public bool Loading
    {
        get => _isLoading;
        set { if (_isLoading == value) return; _isLoading = value; OnPropertyChanged(); }
    }

    public string ArchiveType { get; private set; } = "—";
    public string Summary { get; private set; } = "正在读取归档…";

    public ArchivePreviewPage(FileItem archive)
    {
        InitializeComponent();

        _archive = archive;
        Title = archive.Name;
        BindingContext = this;

        TypeLabel.Text = ArchiveType;
        SummaryLabel.Text = Summary;
        PathLabel.Text = "（根目录）";
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (Entries.Count == 0) await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Loading = true;
        try
        {
            var nodes = await Task.Run(() => ArchiveService.ListChildren(_archive.FullPath, _currentPath));

            Entries.Clear();
            foreach (var n in nodes) Entries.Add(n);

            var files = nodes.Where(n => !n.IsDirectory).ToList();
            var totalSize = files.Sum(n => n.Size);

            ArchiveType = DetectType(_archive.FullPath);
            Summary = $"{nodes.Count} 项 · {files.Count} 个文件 · {FileOperations.FormatSize(totalSize)}";
        }
        catch (Exception ex)
        {
            Summary = "读取失败：" + ex.Message;
        }
        finally
        {
            Loading = false;
            TypeLabel.Text = ArchiveType;
            SummaryLabel.Text = Summary;
            PathLabel.Text = _currentPath.Length == 0 ? "（根目录）" : _currentPath;
            BackButton.IsEnabled = _currentPath.Length > 0;
            BackButton.Opacity = _currentPath.Length > 0 ? 1 : 0.4;
        }
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        if (_currentPath.Length == 0) return;
        var trimmed = _currentPath.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        _currentPath = slash < 0 ? string.Empty : trimmed[..(slash + 1)];
        EntryList.SelectedItem = null;
        await LoadAsync();
    }

    /// <summary>根据扩展名给出归档类型标签（分卷无标准扩展名时归为"分卷/其他"）。</summary>
    private static string DetectType(string path)
    {
        var ext = Path.GetExtension(path).ToUpperInvariant().TrimStart('.');
        return ext switch
        {
            "ZIP" => "ZIP 压缩包",
            "RAR" => "RAR 压缩包",
            "7Z" => "7Z 压缩包",
            "GZ" or "TGZ" => "GZIP 压缩包",
            "TAR" => "TAR 归档",
            "BZ2" => "BZIP2 压缩包",
            "XZ" => "XZ 压缩包",
            "" => "分卷压缩包",
            _ => ext + " 压缩包"
        };
    }

    private async void OnEntrySelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.Count == 0) return;
        if (e.CurrentSelection[0] is not ArchiveNode node) { EntryList.SelectedItem = null; return; }

        EntryList.SelectedItem = null; // 立刻取消选中，方便再次点同一项

        if (node.IsDirectory)
        {
            _currentPath = node.Key;   // 已含末尾斜杠
            await LoadAsync();
            return;
        }

        var kind = FileKinds.Classify(node.Name);
        if (kind == FileKind.Text) await PreviewTextEntryAsync(node);
        else if (kind == FileKind.Image) await PreviewImageEntryAsync(node);
        else await this.SafeAlertAsync("暂不支持预览", $"「{node.Name}」暂不支持应用内预览，请解压后再打开。", "好");
    }

    /// <summary>把单个文本条目抽到缓存目录，用只读编辑器打开（不污染用户目录）。</summary>
    private async Task PreviewTextEntryAsync(ArchiveNode node)
    {
        Loading = true;
        try
        {
            var dest = CachePathFor(node.Key);
            await Task.Run(() => ArchiveService.ExtractEntryToPath(_archive.FullPath, node.Key, dest));

            var tempItem = new FileItem
            {
                Name = Path.GetFileName(node.Key),
                FullPath = dest,
                RelativePath = "archive:" + node.Key,
                IsDirectory = false
            };

            await Navigation.PushAsync(new EditorPage(tempItem, readOnly: true));
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("预览失败", ex.Message, "好");
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>把单个图片条目抽到缓存目录，用图片预览打开。</summary>
    private async Task PreviewImageEntryAsync(ArchiveNode node)
    {
        Loading = true;
        try
        {
            var dest = CachePathFor(node.Key);
            await Task.Run(() => ArchiveService.ExtractEntryToPath(_archive.FullPath, node.Key, dest));

            var tempItem = new FileItem
            {
                Name = Path.GetFileName(node.Key),
                FullPath = dest,
                RelativePath = "archive:" + node.Key,
                IsDirectory = false
            };

            await Navigation.PushAsync(new ImagePreviewPage(tempItem));
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("预览失败", ex.Message, "好");
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>为归档内条目生成缓存路径：基础目录 + 哈希防同名冲突。</summary>
    private static string CachePathFor(string key)
    {
        var baseName = Path.GetFileName(key);
        if (string.IsNullOrEmpty(baseName)) baseName = "entry";

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(baseName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());

        var hash = Math.Abs(key.GetHashCode()) % 100000;
        var dir = Path.Combine(FileSystem.CacheDirectory, "archive_preview");
        return Path.Combine(dir, $"{hash}_{cleaned}");
    }
}
