using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Windows.Input;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using Pebble.Models;
using Pebble.Services;

namespace Pebble;

public partial class MainPage : ContentPage
{
    private readonly GitService _git = new();

    /// <summary>当前浏览根：可能是 App 沙盒里的 Git 仓库，也可能是手机存储。</summary>
    private string _root = string.Empty;
    private bool _isGitRepo;

    /// <summary>点了「打开手机存储」但用户还没去设置页授权，返回时要自动接上。</summary>
    private bool _pendingPhoneStorage;

    private string _relativePath = string.Empty; // 永远是 '/' 分隔，根目录为空串

    // 应用内剪贴板：跨目录粘贴，不碰系统剪贴板
    private readonly List<string> _clipboard = new();
    private bool _clipboardIsCut;

    // 抽屉里批量勾选时抑制"勾选即 stage"的副作用
    private bool _suppressStage;

    private IDispatcherTimer? _pressTimer;
    private bool _suppressNextTap;
    private bool _animateItems;

    /// <summary>是否显示以 . 开头的隐藏文件。</summary>
    private bool _showHidden;

    public ObservableCollection<FileItem> Items { get; } = new();
    public ObservableCollection<FileItem> Changes { get; } = new();

    public ICommand RefreshCommand { get; }

    public MainPage()
    {
        InitializeComponent();
        RefreshCommand = new Command(async () => await LoadAsync());
        BindingContext = this;
    }

    // ------------------------------------------------------------- 绑定属性

    private string _currentPath = "/";
    public string CurrentPath
    {
        get => _currentPath;
        private set => SetField(ref _currentPath, value);
    }

    private string _branchName = "—";
    public string BranchName
    {
        get => _branchName;
        private set => SetField(ref _branchName, value);
    }

    /// <summary>本地服务指示点：运行中为绿色，未启动跟随主题次要色。</summary>
    public Color ServerColor => LocalServerService.Shared.IsRunning
        ? Color.FromArgb("#22C55E")
        : Application.Current?.RequestedTheme == AppTheme.Dark
            ? Color.FromArgb("#4C5468")
            : Color.FromArgb("#9AA1B2");

    /// <summary>服务器运行状态文本（含局域网 IP），显示在顶栏。</summary>
    public string ServerStatus => LocalServerService.Shared.IsRunning
        ? $"服务运行中 · {LocalServerService.Shared.Url}"
        : string.Empty;

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (_isLoading == value) return;
            _isLoading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotLoading));
        }
    }

    public bool IsNotLoading => !_isLoading;

    private bool _isSelectionMode;
    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        private set
        {
            if (_isSelectionMode == value) return;
            _isSelectionMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBrowseMode));
            OnPropertyChanged(nameof(FabMargin));
            OnPropertyChanged(nameof(SelectionSummary));
        }
    }

    public bool IsBrowseMode => !_isSelectionMode;

    /// <summary>当前根是不是 Git 仓库。非仓库时 Git 抽屉、徽章、提交入口全部停用。</summary>
    public bool IsGitRepo
    {
        get => _isGitRepo;
        private set
        {
            if (_isGitRepo == value) return;
            _isGitRepo = value;
            OnPropertyChanged();
        }
    }

    /// <summary>是否显示隐藏文件（. 开头）。</summary>
    public bool ShowHidden
    {
        get => _showHidden;
        private set
        {
            if (_showHidden == value) return;
            _showHidden = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HiddenToggleLabel));
            OnPropertyChanged(nameof(HiddenDotColor));
        }
    }

    public string HiddenToggleLabel => _showHidden ? "隐藏文件：开" : "隐藏文件：关";

    /// <summary>隐藏文件开关的指示色：开启时紫色，关闭时灰色。</summary>
    public Color HiddenDotColor => _showHidden
        ? Color.FromArgb("#7C5CFF")
        : Application.Current?.RequestedTheme == AppTheme.Dark
            ? Color.FromArgb("#4C5468")
            : Color.FromArgb("#9AA1B2");

    /// <summary>顶栏根目录显示名。</summary>
    public string RootLabel => _root == StorageAccess.ExternalRoot
        ? "手机存储"
        : string.IsNullOrEmpty(_root) ? "/" : Path.GetFileName(_root.TrimEnd('/'));

    /// <summary>FAB 会压住底部粘贴条，剪贴板有内容时整体上移让位（而不是隐藏，否则提交入口就没了）。</summary>
    public Thickness FabMargin => _clipboard.Count == 0
        ? new Thickness(0, 0, 18, 18)
        : new Thickness(0, 0, 18, 96);

    public string SelectionSummary
    {
        get
        {
            var count = Items.Count(i => i.IsSelected);
            return count == 0 ? "选择项目" : $"已选 {count} 项";
        }
    }

    private int _changeCount;
    public int ChangeCount
    {
        get => _changeCount;
        private set
        {
            if (_changeCount == value) return;
            _changeCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasChanges));
        }
    }

    public bool HasChanges => _changeCount > 0;

    private string _changeSummary = "0 个文件待提交";
    public string ChangeSummary
    {
        get => _changeSummary;
        private set => SetField(ref _changeSummary, value);
    }

    public bool ClipboardVisible => _clipboard.Count > 0;

    public string ClipboardSummary =>
        _clipboard.Count == 0
            ? string.Empty
            : $"剪贴板：{_clipboard.Count} 项（{(_clipboardIsCut ? "移动" : "复制")}）";

    // ---------------------------------------------------------------- 生命周期

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // 从系统「所有文件访问权限」设置页返回时：拿到权限就自动切过去，
        // 不用用户再点一次按钮。
        if (_pendingPhoneStorage)
        {
            if (StorageAccess.HasAllFilesAccess())
            {
                _pendingPhoneStorage = false;
                SwitchRoot(StorageAccess.ExternalRoot);
                return;
            }

            _pendingPhoneStorage = false;
        }

        if (_root.Length > 0) return;

        IsLoading = true;
        try
        {
            // 先探原生库：Android 上缺 .so 或版本不匹配时，这里给出明确原因而不是后面莫名崩溃。
            if (!GitService.IsNativeLibraryAvailable())
            {
                await this.SafeAlertAsync("Git 原生库不可用",
                    $"{GitService.ProbeNativeLibrary()}\n\n本项目需要 {GitService.ExpectedNativeLibrary}，" +
                    "放在 Platforms/Android/libs/<abi>/ 或随平台包提供（详见 README）。", "好");
            }

            await TlsBootstrap.EnsureCaBundleAsync();

            // 启动路径：优先上次退出路径，其次全盘存储（有权限时），最后才回退演示仓库。
            var last = Preferences.Default.Get("last_path", string.Empty);
            if (last.Length > 0 && Directory.Exists(last))
            {
                var root = ResolveRootFromLastPath(last);
                var rel = ResolveRelativeFromLastPath(last);

                _root = root;
                _relativePath = rel;
                ApplyRepoContext(root);
                OnPropertyChanged(nameof(RootLabel));
                await LoadAsync();
                return;
            }

            if (StorageAccess.HasAllFilesAccess() && StorageAccess.ExternalRootExists())
            {
                SwitchRoot(StorageAccess.ExternalRoot);
                return;
            }

            var demoRepo = await TestRepoFactory.EnsureAsync();
            _root = demoRepo;
            _relativePath = string.Empty;
            ApplyRepoContext(demoRepo);
            OnPropertyChanged(nameof(RootLabel));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("仓库初始化失败", ex.Message, "好");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>根据根路径刷新仓库识别与分支名（不触发重新加载）。</summary>
    private void ApplyRepoContext(string root)
    {
        var repo = GitService.DiscoverRepository(root);
        IsGitRepo = repo is not null;

        BranchName = IsGitRepo
            ? GitService.GetBranchName(repo!)
            : "外部目录";
    }

    /// <summary>从上次记录的路径里恢复浏览根：能发现 Git 仓库就用仓库根，否则用外部存储根。</summary>
    private string ResolveRootFromLastPath(string lastPath)
    {
        var repo = GitService.DiscoverRepository(lastPath);
        if (repo is not null) return repo;

        // 在外部存储内部：回到 /storage/emulated/0 这个根，让 relativePath 承载剩余部分
        if (lastPath.StartsWith(StorageAccess.ExternalRoot, StringComparison.Ordinal))
            return StorageAccess.ExternalRoot;

        return Path.GetDirectoryName(lastPath) ?? lastPath;
    }

    private string ResolveRelativeFromLastPath(string lastPath)
    {
        var root = ResolveRootFromLastPath(lastPath);
        if (root == lastPath) return string.Empty;

        var rel = Path.GetRelativePath(root, lastPath);
        return rel.Replace(Path.DirectorySeparatorChar, '/').Trim('/');
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // 记录当前路径，下次启动恢复
        if (_root.Length > 0)
        {
            var full = string.IsNullOrEmpty(_relativePath)
                ? _root
                : Path.Combine(_root, _relativePath.Replace('/', Path.DirectorySeparatorChar));
            Preferences.Default.Set("last_path", full);
        }
    }

    /// <summary>切换浏览根（Git 仓库 / 手机存储），并重置到该根的顶层。</summary>
    private void SwitchRoot(string root)
    {
        _root = root;
        _relativePath = string.Empty;

        // 向上逐层查 .git，手机存储的子目录里也能识别出仓库
        var repo = GitService.DiscoverRepository(root);
        if (repo is not null) _root = repo;

        ApplyRepoContext(_root);

        OnPropertyChanged(nameof(RootLabel));
        OnPropertyChanged(nameof(ServerColor));
                OnPropertyChanged(nameof(ServerStatus));
        _ = LoadAsync();
    }

    // ---------------------------------------------------------------- 数据加载

    private async Task LoadAsync()
    {
        if (_root.Length == 0) return;

        try
        {
            // 仓库根按 Git 视角列（带 M/U/D）；其他路径只按纯文件系统列
            var items = IsGitRepo
                ? await _git.GetChildrenAsync(_root, _relativePath)
                : await FileOperations.ListAsync(_root, _relativePath, _showHidden);

            // Git 视角列出来的也会包含 .git，需要统一按 showHidden 过滤一次
            if (!_showHidden)
                items = items.Where(i => !i.Name.StartsWith('.')).ToList();

            // 只有真正换目录/刷新时才播放入场动画；
            // 滚动复用触发的 Loaded 会在窗口期内被 _animateItems 挡掉，避免来回闪。
            _animateItems = true;

            Items.Clear();
            foreach (var item in items) Items.Add(item);

            CurrentPath = _relativePath.Length == 0 ? RootLabel : "/" + _relativePath;
            OnPropertyChanged(nameof(SelectionSummary));

            _ = Task.Delay(1200).ContinueWith(_ =>
                MainThread.BeginInvokeOnMainThread(() => _animateItems = false));
            // 角标只影响一个数字，别让它拖慢目录切换：列表先出来，变更数稍后补上。
            if (IsGitRepo) _ = RefreshChangeCountAsync();
            else ChangeCount = 0;
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("读取失败", ex.Message, "好");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>列表项入场：从下方 14px 淡入，按索引错开，像文件"掉"进来。</summary>
    private async void OnItemLoaded(object? sender, EventArgs e)
    {
        if (!_animateItems) return;
        if (sender is not VisualElement view || view.BindingContext is not FileItem item) return;

        var index = Items.IndexOf(item);
        if (index < 0) return;

        view.Opacity = 0;
        view.TranslationY = 14;

        try
        {
            await Task.Delay(Math.Min(index, 10) * 34);

            await Task.WhenAll(
                view.FadeToAsync(1, 190, Easing.CubicOut),
                view.TranslateToAsync(0, 0, 240, Easing.CubicOut));
        }
        catch (Exception ex)
        {
            AppLog.Error("item-anim", ex);
            view.Opacity = 1;
            view.TranslationY = 0;
        }
    }

    private async Task RefreshChangeCountAsync()
    {
        try
        {
            var changes = await _git.GetWorkingChangesAsync(_root);
            ChangeCount = changes.Count;
        }
        catch (Exception ex)
        {
            AppLog.Error("change-count", ex);
        }
    }

    // ---------------------------------------------------------------- 点击 / 长按

    private static FileItem? ItemOf(object? sender)
        => sender is BindableObject bindable && bindable.BindingContext is FileItem item ? item : null;

    private async void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if (_suppressNextTap) { _suppressNextTap = false; return; }
        if (ItemOf(sender) is not FileItem item) return;

        // async void 一旦漏异常就会直接杀进程（AppDomain 兜底只记日志不阻止退出），
        // 所以整个分发链路都要兜住
        try
        {
            if (IsSelectionMode)
            {
                ToggleSelection(item);
                return;
            }

            if (item.IsDirectory)
            {
                _relativePath = item.RelativePath;
                await LoadAsync();
                return;
            }

            // M 状态的文件：先问编辑还是看差异
            if (IsGitRepo && item.GitStatus == GitFileStatus.Modified)
            {
                await OpenModifiedFileAsync(item);
                return;
            }

            // 按扩展名分流：代码进编辑器、图片进全屏预览、压缩/二进制交给系统
            switch (FileKinds.Classify(item.FullPath))
            {
                case FileKind.Text:
                    await OpenEditorAsync(item);
                    break;

                case FileKind.Image:
                    await OpenImagePreviewAsync(item);
                    break;

                default:
                    await OpenWithSystemAsync(item);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("item-tap", ex);
            await this.SafeAlertAsync("打开失败", ex.Message, "好");
        }
    }

    private async Task OpenModifiedFileAsync(FileItem item)
    {
        var action = await DisplayActionSheetAsync(item.Name, "取消", null, "编辑文件", "查看差异 (Diff)");

        if (action == "编辑文件") await OpenEditorAsync(item);
        else if (action == "查看差异 (Diff)") await OpenDiffAsync(item);
    }

    private async Task OpenDiffAsync(FileItem item)
    {
        IsLoading = true;
        try
        {
            var diff = await _git.GetFileDiffAsync(_root, item.RelativePath);
            if (string.IsNullOrWhiteSpace(diff))
            {
                await this.SafeAlertAsync("没有差异", "该文件没有未提交的修改。", "好");
                return;
            }

            await Navigation.PushAsync(new DiffPage(item.Name, diff));
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("读取差异失败", ex.Message, "好");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task OpenImagePreviewAsync(FileItem item)
    {
        // 传入同目录的兄弟图片，支持左右滑动切换
        var siblings = Items.Where(i => !i.IsDirectory && FileKinds.Classify(i.FullPath) == FileKind.Image).ToList();
        await Navigation.PushAsync(new ImagePreviewPage(item, siblings));
    }

    /// <summary>zip / 7z / 未知二进制：调起系统默认应用打开，而不是弹「不支持」。</summary>
    private async Task OpenWithSystemAsync(FileItem item)
    {
        try
        {
            var fullPath = item.FullPath;
            await Launcher.Default.OpenAsync(new OpenFileRequest(item.Name, new ReadOnlyFile(fullPath)));
        }
        catch (Exception ex)
        {
            AppLog.Error("open-with-system", ex);
            await this.SafeAlertAsync("无法打开",
                $"没有应用能处理这个文件：{item.Name}", "好");
        }
    }

    private void OnItemPressed(object? sender, PointerEventArgs e)
    {
        if (ItemOf(sender) is not FileItem item) return;
        if (IsSelectionMode) return;

        // PointerPressed 在按下的瞬间就触发了，所以这里只启动计时器，
        // 等 420ms 才判定为长按：目录弹操作菜单，文件进多选。
        // 短按直接交给 OnItemTapped（目录=进入）。
        _pressTimer?.Stop();
        _pressTimer = Dispatcher.CreateTimer();
        _pressTimer.Interval = TimeSpan.FromMilliseconds(420);
        _pressTimer.Tick += (_, _) =>
        {
            _pressTimer?.Stop();
            _suppressNextTap = true;   // 长按后松开还会冒一次 Tap，屏蔽掉

            if (item.IsDirectory) _ = ShowDirectoryMenuAsync(item);
            else EnterSelection(item);
        };
        _pressTimer.Start();
    }

    private async Task ShowDirectoryMenuAsync(FileItem item)
    {
        var action = await DisplayActionSheetAsync(item.Name, "取消", null,
            "新建子文件夹", "重命名", "删除", "选择项目");

        switch (action)
        {
            case "新建子文件夹":
                await CreateFolderAsync(item.RelativePath);
                break;

            case "重命名":
                await RenameItemAsync(item);
                break;

            case "删除":
                await DeleteItemsAsync(new[] { item });
                break;

            case "选择项目":
                EnterSelection(item);
                break;
        }
    }

    private void OnItemReleased(object? sender, PointerEventArgs e) => _pressTimer?.Stop();

    private void EnterSelection(FileItem item)
    {
        IsSelectionMode = true;
        item.IsSelected = true;
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private void ToggleSelection(FileItem item)
    {
        item.IsSelected = !item.IsSelected;
        OnPropertyChanged(nameof(SelectionSummary));

        if (Items.All(i => !i.IsSelected)) ExitSelection();
    }

    private void ExitSelection()
    {
        foreach (var item in Items) item.IsSelected = false;
        IsSelectionMode = false;
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private void OnExitSelectionTapped(object? sender, TappedEventArgs e) => ExitSelection();

    private void OnSelectAllTapped(object? sender, TappedEventArgs e)
    {
        foreach (var item in Items) item.IsSelected = true;
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private async Task OpenEditorAsync(FileItem item)
    {
        var editor = new EditorPage(item);
        editor.Disappearing += async (_, _) => await LoadAsync();
        await Navigation.PushAsync(editor);
    }

    // ------------------------------------------------------------ 本地静态服务器

    private async void OnServerTapped(object? sender, TappedEventArgs e)
    {
        var server = LocalServerService.Shared;

        if (!server.IsRunning)
        {
            try
            {
                var url = server.Start(_root, AppSettings.ServerPort);
                OnPropertyChanged(nameof(ServerColor));
                OnPropertyChanged(nameof(ServerStatus));

                await this.SafeAlertAsync("服务已启动",
                    $"电脑浏览器访问：\n{url}\n\n" +
                    $"根目录：{server.Root}\n\n" +
                    "再点一次 ⇄ 可以停止服务。", "好");
            }
            catch (Exception ex)
            {
                await this.SafeAlertAsync("启动失败", ex.Message, "好");
            }
            return;
        }

        var action = await DisplayActionSheetAsync("本地服务运行中", "取消", null,
            "复制地址", "在浏览器打开", "停止服务");

        switch (action)
        {
            case "复制地址":
                await Clipboard.Default.SetTextAsync(server.Url);
                await this.SafeAlertAsync("已复制", server.Url, "好");
                break;

            case "在浏览器打开":
                try { await Browser.Default.OpenAsync(server.Url, BrowserLaunchMode.SystemPreferred); }
                catch (Exception ex) { await this.SafeAlertAsync("打开失败", ex.Message, "好"); }
                break;

            case "停止服务":
                server.Stop();
                OnPropertyChanged(nameof(ServerColor));
                OnPropertyChanged(nameof(ServerStatus));
                break;
        }
    }

    // ---------------------------------------------------------------- 批量操作

    private List<FileItem> SelectedItems => Items.Where(i => i.IsSelected).ToList();

    private async Task CaptureSelectionAsync(bool isCut)
    {
        var selected = SelectedItems;
        if (selected.Count == 0) return;

        _clipboard.Clear();
        _clipboard.AddRange(selected.Select(i => i.FullPath));
        _clipboardIsCut = isCut;

        ExitSelection();
        NotifyClipboard();

        await this.SafeAlertAsync(isCut ? "已剪切" : "已复制",
            $"共 {_clipboard.Count} 项。进入目标目录后点底部的「粘贴到此处」。", "好");
    }

    private async void OnCopyTapped(object? sender, EventArgs e) => await CaptureSelectionAsync(false);

    private async void OnMoveTapped(object? sender, EventArgs e) => await CaptureSelectionAsync(true);

    /// <summary>多选状态下暂存选中文件（模块三第2点）。仅 Git 仓库中可用。</summary>
    private async void OnStageTapped(object? sender, EventArgs e)
    {
        var selected = SelectedItems;
        if (selected.Count == 0) return;

        if (!IsGitRepo)
        {
            await this.SafeAlertAsync("不是 Git 仓库", "当前目录不在 Git 仓库中，无法暂存。", "好");
            return;
        }

        // 拦截 .git 和目录（目录用 Commands.Stage 会递归暂存，风险大）
        var files = selected.Where(i => !i.IsDirectory && i.Name != ".git").Select(i => i.RelativePath).ToList();
        if (files.Count == 0)
        {
            await this.SafeAlertAsync("无可暂存项", "请选择文件（目录暂存请进入后再选）。", "好");
            return;
        }

        try
        {
            var count = await _git.StageFilesAsync(_root, files);
            await this.SafeAlertAsync("已暂存", $"已暂存 {count} 个文件。", "好");
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("暂存失败", ex.Message, "好");
        }

        ExitSelection();
        await LoadAsync();
    }

    /// <summary>解压当前选中的 zip 到当前目录（模块二第3点）。</summary>
    private async void OnExtractTapped(object? sender, EventArgs e)
    {
        var selected = SelectedItems;
        if (selected.Count == 0) return;

        var zips = selected.Where(i => !i.IsDirectory && FileKinds.Classify(i.FullPath) == FileKind.Binary
                                       && Path.GetExtension(i.FullPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                           .ToList();

        if (zips.Count == 0)
        {
            await this.SafeAlertAsync("没有 zip", "请选择 .zip 文件。", "好");
            return;
        }

        var targetDirectory = RepoPath.Combine(_root, _relativePath);

        // 解压前先统一问一次覆盖策略，避免逐个文件弹窗
        var policy = await DisplayActionSheetAsync(
            "解压遇到同名文件时", "取消", null,
            "覆盖", "保留两者", "跳过");

        ConflictAction action;
        if (policy == "保留两者") action = ConflictAction.KeepBoth;
        else if (policy == "跳过") action = ConflictAction.Skip;
        else if (policy == "覆盖") action = ConflictAction.Replace;
        else return;

        try
        {
            await Task.Run(() =>
            {
                foreach (var zip in zips)
                {
                    FileOperations.ExtractZip(zip.FullPath, targetDirectory, (_, _) => action);
                }
            });

            await this.SafeAlertAsync("已解压", $"解压 {zips.Count} 个压缩包到当前目录。", "好");
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("解压失败", ex.Message, "好");
        }

        ExitSelection();
        await LoadAsync();
    }

    private async void OnDeleteTapped(object? sender, EventArgs e)
    {
        var selected = SelectedItems;
        if (selected.Count == 0) return;

        // 防呆：禁止对 .git 或系统级隐藏目录动手
        var forbidden = selected.Where(IsProtectedPath).ToList();
        if (forbidden.Count > 0)
        {
            await this.SafeAlertAsync("操作被拦截",
                $"以下项目是系统关键目录，已阻止操作：\n{string.Join("\n", forbidden.Select(i => i.Name))}", "好");
            return;
        }

        var confirmed = await this.SafeConfirmAsync("删除", $"确定删除 {selected.Count} 项？该操作不可撤销。", "删除", "取消");
        if (!confirmed) return;

        try
        {
            await Task.Run(() =>
            {
                foreach (var item in selected) MoveToTrash(item.FullPath);
            });
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("删除失败", ex.Message, "好");
        }

        ExitSelection();
        await LoadAsync();
    }

    /// <summary>.git 目录、以及 /storage/emulated/0 下的 Android、DCIM 等系统目录禁止删除/移动/覆盖。</summary>
    private static bool IsProtectedPath(FileItem item)
    {
        var name = item.Name;

        if (name == ".git") return true;

        if (item.IsDirectory)
        {
            var full = item.FullPath.Replace('\\', '/');
            // 外部存储根下的系统目录
            if (full.StartsWith(StorageAccess.ExternalRoot + "/", StringComparison.Ordinal))
            {
                var rel = full[(StorageAccess.ExternalRoot.Length + 1)..];
                var top = rel.Split('/')[0];
                if (top is "Android" or "DCIM" or "Movies" or "Music" or "Pictures" or "Alarms" or "Ringtones" or "Notifications" or "Download" or "MIUI" or "Documents")
                    return true;
            }
        }

        return false;
    }

    /// <summary>删除进沙盒回收站而不是直接物理删除，给「清理回收站」留数据源。</summary>
    private static void MoveToTrash(string path)
    {
        var trash = Path.Combine(FileSystem.AppDataDirectory, ".trash");
        Directory.CreateDirectory(trash);

        var name = Path.GetFileName(path);
        var target = Path.Combine(trash, name);

        // 回收站里同名：追加时间戳
        if (File.Exists(target) || Directory.Exists(target))
            target = Path.Combine(trash, $"{Path.GetFileNameWithoutExtension(name)}-{DateTime.Now:HHmmss}{Path.GetExtension(name)}");

        if (Directory.Exists(path)) Directory.Move(path, target);
        else if (File.Exists(path)) File.Move(path, target);
    }

    private async void OnRenameTapped(object? sender, EventArgs e)
    {
        var selected = SelectedItems;
        if (selected.Count != 1)
        {
            await this.SafeAlertAsync("重命名", "一次只能重命名一项。", "好");
            return;
        }

        var item = selected[0];
        var newName = await DisplayPromptAsync("重命名", "新名称", "确定", "取消", item.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;

        try
        {
            await Task.Run(() => FileOperations.Rename(item.FullPath, newName.Trim()));
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("重命名失败", ex.Message, "好");
        }

        ExitSelection();
        await LoadAsync();
    }

    private async void OnCompressTapped(object? sender, EventArgs e)
    {
        var selected = SelectedItems;
        if (selected.Count == 0) return;

        var targetDirectory = RepoPath.Combine(_root, _relativePath);

        try
        {
            var zip = await Task.Run(() => FileOperations.Compress(selected.Select(i => i.FullPath).ToList(), targetDirectory));
            await this.SafeAlertAsync("已压缩", Path.GetFileName(zip), "好");
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("压缩失败", ex.Message, "好");
        }

        ExitSelection();
        await LoadAsync();
    }

    private async void OnPasteTapped(object? sender, EventArgs e)
    {
        if (_clipboard.Count == 0) return;

        var targetDirectory = RepoPath.Combine(_root, _relativePath);
        var isCut = _clipboardIsCut;
        var sources = _clipboard.ToList();

        // 先在同线程预扫描冲突，逐个决策（支持「应用到所有」），决策完再统一执行，避免后台线程弹窗
        var decisions = new Dictionary<string, ConflictAction>(StringComparer.Ordinal);
        ConflictAction? applyToAll = null;

        foreach (var source in sources)
        {
            var target = Path.Combine(targetDirectory, Path.GetFileName(source));
            if (!File.Exists(target) && !Directory.Exists(target)) continue;

            ConflictAction action;
            if (applyToAll is not null)
            {
                action = applyToAll.Value;
            }
            else
            {
                var choice = await ResolveConflictOnUiAsync(Path.GetFileName(source));
                if (choice is null) return;                 // 用户取消
                if (choice.ApplyToAll) applyToAll = choice.Action;
                action = choice.Action;
            }

            if (action == ConflictAction.Skip) continue;
            decisions[source] = action;
        }

        try
        {
            await Task.Run(() =>
            {
                foreach (var source in sources)
                {
                    var action = decisions.TryGetValue(source, out var d) ? d : ConflictAction.Replace;

                    if (isCut)
                        FileOperations.MoveToWithConflict(source, targetDirectory, (_, _) => action);
                    else
                        FileOperations.CopyToWithConflict(source, targetDirectory, (_, _) => action);
                }
            });
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("粘贴失败", ex.Message, "好");
        }
        finally
        {
            if (isCut) _clipboard.Clear();
            NotifyClipboard();
        }

        await LoadAsync();
    }

    private sealed record ConflictChoice(ConflictAction Action, bool ApplyToAll);

    /// <summary>弹冲突选择（调用方已在主线程）。用户取消返回 null。</summary>
    private async Task<ConflictChoice?> ResolveConflictOnUiAsync(string name)
    {
        var action = await DisplayActionSheetAsync(
            $"目标已存在：{name}", "取消", null,
            "替换", "保留两者", "跳过", "替换（应用到所有）");

        return action switch
        {
            "替换" => new ConflictChoice(ConflictAction.Replace, false),
            "保留两者" => new ConflictChoice(ConflictAction.KeepBoth, false),
            "跳过" => new ConflictChoice(ConflictAction.Skip, false),
            "替换（应用到所有）" => new ConflictChoice(ConflictAction.Replace, true),
            _ => null
        };
    }

    private void NotifyClipboard()
    {
        OnPropertyChanged(nameof(ClipboardVisible));
        OnPropertyChanged(nameof(ClipboardSummary));
        OnPropertyChanged(nameof(FabMargin));
    }

    // ---------------------------------------------------------------- 导航

    private async void OnUpTapped(object? sender, TappedEventArgs e) => await GoUpAsync();

    private async void OnToggleHiddenTapped(object? sender, TappedEventArgs e)
    {
        ShowHidden = !ShowHidden;
        await LoadAsync();
    }

    private async Task GoUpAsync()
    {
        if (_relativePath.Length == 0) return;

        var index = _relativePath.LastIndexOf('/');
        _relativePath = index < 0 ? string.Empty : _relativePath[..index];
        await LoadAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        if (SheetOverlay.IsVisible)
        {
            Dispatcher.Dispatch(() => _ = CloseSheetAsync());
            return true;
        }

        if (IsSelectionMode)
        {
            Dispatcher.Dispatch(ExitSelection);
            return true;
        }

        if (_relativePath.Length == 0) return base.OnBackButtonPressed();

        Dispatcher.Dispatch(() => _ = GoUpAsync());
        return true;
    }

    // ---------------------------------------------------------------- 新建

    private async void OnNewFabTapped(object? sender, TappedEventArgs e)
    {
        var action = await DisplayActionSheetAsync("新建", "取消", null, "文件", "文件夹");

        if (action == "文件") await CreateFileAsync();
        else if (action == "文件夹") await CreateFolderAsync();
    }

    private async Task CreateFileAsync()
    {
        var name = await DisplayPromptAsync("新建文件", "文件名，可用 src/a.cs 建子目录", "创建", "取消", "untitled.txt");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            await Task.Run(() =>
            {
                var directory = RepoPath.Combine(_root, _relativePath);
                var full = Path.Combine(directory, name.Trim().Replace('\\', '/'));

                var parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                if (!File.Exists(full)) File.WriteAllText(full, string.Empty);
            });
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("创建失败", ex.Message, "好");
        }

        await LoadAsync();
    }

    private async Task CreateFolderAsync() => await CreateFolderAsync(_relativePath);

    private async Task CreateFolderAsync(string insideRelativePath)
    {
        var name = await DisplayPromptAsync("新建文件夹", "文件夹名称", "创建", "取消", "新建文件夹");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            await Task.Run(() => FileOperations.CreateDirectory(_root, insideRelativePath, name));
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("创建失败", ex.Message, "好");
        }

        await LoadAsync();
    }

    /// <summary>长按目录菜单用的单个重命名。</summary>
    private async Task RenameItemAsync(FileItem item)
    {
        var newName = await DisplayPromptAsync("重命名", "新名称", "确定", "取消", item.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == item.Name) return;

        try
        {
            await Task.Run(() => FileOperations.Rename(item.FullPath, newName.Trim()));
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("重命名失败", ex.Message, "好");
        }

        await LoadAsync();
    }

    private async Task DeleteItemsAsync(IReadOnlyCollection<FileItem> items)
    {
        if (items.Count == 0) return;

        var confirmed = await this.SafeConfirmAsync("删除",
            $"确定删除 {items.Count} 项？该操作不可撤销。", "删除", "取消");
        if (!confirmed) return;

        try
        {
            await Task.Run(() =>
            {
                foreach (var item in items) FileOperations.Delete(item.FullPath);
            });
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("删除失败", ex.Message, "好");
        }

        await LoadAsync();
    }

    // ------------------------------------------------------------ 浏览位置

    private async void OnPhoneStorageTapped(object? sender, TappedEventArgs e)
    {
        if (!StorageAccess.HasAllFilesAccess())
        {
            var go = await this.SafeConfirmAsync("需要「所有文件访问」权限",
                "浏览手机文件需要该权限。\n\n接下来会打开系统设置页，请在列表里找到 Pebble 并允许。",
                "去授权", "取消");
            if (!go) return;

            // 记住意图：用户从设置页返回时 OnAppearing 会自动接上
            _pendingPhoneStorage = true;
            StorageAccess.RequestAllFilesAccess();
            return;
        }

        if (!StorageAccess.ExternalRootExists())
        {
            await this.SafeAlertAsync("找不到手机存储", $"目录不存在：{StorageAccess.ExternalRoot}", "好");
            return;
        }

        SwitchRoot(StorageAccess.ExternalRoot);
    }

    private async void OnSwitchRootTapped(object? sender, TappedEventArgs e)
    {
        var options = new List<string>();
        if (_root != StorageAccess.ExternalRoot) options.Add("打开手机存储");
        else options.Add("返回演示仓库");

        options.Add("书签…");
        options.Add("设置…");

        var action = await DisplayActionSheetAsync("浏览位置", "取消", null, options.ToArray());

        switch (action)
        {
            case "打开手机存储":
                OnPhoneStorageTapped(sender, e);
                break;

            case "返回演示仓库":
                var repo = await TestRepoFactory.EnsureAsync();
                SwitchRoot(repo);
                break;

            case "书签…":
                await ShowBookmarksAsync();
                break;

            case "设置…":
                await Navigation.PushAsync(new SettingsPage());
                break;
        }
    }

    private async Task ShowBookmarksAsync()
    {
        var builtIn = BookmarkStore.BuiltIn;
        var custom = BookmarkStore.Custom;

        var options = new List<string>();
        foreach (var b in builtIn) options.Add(b.Name);
        foreach (var b in custom) options.Add($"★ {b.Name}");
        options.Add("＋ 添加当前目录为书签");

        var choice = await DisplayActionSheetAsync("快捷书签", "取消", null, options.ToArray());
        if (choice is null) return;

        if (choice.StartsWith("★ ", StringComparison.Ordinal))
        {
            // 打开自定义书签
            var name = choice[2..];
            var bookmark = custom.FirstOrDefault(b => b.Name == name);
            if (bookmark is not null && Directory.Exists(bookmark.Path))
                SwitchRoot(bookmark.Path);
            return;
        }

        if (choice == "＋ 添加当前目录为书签")
        {
            var current = string.IsNullOrEmpty(_relativePath)
                ? _root
                : Path.Combine(_root, _relativePath.Replace('/', Path.DirectorySeparatorChar));

            var label = await DisplayPromptAsync("添加书签", "书签名称", "保存", "取消",
                Path.GetFileName(current.TrimEnd('/', '\\')));
            if (string.IsNullOrWhiteSpace(label)) return;

            BookmarkStore.Add(label, current);
            await this.SafeAlertAsync("已添加", $"书签「{label}」已保存。", "好");
            return;
        }

        // 内置书签
        var target = builtIn.FirstOrDefault(b => b.Name == choice);
        if (target is not null && Directory.Exists(target.Path))
            SwitchRoot(target.Path);
        else if (target is not null)
            await this.SafeAlertAsync("目录不存在", target.Path, "好");
    }

    // ---------------------------------------------------------------- Git 抽屉

    private async void OnGitFabTapped(object? sender, TappedEventArgs e)
    {
        // 不做缩放反馈：FAB 带阴影，缩放会每帧重算阴影，低端机上就是一团虚影。
        // 抽屉滑出本身就是最好的反馈。
        await OpenSheetAsync();
    }

    private async Task OpenSheetAsync()
    {
        var height = SheetPanel.Height > 0 ? SheetPanel.Height : 520;

        // 顺序很关键：先把它摆到屏幕外并设为透明，再显示，然后等布局跑完一帧才启动动画。
        // 直接在 IsVisible=true 的同一帧调 TranslateToAsync，Android 上会因为控件还没完成布局而"瞬移"，看不到过渡。
        SheetOverlay.Opacity = 0;
        SheetPanel.TranslationY = height;
        SheetOverlay.IsVisible = true;

        await Task.Delay(16);

        // 动画和数据加载并行：git 扫描可能要几百毫秒，串行做就会"卡一下才滑出来"。
        var slide = Task.WhenAll(
            SheetOverlay.FadeToAsync(1, 160, Easing.CubicOut),
            SheetPanel.TranslateToAsync(0, 0, 240, Easing.CubicOut));

        await RefreshChangesAsync();
        await slide;
    }

    private async Task CloseSheetAsync()
    {
        if (!SheetOverlay.IsVisible) return;

        var height = SheetPanel.Height > 0 ? SheetPanel.Height : 520;

        await Task.WhenAll(
            SheetOverlay.FadeToAsync(0, 130, Easing.CubicIn),
            SheetPanel.TranslateToAsync(0, height, 200, Easing.CubicIn));

        SheetOverlay.IsVisible = false;
    }

    private async void OnSheetBackdropTapped(object? sender, TappedEventArgs e) => await CloseSheetAsync();

    private async Task RefreshChangesAsync()
    {
        try
        {
            var changes = await _git.GetWorkingChangesAsync(_root);

            // 批量填充时别把每一行都当成用户手动勾选去 stage
            _suppressStage = true;
            Changes.Clear();
            foreach (var change in changes)
            {
                change.IsSelected = true;
                Changes.Add(change);
            }
            _suppressStage = false;

            ChangeSummary = Changes.Count == 0 ? "工作区干净" : $"{Changes.Count} 个文件待提交";
        }
        catch (Exception ex)
        {
            _suppressStage = false;
            await this.SafeAlertAsync("读取变更失败", ex.Message, "好");
        }
    }

    private void OnChangeChecked(object? sender, CheckedChangedEventArgs e)
    {
        if (_suppressStage) return;
        if (ItemOf(sender) is not FileItem item || _root.Length == 0) return;

        var stage = e.Value;
        var relativePath = item.RelativePath;

        _ = Task.Run(() =>
        {
            try
            {
                if (stage) _git.StageFile(_root, relativePath);
                else _git.UnstageFile(_root, relativePath);
            }
            catch (Exception)
            {
                // 勾选失败不打断交互，Commit 时会按勾选结果重新 stage。
            }
        });
    }

    private async void OnCommitClicked(object? sender, EventArgs e)
    {
        var message = CommitEntry.Text?.Trim();
        if (string.IsNullOrEmpty(message))
        {
            await this.SafeAlertAsync("缺少提交信息", "请先写一句 Commit message。", "好");
            return;
        }

        var selected = Changes.Where(c => c.IsSelected).Select(c => c.RelativePath).ToList();
        if (selected.Count == 0)
        {
            await this.SafeAlertAsync("没有选中文件", "至少勾选一个文件再提交。", "好");
            return;
        }

        IsLoading = true;
        CommitButton.IsEnabled = false;

        try
        {
            var result = await Task.Run(() => _git.CommitPaths(_root, message, selected));

            await this.SafeAlertAsync(result.Success ? "完成" : "未完成", result.Message, "好");

            if (result.Success)
            {
                CommitEntry.Text = string.Empty;
                BranchName = GitService.GetBranchName(_root);
                await CloseSheetAsync();
            }

            await LoadAsync();
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("提交失败", ex.Message, "好");
        }
        finally
        {
            IsLoading = false;
            CommitButton.IsEnabled = true;
        }
    }

    // ---------------------------------------------------------------- 工具

    private void SetField(ref string field, string value,
        [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(name);
    }
}
