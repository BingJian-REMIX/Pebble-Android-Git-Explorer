using System.Collections.ObjectModel;
using System.Windows.Input;
using Pebble.Models;
using Pebble.Services;

namespace Pebble;

public partial class MainPage : ContentPage
{
    private readonly GitService _git = new();
    private string _repoRoot = string.Empty;
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
        if (_repoRoot.Length > 0) return;

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

            // 注意：这里不能申请存储权限。App 只用私有目录，而 Android 11+ 的
            // 「所有文件访问」会跳系统设置页，返回后再次触发 OnAppearing → 反复闪跳。
            // 将来要浏览外部目录时，在打开目录的那一刻再调 StoragePermission.EnsureAsync()。
            await TlsBootstrap.EnsureCaBundleAsync();

            _repoRoot = await TestRepoFactory.EnsureAsync();
            BranchName = GitService.GetBranchName(_repoRoot);

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

    // ---------------------------------------------------------------- 数据加载

    private async Task LoadAsync()
    {
        if (_repoRoot.Length == 0) return;

        try
        {
            var items = await _git.GetChildrenAsync(_repoRoot, _relativePath);

            // 只有真正换目录/刷新时才播放入场动画；
            // 滚动复用触发的 Loaded 会在窗口期内被 _animateItems 挡掉，避免来回闪。
            _animateItems = true;

            Items.Clear();
            foreach (var item in items) Items.Add(item);

            CurrentPath = _relativePath.Length == 0 ? "/" : "/" + _relativePath;
            OnPropertyChanged(nameof(SelectionSummary));

            _ = Task.Delay(1200).ContinueWith(_ =>
                MainThread.BeginInvokeOnMainThread(() => _animateItems = false));
            // 角标只影响一个数字，别让它拖慢目录切换：列表先出来，变更数稍后补上。
            _ = RefreshChangeCountAsync();
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
            var changes = await _git.GetWorkingChangesAsync(_repoRoot);
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

        var action = await DisplayActionSheetAsync(item.Name, "取消", null, "用编辑器打开", "查看属性");

        if (action == "用编辑器打开") await OpenEditorAsync(item);
        else if (action == "查看属性") await ShowPropertiesAsync(item);
    }

    private void OnItemPressed(object? sender, PointerEventArgs e)
    {
        if (IsSelectionMode) return;
        if (ItemOf(sender) is not FileItem item) return;

        _pressTimer?.Stop();
        _pressTimer = Dispatcher.CreateTimer();
        _pressTimer.Interval = TimeSpan.FromMilliseconds(420);
        _pressTimer.Tick += (_, _) =>
        {
            _pressTimer?.Stop();
            _suppressNextTap = true;   // 长按后松开还会冒一次 Tap，屏蔽掉
            EnterSelection(item);
        };
        _pressTimer.Start();
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

    private async Task ShowPropertiesAsync(FileItem item)
    {
        var size = await Task.Run(() => FileOperations.SizeOf(item.FullPath));
        var modified = File.Exists(item.FullPath) ? File.GetLastWriteTime(item.FullPath) : DateTime.MinValue;

        await this.SafeAlertAsync(item.Name,
            $"路径：{item.RelativePath}\n" +
            $"类型：{(item.IsDirectory ? "目录" : "文件")}\n" +
            $"大小：{FileOperations.FormatSize(size)}\n" +
            $"修改时间：{(modified == DateTime.MinValue ? "—" : modified.ToString("yyyy-MM-dd HH:mm"))}\n" +
            $"Git：{item.GitStatus}", "关闭");
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

        var targetDirectory = RepoPath.Combine(_repoRoot, _relativePath);

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

        var targetDirectory = RepoPath.Combine(_repoRoot, _relativePath);
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
        var name = await DisplayPromptAsync("新建文件", "文件名，可用 src/a.cs 建子目录", "创建", "取消", "untitled.txt");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var directory = RepoPath.Combine(_repoRoot, _relativePath);
            var full = Path.Combine(directory, name.Trim().Replace('\\', '/'));

            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            if (!File.Exists(full)) File.WriteAllText(full, string.Empty);
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("创建失败", ex.Message, "好");
        }

        await LoadAsync();
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
            var changes = await _git.GetWorkingChangesAsync(_repoRoot);

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
        if (ItemOf(sender) is not FileItem item || _repoRoot.Length == 0) return;

        var stage = e.Value;
        var relativePath = item.RelativePath;

        _ = Task.Run(() =>
        {
            try
            {
                if (stage) _git.StageFile(_repoRoot, relativePath);
                else _git.UnstageFile(_repoRoot, relativePath);
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
            var result = await Task.Run(() => _git.CommitPaths(_repoRoot, message, selected));

            await this.SafeAlertAsync(result.Success ? "完成" : "未完成", result.Message, "好");

            if (result.Success)
            {
                CommitEntry.Text = string.Empty;
                BranchName = GitService.GetBranchName(_repoRoot);
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
