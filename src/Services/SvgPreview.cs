using System.Text;

namespace Pebble.Services;

/// <summary>
/// 生成 WebView 用的 SVG 预览页：与 CodePreview / MarkdownPreview 同构（自包含 HTML、离线可用、随深浅色切换背景）。
/// 关键修复：之前用 base64 &lt;img&gt; 只给 max-width/max-height，遇到 width="100%" 类无固有尺寸的 SVG 会塌成 0 高度 → 空白。
/// 现改为**内联原始 SVG**，并用 <c>width:100%;height:100%</c> 强制铺满视口（有 viewBox 时 preserveAspectRatio 自动居中不变形），
/// 保证任何 SVG 都能渲染出来。
/// </summary>
public static class SvgPreview
{
    public static string Build(string svg, bool dark)
    {
        var bg = dark ? "#07090F" : "#F7F8FC";
        var body = (svg ?? string.Empty).Trim().TrimStart('\uFEFF');

        const string template = """
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>
  html, body { margin:0; padding:0; height:100%; box-sizing:border-box; background:__BG__; }
  body { display:flex; align-items:center; justify-content:center; padding:12px; }
  svg { display:block; width:100%; height:100%; max-width:100%; max-height:100%; }
</style>
</head>
<body>
__SVG__
</body>
</html>
""";

        return template
            .Replace("__BG__", bg)
            .Replace("__SVG__", body);
    }
}
