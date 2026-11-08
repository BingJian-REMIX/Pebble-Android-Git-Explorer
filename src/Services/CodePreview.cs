using System.IO;
using System.Text.Json;

namespace Pebble.Services;

/// <summary>
/// 生成 WebView 用的代码编辑器页面：透明 textarea 负责输入，底层 pre 显示高亮。
/// 高亮是单遍扫描的极简 tokenizer（不依赖 CDN，离线可用，也不会因为正则互相嵌套而出错）。
/// </summary>
public static class CodePreview
{
    public static string BuildEditorHtml(string fileName, string content, bool dark, bool truncated, string? lang = null)
    {
        var json = JsonSerializer.Serialize(content);
        var themeClass = dark ? "dark" : "light";
        var banner = truncated
            ? "<div id=\"banner\">文件过大，已截断显示</div>"
            : string.Empty;

        // 不显式指定语言时，按文件名后缀猜（bat/cmd 用批处理高亮）
        if (lang is null)
        {
            var ext = Path.GetExtension(fileName);
            if (ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
                lang = "batch";
        }

        return Template
            .Replace("__THEME__", themeClass)
            .Replace("__TITLE__", System.Net.WebUtility.HtmlEncode(fileName))
            .Replace("__BANNER__", banner)
            .Replace("__LANG__", lang ?? string.Empty)
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
const LANG = "__LANG__";

const KEYWORDS = new Set(('public private protected internal static readonly const class struct interface enum record ' +
  'void var let function return if else for foreach while switch case default break continue new using namespace ' +
  'import export from def self True False None null true false async await try catch finally throw ' +
  'this base super extends implements package int string bool double float long char object dynamic ' +
  'override virtual abstract sealed in out ref params get set value where select yield').split(' '));

const isIdStart = c => /[A-Za-z_$]/.test(c);
const isId = c => /[A-Za-z0-9_$]/.test(c);
const isDigit = c => c >= '0' && c <= '9';

function highlightBatch(src) {
  const KEYWORDS = new Set(('echo set if else goto call for in do shift title cd dir del delete copy move rem pause exit start setlocal endlocal cls color choice find findstr type off on not eq neq equ lss leq gtr geq defined errorlevel pushd popd attrib ren xcopy mkdir md rd rmdir timeout ver echo.' ).split(' '));
  const lines = src.split('\n');
  const parts = [];
  for (const line of lines) {
    const trimmed = line.replace(/^\s+/, '');
    // 行注释：REM 或 ::
    if (/^rem\b/i.test(trimmed) || /^::/.test(trimmed)) { parts.push(span('c', line) + '\n'); continue; }
    // 标签：行首 :label
    if (/^:[\w]/.test(trimmed)) { parts.push(span('k', line) + '\n'); continue; }
    let row = '', i = 0, n = line.length;
    while (i < n) {
      const c = line[i];
      if (c === '"') { let j = i + 1; while (j < n && line[j] !== '"') j++; j = Math.min(j + 1, n); row += span('s', line.slice(i, j)); i = j; continue; }
      if (c === '%') { let j = i + 1; while (j < n && line[j] !== '%') j++; j = Math.min(j + 1, n); row += span('n', line.slice(i, j)); i = j; continue; }
      const bq = line.indexOf('!', i + 1);
      if (c === '!' && bq >= 0) { row += span('n', line.slice(i, bq + 1)); i = bq + 1; continue; }
      if (isIdStart(c)) {
        let j = i; while (j < n && isId(line[j])) j++;
        const w = line.slice(i, j);
        const before = i > 0 ? line[i - 1] : ' ';
        if (/\s/.test(before) && KEYWORDS.has(w.toLowerCase())) row += span('k', w);
        else row += span('i', w);
        i = j; continue;
      }
      row += esc(c); i++;
    }
    parts.push(row + '\n');
  }
  return parts.join('');
}

function highlightDispatch(src) {
  return LANG === 'batch' ? highlightBatch(src) : highlight(src);
}

function esc(s) {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
function span(cls, text) {
  return '<span class="t-' + cls + '">' + esc(text) + '</span>';
}

function highlight(src) {
  const parts = [];
  let i = 0;
  const n = src.length;
  while (i < n) {
    const c = src[i];

    // 块注释
    if (c === '/' && src[i + 1] === '*') {
      let e = src.indexOf('*/', i + 2);
      e = e < 0 ? n : e + 2;
      parts.push(span('c', src.slice(i, e))); i = e; continue;
    }
    // 行注释
    if (c === '/' && src[i + 1] === '/') {
      let e = src.indexOf('\n', i);
      e = e < 0 ? n : e;
      parts.push(span('c', src.slice(i, e))); i = e; continue;
    }
    // 行首 # 注释（shell / yml / python）
    if (c === '#' && (i === 0 || src[i - 1] === '\n')) {
      let e = src.indexOf('\n', i);
      e = e < 0 ? n : e;
      parts.push(span('c', src.slice(i, e))); i = e; continue;
    }
    // 字符串
    if (c === '"' || c === "'" || c === '`') {
      let j = i + 1;
      while (j < n && src[j] !== c) {
        if (src[j] === '\\') j++;
        j++;
      }
      j = Math.min(j + 1, n);
      parts.push(span('s', src.slice(i, j))); i = j; continue;
    }
    // 数字
    if (isDigit(c) && (i === 0 || !isId(src[i - 1]))) {
      let j = i;
      while (j < n && /[0-9a-fA-FxX._]/.test(src[j])) j++;
      parts.push(span('n', src.slice(i, j))); i = j; continue;
    }
    // 标识符 / 关键字
    if (isIdStart(c)) {
      let j = i;
      while (j < n && isId(src[j])) j++;
      const word = src.slice(i, j);
      parts.push(span(KEYWORDS.has(word) ? 'k' : 'i', word));
      i = j; continue;
    }

    parts.push(esc(c));
    i++;
  }
  return parts.join('');
}

const pre = document.getElementById('hl');
const ed = document.getElementById('ed');
const wrap = document.getElementById('wrap');

pre.innerHTML = highlightDispatch(SOURCE);
ed.value = SOURCE;

if (document.getElementById('banner')) document.body.classList.add('has-banner');

let syncing = false;
ed.addEventListener('input', () => {
  pre.innerHTML = highlightDispatch(ed.value);
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

// 缩放统一由 JS 维护，避免依赖 MAUI 手势识别器（其会破坏 WebView 原生滚动）。
let curZoom = 1;
function clampZoom(z) { return Math.max(0.5, Math.min(5, z)); }
window.setZoom = f => { curZoom = clampZoom(f); document.body.style.zoom = curZoom; };
window.getZoom = () => curZoom;
function pinchDist(t) { const dx = t[0].clientX - t[1].clientX, dy = t[0].clientY - t[1].clientY; return Math.hypot(dx, dy); }
let pStart = 0, pStartZoom = 1;
document.addEventListener('touchstart', e => {
  if (e.touches.length === 2) { pStart = pinchDist(e.touches); pStartZoom = curZoom; }
}, { passive: true });
document.addEventListener('touchmove', e => {
  if (e.touches.length === 2) {
    e.preventDefault();                       // 阻止浏览器默认捏合缩放
    const d = pinchDist(e.touches);
    curZoom = clampZoom(pStartZoom * d / pStart);
    document.body.style.zoom = curZoom;
  }
}, { passive: false });
</script>
</body>
</html>
""";
}
