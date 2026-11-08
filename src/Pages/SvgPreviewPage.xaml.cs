using Microsoft.Maui.ApplicationModel;
using Pebble.Models;
using Pebble.Services;

namespace Pebble;

/// <summary>
/// 用 WebView 渲染 SVG 图像（MAUI 的 Image 控件无法直接显示 SVG）。
/// 交由 SvgPreview 生成自包含 HTML，以 base64 &lt;img&gt; 嵌入，随主题切换背景。
/// </summary>
public partial class SvgPreviewPage : ContentPage
{
    private readonly FileItem _item;
    private const long MaxSize = 8L * 1024 * 1024;   // 8 MB：超过则引导用电脑查看

    public SvgPreviewPage(FileItem item)
    {
        InitializeComponent();

        _item = item;
        Title = item.Name;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Application.Current!.RequestedThemeChanged += OnThemeChanged;
        LoadSvg();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        Application.Current!.RequestedThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, AppThemeChangedEventArgs e) => LoadSvg();

    private void LoadSvg()
    {
        try
        {
            if (!File.Exists(_item.FullPath))
            {
                _ = this.SafeAlertAsync("文件不存在", _item.FullPath, "好");
                return;
            }

            var info = new FileInfo(_item.FullPath);
            if (info.Length > MaxSize)
            {
                _ = this.SafeAlertAsync("文件过大",
                    "SVG 超过 8MB，建议用电脑查看。", "好");
                return;
            }

            var svg = File.ReadAllText(_item.FullPath);
            var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
            View.Source = new HtmlWebViewSource { Html = SvgPreview.Build(svg, dark) };
        }
        catch (Exception ex)
        {
            _ = this.SafeAlertAsync("无法预览", ex.Message, "好");
        }
    }
}
