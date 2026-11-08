using System.IO;
using Microsoft.Maui.Controls;
using Pebble.Models;
using Pebble.Services;

namespace Pebble;

public partial class FontPreviewPage : ContentPage
{
    private readonly FileItem _item;

    public FontPreviewPage(FileItem item)
    {
        InitializeComponent();
        _item = item;
        Title = item.Name;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        string html;
        var size = new FileInfo(_item.FullPath).Length;

        // 字体内嵌进 HTML 会按 base64 翻倍，超大字体直接进 WebView 有 OOM 风险，
        // 超过 6 MB 引导用户走系统打开预览。
        if (size > 6L * 1024 * 1024)
        {
            html = "<html><body style='font-family:sans-serif;padding:24px;color:#8B94A8'>" +
                   "字体文件过大（" + FileOperations.FormatSize(size) + "），应用内预览会占用过多内存，<br>" +
                   "请用「打开方式 → 系统打开」预览。</body></html>";
        }
        else
        {
            try
            {
                var bytes = File.ReadAllBytes(_item.FullPath);
                var b64 = Convert.ToBase64String(bytes);
                html = BuildFontHtml(b64, Dark);
            }
            catch (Exception ex)
            {
                html = "<html><body style='font-family:sans-serif;padding:24px;color:#EF4444'>" +
                       "读取失败：" + ex.Message + "</body></html>";
            }
        }

        View.Source = new HtmlWebViewSource { Html = html };
    }

    private static bool Dark => Application.Current?.RequestedTheme == AppTheme.Dark;

    private static string BuildFontHtml(string base64, bool dark)
    {
        var (bg, fg, dim, card) = dark
            ? ("#07090F", "#E9ECF5", "#8B94A8", "#10131C")
            : ("#F7F8FC", "#14161F", "#5F6779", "#FFFFFF");

        const string glyphs = "ABCDEFGHIJKLMNOPQRSTUVWXYZ abcdefghijklmnopqrstuvwxyz 0123456789 &@#%()[]{}<>/\\.,:;!?\"'*-+=_";

        var samples = new System.Text.StringBuilder();
        foreach (var (size, label) in new[] { ("64px", "大字"), ("40px", "标题"), ("28px", "正文大"), ("18px", "正文"), ("14px", "注释") })
        {
            samples.Append(
                "<div style='margin:14px 0'>" +
                "<div style='font-size:11px;color:" + dim + ";margin-bottom:4px'>" + label + " · " + size + "</div>" +
                "<div style='font-family:PebbleFont;font-size:" + size + ";line-height:1.3'>" +
                "永和九年岁在癸丑暮春之初 · The quick brown fox 0123456789</div></div>");
        }

        var grid = new System.Text.StringBuilder();
        foreach (var ch in glyphs)
        {
            var t = ch == ' ' ? "&nbsp;" : ch.ToString();
            grid.Append("<span style='display:inline-block;min-width:1.6em;text-align:center;" +
                        "font-family:PebbleFont;font-size:26px;padding:8px 0'>" + t + "</span>");
        }

        const string template = """
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1">
<style>
  html, body { margin:0; padding:16px 18px; background:__BG__; color:__FG__;
    font:15px/1.6 -apple-system, system-ui, sans-serif; }
  @font-face { font-family:'PebbleFont'; src:url(data:font/ttf;base64,__B64__) format('truetype'); }
  .card { background:__CARD__; border-radius:14px; padding:16px 18px; margin-bottom:14px; }
  .grid { background:__CARD__; border-radius:14px; padding:14px; line-height:2; word-spacing:2px; }
</style>
</head>
<body>
  <div class="card">__SAMPLES__</div>
  <div class="grid">__GRID__</div>
  <div style="color:__DIM__;font-size:11px;margin-top:8px">字形覆盖：字母 / 数字 / 标点</div>
</body>
</html>
""";

        return template
            .Replace("__BG__", bg)
            .Replace("__FG__", fg)
            .Replace("__DIM__", dim)
            .Replace("__CARD__", card)
            .Replace("__B64__", base64)
            .Replace("__SAMPLES__", samples.ToString())
            .Replace("__GRID__", grid.ToString());
    }
}
