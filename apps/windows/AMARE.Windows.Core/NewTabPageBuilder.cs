using System.Net;
using System.Text;

namespace Amare.Windows.Core;

public static class NewTabPageBuilder
{
    public static string Build(
        IReadOnlyList<HistoryEntryDto> topSites,
        string searchEngineName,
        string nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
        {
            throw new ArgumentException("Nonce is required.", nameof(nonce));
        }
        var sites = topSites
            .Where(site => NavigationPolicy.Decide(site.Url) == NavigationDecision.Allow
                           && (site.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                               || site.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            .Take(8)
            .ToArray();
        var builder = new StringBuilder(sites.Length * 160 + 4096);
        builder.Append("""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>New Tab</title>
<style>
:root { color-scheme: light dark; --fg:#202124; --muted:#5f6368; --bg:#f8f9fa; --panel:#ffffff; --line:#dadce0; --accent:#315c9e; }
@media (prefers-color-scheme: dark) { :root { --fg:#e8eaed; --muted:#a7abb1; --bg:#202124; --panel:#2b2c2f; --line:#4a4d52; --accent:#8ab4f8; } }
* { box-sizing: border-box; }
body { margin:0; min-height:100vh; background:var(--bg); color:var(--fg); font:15px/1.5 "Segoe UI", system-ui, sans-serif; display:flex; align-items:center; justify-content:center; }
main { width:min(760px, calc(100% - 48px)); padding:48px 0; }
.logo { font-size:34px; letter-spacing:.08em; font-weight:700; text-align:center; margin-bottom:8px; }
.subtitle { color:var(--muted); text-align:center; margin-bottom:28px; }
form { display:flex; background:var(--panel); border:1px solid var(--line); border-radius:28px; padding:7px 8px 7px 20px; box-shadow:0 2px 12px rgba(0,0,0,.06); }
input { flex:1; border:0; outline:0; background:transparent; color:var(--fg); font-size:17px; min-width:0; }
button { border:0; border-radius:22px; background:var(--accent); color:white; padding:9px 18px; font-size:14px; cursor:pointer; }
h2 { font-size:15px; color:var(--muted); font-weight:600; margin:32px 0 12px; }
.entrypoints { display:flex; gap:8px; justify-content:center; margin-top:16px; flex-wrap:wrap; }
.chip { border:1px solid var(--line); border-radius:16px; padding:5px 12px; color:var(--muted); font-size:12px; }
.grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(150px,1fr)); gap:10px; }
.site { display:flex; align-items:center; gap:10px; min-height:54px; padding:10px 12px; border:1px solid var(--line); border-radius:12px; background:var(--panel); color:var(--fg); text-decoration:none; overflow:hidden; }
.site:hover { border-color:var(--accent); }
.initial { width:30px; height:30px; border-radius:8px; background:var(--accent); color:white; display:grid; place-items:center; flex:0 0 auto; font-weight:700; }
.name { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.empty { color:var(--muted); border:1px dashed var(--line); border-radius:12px; padding:18px; }
.hint { color:var(--muted); font-size:13px; margin-top:28px; text-align:center; }
</style>
</head>
<body>
<main>
<div class="logo">AMARE</div>
<div class="subtitle">Search the web with
""");
        builder.Append(WebUtility.HtmlEncode(searchEngineName));
        builder.Append("""
</div>
<form id="searchForm" autocomplete="off">
<input id="searchBox" name="q" type="search" placeholder="Search or enter a web address" autofocus />
<button type="submit">Search</button>
</form>
<div class="entrypoints">
<span class="chip">Bookmarks · next phase</span>
<span class="chip">Library · next phase</span>
<span class="chip">AI panel · next phase</span>
</div>
<h2>Frequently visited</h2>
""");
        if (sites.Length == 0)
        {
            builder.Append("<div class=\"empty\">Your frequently visited sites will appear here as you browse.</div>\n");
        }
        else
        {
            builder.Append("<div class=\"grid\">\n");
            foreach (var site in sites)
            {
                var title = string.IsNullOrWhiteSpace(site.Title) ? site.Url : site.Title;
                var initial = title.Length == 0 ? "?" : WebUtility.HtmlEncode(title[..1].ToUpperInvariant());
                builder.Append("<a class=\"site\" href=\"")
                    .Append(WebUtility.HtmlEncode(site.Url))
                    .Append("\"><span class=\"initial\">")
                    .Append(initial)
                    .Append("</span><span class=\"name\">")
                    .Append(WebUtility.HtmlEncode(title))
                    .Append("</span></a>\n");
            }
            builder.Append("</div>\n");
        }
        builder.Append("""
<div class="hint">Local new tab page · No external server</div>
</main>
<script>
const token =
""");
        builder.Append(System.Text.Json.JsonSerializer.Serialize(nonce));
        builder.Append("""
;
document.getElementById('searchForm').addEventListener('submit', event => {
  event.preventDefault();
  const query = document.getElementById('searchBox').value.trim();
  if (!query) return;
  window.chrome.webview.postMessage({ token, type: 'search', query });
});
</script>
</body>
</html>
""");
        return builder.ToString();
    }
}
