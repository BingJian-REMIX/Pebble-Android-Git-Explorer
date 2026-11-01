using System.Text.Json;

namespace Pebble.Services;

/// <summary>
/// 生成 WebView 用的代码编辑器页面：透明 textarea 负责输入，底层 pre 显示高亮。
/// 高亮是单遍扫描的极简 tokenizer（不依赖 CDN，离线可用，也不会因为正则互相嵌套而出错）。
/// </summary>
public static class CodePreview
{
    public static string BuildEditorHtml(string fileName, string content, bool dark, bool truncated)
    {
        var json = JsonSerializer.Serialize(content);
        var themeClass = dark ? "dark" : "light";
        var banner = truncated
            ? "<div id=\"banner\">文件过大，已截断显示</div>"
            : string.Empty;

        return Template
            .Replace("__THEME__", themeClass)
            .Replace("__TITLE__", System.Net.WebUtility.HtmlEncode(fileName))
            .Replace("__BANNER__", banner)
            .Replace("__SRC__", json);
    }

    private const string Template = """
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1">
<title>__TITLE__</title>
<style>
  :root {
    --bg: #F7F8FC; --fg: #14161F; --dim: #9AA1B2;
    --kw: #6D28D9; --str: #047857; --num: #B45309; --com: #94A3B8; --banner: #FEF3C7; --banner-fg: #92400E;
  }
  body.dark {
    --bg: #07090F; --fg: #E9ECF5; --dim: #4C5468;
    --kw: #C084FC; --str: #4ADE80; --num: #FBBF24; --com: #4C5468; --banner: #3F2D0A; --banner-fg: #FBBF24;
  }
  html, body { margin: 0; padding: 0; height: 100%; background: var(--bg); }
  #banner {
    position: fixed; top: 0; left: 0; right: 0; z-index: 9;
    padding: 7px 14px; font: 12px/1.4 -apple-system, system-ui, sans-serif;
    background: var(--banner); color: var(--banner-fg);
  }
  #wrap { position: absolute; top: 0; left: 0; right: 0; bottom: 0; }
  body.has-banner #wrap { top: 31px; }
  pre, textarea {
    position: absolute; top: 0; left: 0; right: 0; bottom: 0;
    margin: 0; padding: 14px 16px; box-sizing: border-box;
    font: 13px/1.55 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
    white-space: pre; overflow: auto; tab-size: 4;
    border: 0; outline: 0;
  }
  pre { color: var(--fg); background: transparent; }
  textarea {
    color: transparent; background: transparent; resize: none;
    caret-color: var(--fg); -webkit-text-fill-color: transparent;
  }
  textarea::selection { background: rgba(124, 92, 255, .35); -webkit-text-fill-color: transparent; }
  .t-k { color: var(--kw); font-weight: 600; }
  .t-s { color: var(--str); }
  .t-n { color: var(--num); }
  .t-c { color: var(--com); font-style: italic; }
  .t-i { color: var(--fg); }
</style>
</head>
<body class="__THEME__">
__BANNER__
<div id="wrap">
  <pre id="hl"></pre>
  <textarea id="ed" wrap="off" spellcheck="false" autocapitalize="off" autocomplete="off"></textarea>
</div>
<script>
const SOURCE = __SRC__;

const KEYWORDS = new Set(('public private protected internal static readonly const class struct interface enum record ' +
  'void var let function return if else for foreach while switch case default break continue new using namespace ' +
  'import export from def self True False None null true false async await try catch finally throw ' +
  'this base super extends implements package int string bool double float long char object dynamic ' +
  'override virtual abstract sealed in out ref params get set value where select yield').split(' '));

const isIdStart = c => /[A-Za-z_$]/.test(c);
const isId = c => /[A-Za-z0-9_$]/.test(c);
const isDigit = c => c >= '0' && c <= '9';

function esc(s) {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
function span(cls, text) {
  return '<span class="t-' + cls + '">' + esc(text) + '</span>';
}

function highlight(src) {
  let out = '', i = 0;
  const n = src.length;
  while (i < n) {
    const c = src[i];

    // 块注释
    if (c === '/' && src[i + 1] === '*') {
      let e = src.indexOf('*/', i + 2);
      e = e < 0 ? n : e + 2;
      out += span('c', src.slice(i, e)); i = e; continue;
    }
    // 行注释
    if (c === '/' && src[i + 1] === '/') {
      let e = src.indexOf('\n', i);
      e = e < 0 ? n : e;
      out += span('c', src.slice(i, e)); i = e; continue;
    }
    // 行首 # 注释（shell / yml / python）
    if (c === '#' && (i === 0 || src[i - 1] === '\n')) {
      let e = src.indexOf('\n', i);
      e = e < 0 ? n : e;
      out += span('c', src.slice(i, e)); i = e; continue;
    }
    // 字符串
    if (c === '"' || c === "'" || c === '`') {
      let j = i + 1;
      while (j < n && src[j] !== c) {
        if (src[j] === '\\') j++;
        j++;
      }
      j = Math.min(j + 1, n);
      out += span('s', src.slice(i, j)); i = j; continue;
    }
    // 数字
    if (isDigit(c) && (i === 0 || !isId(src[i - 1]))) {
      let j = i;
      while (j < n && /[0-9a-fA-FxX._]/.test(src[j])) j++;
      out += span('n', src.slice(i, j)); i = j; continue;
    }
    // 标识符 / 关键字
    if (isIdStart(c)) {
      let j = i;
      while (j < n && isId(src[j])) j++;
      const word = src.slice(i, j);
      out += span(KEYWORDS.has(word) ? 'k' : 'i', word);
      i = j; continue;
    }

    out += esc(c);
    i++;
  }
  return out;
}

const pre = document.getElementById('hl');
const ed = document.getElementById('ed');
const wrap = document.getElementById('wrap');

pre.innerHTML = highlight(SOURCE);
ed.value = SOURCE;

if (document.getElementById('banner')) document.body.classList.add('has-banner');

let syncing = false;
ed.addEventListener('input', () => {
  pre.innerHTML = highlight(ed.value);
  syncScroll();
});
function syncScroll() {
  if (syncing) return;
  syncing = true;
  pre.scrollTop = ed.scrollTop;
  pre.scrollLeft = ed.scrollLeft;
  syncing = false;
}
ed.addEventListener('scroll', syncScroll);
pre.addEventListener('scroll', () => {
  if (syncing) return;
  syncing = true;
  ed.scrollTop = pre.scrollTop;
  ed.scrollLeft = pre.scrollLeft;
  syncing = false;
});

window.getContent = () => ed.value;
window.setTheme = dark => document.body.classList.toggle('dark', dark);
window.focusEditor = () => ed.focus();
</script>
</body>
</html>
""";
}
