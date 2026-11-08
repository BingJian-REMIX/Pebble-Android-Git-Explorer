using Markdig;

namespace Pebble.Services;

/// <summary>把 Markdown 渲染成带主题配色的 HTML，供编辑器「预览」模式使用。</summary>
public static class MarkdownPreview
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    // 预览切换时同一份内容会被反复渲染，缓存上一次结果避免重复解析（性能优化）。
    private static string? _lastMarkdown;
    private static bool _lastDark;
    private static string? _lastHtml;

    public static string Build(string markdown, bool dark)
    {
        if (_lastMarkdown == markdown && _lastDark == dark && _lastHtml is not null)
            return _lastHtml;

        var body = Markdown.ToHtml(markdown ?? string.Empty, Pipeline);

        var (bg, fg, dim, codeBg, border, link) = dark
            ? ("#07090F", "#E9ECF5", "#8B94A8", "#10131C", "#1E2434", "#C4B5FD")
            : ("#F7F8FC", "#14161F", "#5F6779", "#EEF0FB", "#E3E8F0", "#5B4BE0");

        const string template = """
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1">
<style>
  html, body { margin: 0; padding: 16px 18px; background: __BG__; color: __FG__;
    font: 15px/1.7 -apple-system, system-ui, "Segoe UI", sans-serif; }
  h1, h2, h3, h4 { line-height: 1.3; margin: 1.1em 0 .5em; }
  h1 { font-size: 1.6em; border-bottom: 1px solid __BORDER__; padding-bottom: .3em; }
  h2 { font-size: 1.35em; border-bottom: 1px solid __BORDER__; padding-bottom: .25em; }
  h3 { font-size: 1.15em; }
  a { color: __LINK__; text-decoration: none; }
  code { background: __CODEBG__; padding: .15em .4em; border-radius: 5px;
    font: .92em ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
  pre { background: __CODEBG__; border: 1px solid __BORDER__; border-radius: 10px;
    padding: 14px 16px; overflow: auto; }
  pre code { background: transparent; padding: 0; }
  blockquote { margin: .8em 0; padding: .4em 1em; border-left: 3px solid __LINK__;
    color: __DIM__; background: __CODEBG__; border-radius: 0 8px 8px 0; }
  table { border-collapse: collapse; width: 100%; margin: .8em 0; }
  th, td { border: 1px solid __BORDER__; padding: 7px 10px; text-align: left; }
  img { max-width: 100%; border-radius: 10px; }
  hr { border: 0; border-top: 1px solid __BORDER__; margin: 1.4em 0; }
  ul, ol { padding-left: 1.4em; }
  li { margin: .25em 0; }
</style>
</head>
<body>
__BODY__
</body>
</html>
""";

        var html = template
            .Replace("__BG__", bg)
            .Replace("__FG__", fg)
            .Replace("__DIM__", dim)
            .Replace("__CODEBG__", codeBg)
            .Replace("__BORDER__", border)
            .Replace("__LINK__", link)
            .Replace("__BODY__", body);

        _lastMarkdown = markdown;
        _lastDark = dark;
        _lastHtml = html;
        return html;
    }
}
