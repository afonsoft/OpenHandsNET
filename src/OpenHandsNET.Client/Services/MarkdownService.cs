using Ganss.Xss;

using Markdig;

using Microsoft.AspNetCore.Components;

namespace OpenHandsNET.Client.Services;

/// <summary>
/// SPEC-26 RF-003: markdown → HTML sanitizado. Markdig com pipeline
/// avançado (tabelas, task-lists, fenced code) + whitelist via
/// <see cref="HtmlSanitizer"/> (sem script/iframe/on*/javascript:) —
/// XSS é o vetor #1 do chat, sanitize é obrigatório em toda renderização.
/// </summary>
public sealed class MarkdownService
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml() // markdown nunca injeta HTML cru — só o do renderer
            .Build();

    private static readonly HtmlSanitizer Sanitizer = Build();

    /// <summary>Markdown → HTML sanitizado pronto para <see cref="MarkupString"/>.</summary>
    public static MarkupString Render(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new MarkupString("");
        }

        var html = Markdig.Markdown.ToHtml(markdown, Pipeline);
        return new MarkupString(Sanitizer.Sanitize(html));
    }

    private static HtmlSanitizer Build()
    {
        var s = new HtmlSanitizer();
        // Whitelist estrita de render — headings/listas/tabelas/código.
        s.AllowedTags.Clear();
        foreach (var t in new[]
        {
            "p", "br", "hr", "pre", "code", "blockquote",
            "h1", "h2", "h3", "h4", "h5", "h6",
            "ul", "ol", "li", "input",
            "table", "thead", "tbody", "tr", "th", "td",
            "a", "em", "strong", "del", "code", "span", "div",
        })
        {
            s.AllowedTags.Add(t);
        }

        s.AllowedAttributes.Clear();
        foreach (var a in new[] { "href", "title", "class", "type", "checked", "disabled" })
        {
            s.AllowedAttributes.Add(a);
        }

        s.AllowedCssProperties.Clear();
        s.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto" })
        {
            s.AllowedSchemes.Add(scheme);
        }

        s.KeepChildNodes = true;
        return s;
    }
}