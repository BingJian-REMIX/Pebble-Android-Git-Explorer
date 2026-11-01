using System.Collections.ObjectModel;
using System.Windows.Input;
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

            _root = await TestRepoFactory.EnsureAsync();
            BranchName = GitService.GetBranchName(_root);

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

    /// <summary>切换浏览根（Git 仓库 / 手机存储），并重置到该根的顶层。</summary>
    private void SwitchRoot(string root)
    {
        _root = root;
        _relativePath = string.Empty;
        IsGitRepo = GitService.IsRepository(root);

        BranchName = IsGitRepo
            ? GitService.GetBranchName(root)
            : "外部目录";

        OnPropertyChanged(nameof(RootLabel));
        OnPropertyChanged(nameof(ServerColor));
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
                : await FileOperations.ListAsync(_root, _relativePath);

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

        // 按扩展名分流：代码进编辑器、图片进全屏预览、其余给体积提示
        switch (FileKinds.Classify(item.FullPath))
        {
            case FileKind.Text:
                await OpenEditorAsync(item);
                break;

            case FileKind.Image:
                await Navigation.PushAsync(new ImagePreviewPage(item));
                break;

            default:
                await ShowBinaryInfoAsync(item);
                break;
        }
    }

    private async Task ShowBinaryInfoAsync(FileItem item)
    {
        var size = await Task.Run(() => FileOperations.SizeOf(item.FullPath));

        await this.SafeAlertAsync(item.Name,
            $"路径：{item.RelativePath}\n" +
            $"大小：{FileOperations.FormatSize(size)}\n\n" +
            "暂不支持直接预览。", "好");
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
                var url = server.Start(_root);
                OnPropertyChanged(nameof(ServerColor));

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

    private async void OnDeleteTapped(object? sender, EventArgs e)
    {
        var selected = SelectedItems;
        if (selected.Count == 0) return;

        var confirmed = await this.SafeConfirmAsync("删除", $"确定删除 {selected.Count} 项？该操作不可撤销。", "删除", "取消");
        if (!confirmed) return;

        try
        {
            await Task.Run(() =>
            {
                foreach (var item in selected) _git.DeleteFile(item.FullPath);
            });
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("删除失败", ex.Message, "好");
        }

        ExitSelection();
        await LoadAsync();
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

        try
        {
            await Task.Run(() =>
            {
                foreach (var source in _clipboard.ToList())
                {
                    if (isCut) FileOperations.MoveTo(source, targetDirectory);
                    else FileOperations.CopyTo(source, targetDirectory);
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

    private void NotifyClipboard()
    {
        OnPropertyChanged(nameof(ClipboardVisible));
        OnPropertyChanged(nameof(ClipboardSummary));
        OnPropertyChanged(nameof(FabMargin));
    }

    // ---------------------------------------------------------------- 导航

    private async void OnUpTapped(object? sender, TappedEventArgs e) => await GoUpAsync();

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
        var action = await DisplayActionSheetAsync("浏览位置", "取消", null,
            _root == StorageAccess.ExternalRoot ? "返回演示仓库" : "打开手机存储");

        if (action == "打开手机存储")
        {
            OnPhoneStorageTapped(sender, e);
        }
        else if (action == "返回演示仓库")
        {
            var repo = await TestRepoFactory.EnsureAsync();
            SwitchRoot(repo);
        }
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
