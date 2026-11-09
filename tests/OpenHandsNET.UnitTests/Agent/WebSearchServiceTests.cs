using OpenHandsNET.Infrastructure.Web;

using Shouldly;

using Xunit;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>SPEC-133: <c>web_search</c> — parser DDG + dispatch.</summary>
public sealed class WebSearchServiceTests
{
    private const string DdgHtml = """
        <html><body>
        <a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fpage&amp;rut=abc">Example &amp; Title</a>
        <a class="result__snippet">A snippet &lt;b&gt;with&lt;/b&gt; tags and entities &amp; stuff</a>
        <a class="result__a" href="https://direct.example.org/x">Direct Link</a>
        <a class="result__snippet">Second snippet</a>
        </body></html>
        """;

    [Fact]
    public void ParseDuckDuckGo_Extracts_Title_Url_Snippet()
    {
        var results = WebSearchService.ParseDuckDuckGo(DdgHtml, 5);
        results.Count.ShouldBe(2);
        results[0].Title.ShouldBe("Example & Title");
        results[0].Url.ShouldBe("https://example.com/page");
        results[0].Snippet.ShouldContain("with");
        results[0].Snippet.ShouldNotContain("<b>");
        results[1].Url.ShouldBe("https://direct.example.org/x");
    }

    [Fact]
    public void ParseDuckDuckGo_Respects_MaxResults()
    {
        WebSearchService.ParseDuckDuckGo(DdgHtml, 1).Count.ShouldBe(1);
    }

    [Fact]
    public void ParseDuckDuckGo_EmptyHtml_Returns_Empty()
    {
        WebSearchService.ParseDuckDuckGo("<html></html>", 5).ShouldBeEmpty();
    }
}
