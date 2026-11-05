using System.Net;
using System.Text;
using System.Text.Json;

namespace Amare.Windows.Core;

public sealed record ReaderExtractedBlock(string Tag, string Text, string? Source = null);

public sealed record ReaderExtractedPage(
    string Title,
    string Url,
    IReadOnlyList<ReaderExtractedBlock> Blocks,
    int WordCount);

public sealed record ReaderDisplaySettings(
    string Font,
    string Theme,
    int FontSize,
    double LineHeight,
    int Width);

public static class ReaderExtractionScript
{
    public const string Script = """
        (() => {
          const root = document.querySelector('article, main, [role="main"]') || document.body;
          if (!root) return JSON.stringify({ title: document.title || '', url: location.href, blocks: [], wordCount: 0 });
          const clone = root.cloneNode(true);
          clone.querySelectorAll('script,style,noscript,nav,header,footer,aside,form,button,iframe,svg,canvas, [role="navigation"], [role="banner"], [role="contentinfo"], [aria-hidden="true"]').forEach(node => node.remove());
          const blocks = [];
          const nodes = clone.querySelectorAll('h1,h2,h3,h4,p,li,blockquote,pre,img');
          for (const node of nodes) {
            const tag = node.tagName.toLowerCase();
            if (tag === 'img') {
              const src = node.currentSrc || node.src || '';
              if (src) blocks.push({ tag: 'img', text: node.alt || '', source: src });
              continue;
            }
            const text = (node.innerText || node.textContent || '').replace(/\s+/g, ' ').trim();
            if (text) blocks.push({ tag, text: text.slice(0, 20000) });
            if (blocks.length >= 400) break;
          }
          if (blocks.length === 0) {
            const fallback = (document.body?.innerText || document.body?.textContent || '').replace(/\s+/g, ' ').trim();
            if (fallback) blocks.push({ tag: 'p', text: fallback.slice(0, 20000) });
          }
          const plain = blocks.filter(block => block.tag !== 'img').map(block => block.text).join(' ');
          return JSON.stringify({ title: document.title || '', url: location.href, blocks, wordCount: plain.split(/\s+/).filter(Boolean).length });
        })()
        """;
}

public static class ReaderPageBuilder
{
    public static string Build(ReaderExtractedPage page, ReaderDisplaySettings settings, string nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
        {
            throw new ArgumentException("Nonce is required.", nameof(nonce));
        }
        var font = settings.Font switch
        {
            "sans" => "Segoe UI, system-ui, sans-serif",
            "mono" => "Cascadia Mono, Consolas, monospace",
            _ => "Georgia, 'Times New Roman', serif"
        };
        var (background, foreground, muted, accent) = settings.Theme switch
        {
            "dark" => ("#121212", "#e8e6e3", "#a8a5a0", "#9ec1ff"),
            "sepia" => ("#f5ecd8", "#3d3427", "#776b59", "#7a4e1d"),
            _ => ("#fcfbf8", "#202124", "#5f6368", "#2457a7")
        };
        var builder = new StringBuilder(Math.Max(2048, page.Blocks.Count * 96));
        builder.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n")
            .Append("<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<meta name=\"amare-reader-nonce\" content=\"")
            .Append(WebUtility.HtmlEncode(nonce)).Append("\">\n<title>");
        builder.Append(WebUtility.HtmlEncode(page.Title));
        builder.Append("</title><style>:root{color-scheme:light dark;--fg:")
            .Append(foreground).Append(";--muted:").Append(muted)
            .Append(";--bg:").Append(background).Append(";--accent:").Append(accent)
            .Append(";}@media(prefers-color-scheme:dark){:root{color-scheme:dark;}}")
            .Append("body{margin:0;background:var(--bg);color:var(--fg);font-family:")
            .Append(font).Append(";font-size:").Append(settings.FontSize)
            .Append("px;line-height:").Append(settings.LineHeight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
            .Append(";}main{max-width:").Append(settings.Width).Append("px;margin:0 auto;padding:42px 28px 80px;}")
            .Append("h1,h2,h3,h4{line-height:1.2;margin:1.6em 0 .55em;}h1{font-size:1.65em;}h2{font-size:1.35em;}h3{font-size:1.15em;}")
            .Append("p{margin:.82em 0;}li{margin:.35em 0;margin-left:1.2em;}blockquote{margin:1em 0;padding-left:1em;border-left:3px solid var(--accent);color:var(--muted);}")
            .Append("pre{overflow:auto;padding:14px;border-radius:8px;background:rgba(127,127,127,.12);font-family:Cascadia Mono,Consolas,monospace;font-size:.88em;}")
            .Append("img{display:block;max-width:100%;height:auto;margin:1.2em auto;border-radius:6px;}")
            .Append(".meta{color:var(--muted);font-size:.72em;overflow-wrap:anywhere;margin-bottom:2em;}.empty{color:var(--muted);}")
            .Append("</style></head><body><main>")
            .Append("<h1>").Append(WebUtility.HtmlEncode(page.Title)).Append("</h1>")
            .Append("<div class=\"meta\">").Append(WebUtility.HtmlEncode(page.Url)).Append("</div>");
        if (page.Blocks.Count == 0 || page.WordCount == 0)
        {
            builder.Append("<p class=\"empty\">Reader could not extract readable content from this page.</p>");
        }
        foreach (var block in page.Blocks)
        {
            var text = WebUtility.HtmlEncode(block.Text);
            switch (block.Tag.ToLowerInvariant())
            {
                case "h1":
                case "h2":
                case "h3":
                case "h4":
                    builder.Append('<').Append(block.Tag.ToLowerInvariant()).Append('>')
                        .Append(text).Append("</").Append(block.Tag.ToLowerInvariant()).Append('>');
                    break;
                case "li":
                    builder.Append("<li>").Append(text).Append("</li>");
                    break;
                case "blockquote":
                    builder.Append("<blockquote>").Append(text).Append("</blockquote>");
                    break;
                case "pre":
                    builder.Append("<pre>").Append(text).Append("</pre>");
                    break;
                case "img":
                    if (IsSafeImageUrl(block.Source))
                    {
                        builder.Append("<img src=\"").Append(WebUtility.HtmlEncode(block.Source))
                            .Append("\" alt=\"").Append(WebUtility.HtmlEncode(block.Text)).Append("\">");
                    }
                    break;
                default:
                    builder.Append("<p>").Append(text).Append("</p>");
                    break;
            }
        }
        builder.Append("</main></body></html>");
        return builder.ToString();
    }

    private static bool IsSafeImageUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase));
}
