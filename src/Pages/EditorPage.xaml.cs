using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.ApplicationModel;
using Pebble.Models;
using Pebble.Services;

namespace Pebble;

public partial class EditorPage : ContentPage
{
    private const long TruncateThreshold = 2L * 1024 * 1024;    // 2 MB：只读前 5000 行
    private const long ConfirmThreshold = 50L * 1024 * 1024;    // 50 MB：先问用户
    private const int MaxLines = 5000;

    private readonly FileItem _item;
    private readonly bool _readOnly;
    private string _content = string.Empty;
    private bool _truncated;
    private bool _isPreview;
    private bool _isMarkdown;
    private bool _openPreview;
    private bool _loaded;
    private double _zoom = 1.0;

    /// <summary>readOnly=true 用于"归档内预览"：内容来自缓存副本，禁止保存回写。</summary>
    public EditorPage(FileItem item, bool openPreview = false, bool readOnly = false)
    {
        InitializeComponent();

        _item = item;
        _openPreview = openPreview;
        _readOnly = readOnly;
        Title = item.Name;
        _isMarkdown = FileKinds.IsMarkdown(item.FullPath);
        ToggleButton.IsVisible = FileKinds.IsHtml(item.FullPath) || _isMarkdown;

        if (_readOnly)
        {
            SaveButton.IsVisible = false;
            ReadOnlyBadge.IsVisible = true;
        }

        // 每次 WebView 重新加载（源码/预览/主题重渲）后重新应用缩放
        View.Navigated += (_, _) => _ = ApplyZoom();
    }

    /// <summary>把当前缩放系数应用到 WebView 内容（body.style.zoom）。</summary>
    private async Task ApplyZoom()
    {
        try
        {
            await View.EvaluateJavaScriptAsync(
                "if(window.setZoom)setZoom(" + _zoom.ToString(CultureInfo.InvariantCulture) + ")");
        }
        catch (Exception ex)
        {
            AppLog.Error("zoom", ex);
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Application.Current!.RequestedThemeChanged += OnThemeChanged;

        if (_loaded) return;
        _loaded = true;

        await LoadAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        Application.Current!.RequestedThemeChanged -= OnThemeChanged;
    }

    private static bool Dark => Application.Current?.RequestedTheme == AppTheme.Dark;

    private void OnThemeChanged(object? sender, AppThemeChangedEventArgs e)
    {
        // 预览态下若是 Markdown，重新生成带主题配色的 HTML；
        // 源码态只切 CSS 类，不重载文档，避免丢掉未保存的编辑
        if (_isPreview)
        {
            if (_isMarkdown)
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try { View.Source = new HtmlWebViewSource { Html = MarkdownPreview.Build(_content, e.RequestedTheme == AppTheme.Dark) }; }
                    catch (Exception ex) { AppLog.Error("theme-md", ex); }
                });
            return;
        }
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try { await View.EvaluateJavaScriptAsync($"setTheme({(e.RequestedTheme == AppTheme.Dark ? "true" : "false")})"); }
            catch (Exception ex) { AppLog.Error("theme", ex); }
        });
    }

    private async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(_item.FullPath))
            {
                await this.SafeAlertAsync("文件不存在", _item.FullPath, "好");
                await Navigation.PopAsync();
                return;
            }

            var size = new FileInfo(_item.FullPath).Length;
            if (_readOnly)
            {
                MetaLabel.Text = $"{FileOperations.FormatSize(size)} · 归档内预览（只读）";
            }
            else
            {
                var parent = ParentDir(_item.RelativePath);
                MetaLabel.Text = parent.Length == 0
                    ? $"{FileOperations.FormatSize(size)} · 根目录"
                    : $"{FileOperations.FormatSize(size)} · {parent}";
            }

            if (size > ConfirmThreshold)
            {
                var sizeText = FileOperations.FormatSize(size);
                var go = await this.SafeConfirmAsync("文件过大",
                    $"该文件 {sizeText}，建议在电脑上处理。是否强制查看？", "强制查看", "取消");
                if (!go)
                {
                    await Navigation.PopAsync();
                    return;
                }
            }

            _truncated = size > TruncateThreshold;
            _content = _truncated ? ReadHead(_item.FullPath, MaxLines) : File.ReadAllText(_item.FullPath);

            // 扩展名说它是文本，内容却含 NUL —— 多半是二进制被误分类。
            // 直接按文本读会得到一个体积翻倍的字符串，再塞进 WebView 基本必 OOM。
            if (!_truncated && LooksBinary(_content))
            {
                await this.SafeAlertAsync("无法预览",
                    "这个文件虽然扩展名是文本类型，但内容包含二进制数据。", "好");
                await Navigation.PopAsync();
                return;
            }

            if (_truncated)
            {
                ReadOnlyBadge.IsVisible = true;
                SaveButton.IsEnabled = false;
                StatusLabel.Text = $"已截断，仅显示前 {MaxLines} 行";
            }
            else
            {
                StatusLabel.Text = $"{CountLines(_content)} 行";
            }

            RenderEditor();
            if (_openPreview && _isMarkdown) _ = EnterPreviewAsync();
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("打开失败", ex.Message, "好");
            await Navigation.PopAsync();
        }
    }

    private static string ReadHead(string path, int maxLines)
    {
        var builder = new StringBuilder();
        using var reader = new StreamReader(path);
        for (var i = 0; i < maxLines && !reader.EndOfStream; i++)
            builder.AppendLine(reader.ReadLine());
        return builder.ToString();
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        var lines = 1;
        foreach (var c in text) if (c == '\n') lines++;
        return lines;
    }

    /// <summary>取相对路径的父目录（去掉末尾的文件名），用于副标题展示位置而不重复文件名。</summary>
    private static string ParentDir(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return string.Empty;
        var normalized = relativePath.Replace('\\', '/');
        var idx = normalized.LastIndexOf('/');
        return idx < 0 ? string.Empty : normalized[..idx];
    }

    /// <summary>前 8KB 里出现 NUL 就认为是二进制。文本文件几乎不会有这个字节。</summary>
    private static bool LooksBinary(string text)
    {
        var limit = Math.Min(text.Length, 8192);
        for (var i = 0; i < limit; i++)
            if (text[i] == '\0') return true;

        return false;
    }

    private void RenderEditor()
    {
        _isPreview = false;
        ToggleButton.Text = "预览";
        View.Source = new HtmlWebViewSource
        {
            Html = CodePreview.BuildEditorHtml(_item.Name, _content, Dark, _truncated)
        };
    }

    private async Task EnterPreviewAsync()
    {
        if (!_isMarkdown) return;
        _isPreview = true;
        ToggleButton.Text = "代码";
        SaveButton.IsEnabled = false;
        try { View.Source = new HtmlWebViewSource { Html = MarkdownPreview.Build(_content, Dark) }; }
        catch (Exception ex) { AppLog.Error("enter-preview", ex); }
    }

    private async void OnToggleClicked(object? sender, EventArgs e)
    {
        if (!_isPreview)
        {
            // 切预览前先把手上的编辑抓回来，否则一进预览就丢了
            await CaptureAsync();
            _isPreview = true;
            ToggleButton.Text = "代码";
            SaveButton.IsEnabled = false;
            if (_isMarkdown)
                View.Source = new HtmlWebViewSource { Html = MarkdownPreview.Build(_content, Dark) };
            else
                View.Source = new HtmlWebViewSource { Html = _content };
        }
        else
        {
            RenderEditor();
            SaveButton.IsEnabled = !_truncated;
        }
    }

    private async Task CaptureAsync()
    {
        try
        {
            var raw = await View.EvaluateJavaScriptAsync("getContent()");
            _content = UnwrapScriptResult(raw);
        }
        catch (Exception ex)
        {
            AppLog.Error("capture", ex);
        }
    }

    /// <summary>
    /// WebView 返回的是 JSON 编码的字符串。不同平台有的带外层引号、有的只给内容部分
    /// （<c>\u003C</c> 这种转义序列），两种都要按 JSON 解一次。
    /// </summary>
    private static string UnwrapScriptResult(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        try
        {
            var json = raw[0] == '"' && raw[^1] == '"' ? raw : "\"" + raw + "\"";
            return JsonSerializer.Deserialize<string>(json) ?? raw;
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private void OnZoomIn(object? sender, EventArgs e) => _ = SetZoom("in");

    private void OnZoomOut(object? sender, EventArgs e) => _ = SetZoom("out");

    /// <summary>
    /// 先与 WebView 内的缩放（可能由捏合手势改变）对齐，再按按钮步进，
    /// 避免「按钮缩放」与「捏合缩放」两套数值错位导致跳变。
    /// </summary>
    private async Task SetZoom(string dir)
    {
        try
        {
            var raw = await View.EvaluateJavaScriptAsync("getZoom&&getZoom()");
            if (!string.IsNullOrEmpty(raw)
                && double.TryParse(raw.Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var j)
                && j > 0)
            {
                _zoom = j;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("zoom-sync", ex);
        }

        _zoom = Math.Clamp(dir == "in" ? _zoom * 1.2 : _zoom / 1.2, 0.5, 5.0);
        ZoomLabel.Text = $"{(int)(_zoom * 100)}%";
        await ApplyZoom();
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (_truncated)
        {
            await this.SafeAlertAsync("无法保存", "文件过大已被截断，保存会丢失后面的内容。", "好");
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            await CaptureAsync();
            await File.WriteAllTextAsync(_item.FullPath, _content);
            StatusLabel.Text = "已保存";
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("保存失败", ex.Message, "好");
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }
}
