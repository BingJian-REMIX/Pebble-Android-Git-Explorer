using System.Net;
using Pebble.Services;

namespace Pebble;

public partial class DiffPage : ContentPage
{
    public DiffPage(string title, string diffText)
    {
        InitializeComponent();

        Title = title;
        TitleLabel.Text = title;
        MetaLabel.Text = "工作区改动（相对 HEAD）";

        View.Source = new HtmlWebViewSource { Html = BuildDiffHtml(diffText) };
    }

    /// <summary>
    /// diff 文本注入 HTML：+ 行绿底、- 行红底、@@ 行蓝底，等宽字体，适配明暗主题。
    /// 逐行按首字符分类；HTML 转义用 WebUtility.HtmlEncode。
    /// </summary>
    private static string BuildDiffHtml(string diff)
    {
        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;

        var css = dark
            ? ":root{--bg:#07090F;--fg:#E9ECF5;--add:#0E2A1A;--add-fg:#7EE2A8;--del:#2A0E13;--del-fg:#F59E9E;--hdr:#12233B;--hdr-fg:#9CC3F0;--meta:#4C5468}"
            : ":root{--bg:#F7F8FC;--fg:#14161F;--add:#E6F6EC;--add-fg:#137A4A;--del:#FDECEC;--del-fg:#C0392B;--hdr:#EAF1FB;--hdr-fg:#1D4ED8;--meta:#9AA1B2}";

        var body = new System.Text.StringBuilder();

        foreach (var rawLine in diff.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var cls = line switch
            {
                _ when line.StartsWith("+++") => "meta",
                _ when line.StartsWith("---") => "meta",
                _ when line.StartsWith("@@") => "hdr",
                _ when line.StartsWith("+") => "add",
                _ when line.StartsWith("-") => "del",
                _ => "ctx"
            };

            var escaped = WebUtility.HtmlEncode(line.Length == 0 ? " " : line);
            body.Append($"<div class=\"{cls}\">{escaped}</div>");
        }

        return $$"""
<!DOCTYPE html>
<html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>
  {{css}}
  html,body{margin:0;padding:0;background:var(--bg);color:var(--fg)}
  body{font:12px/1.6 ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;padding:10px 0}
  div{padding:1px 14px;white-space:pre-wrap;word-break:break-all}
  .add{background:var(--add);color:var(--add-fg)}
  .del{background:var(--del);color:var(--del-fg)}
  .hdr{background:var(--hdr);color:var(--hdr-fg);font-weight:600}
  .meta{color:var(--meta)}
  .ctx{color:var(--fg)}
</style></head><body>
{{body}}
</body></html>
""";
    }
}
