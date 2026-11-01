using System.Text;
using System.Text.Json;
using Microsoft.Maui.ApplicationModel;
using Pebble.Models;
using Pebble.Services;

namespace Pebble;

public partial class EditorPage : ContentPage
{
    private const long TruncateThreshold = 5L * 1024 * 1024;    // 5 MB：只读前 5000 行
    private const long ConfirmThreshold = 50L * 1024 * 1024;    // 50 MB：先问用户
    private const int MaxLines = 5000;

    private readonly FileItem _item;
    private string _content = string.Empty;
    private bool _truncated;
    private bool _isPreview;
    private bool _loaded;

    public EditorPage(FileItem item)
    {
        InitializeComponent();

        _item = item;
        Title = item.Name;
        NameLabel.Text = item.Name;
        ToggleButton.IsVisible = FileKinds.IsHtml(item.FullPath);
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
        // 只切 CSS 类，不重载文档，避免丢掉未保存的编辑
        if (_isPreview) return;
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
            MetaLabel.Text = $"{FileOperations.FormatSize(size)} · {_item.RelativePath}";

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

    private void RenderEditor()
    {
        _isPreview = false;
        ToggleButton.Text = "预览";
        View.Source = new HtmlWebViewSource
        {
            Html = CodePreview.BuildEditorHtml(_item.Name, _content, Dark, _truncated)
        };
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
