namespace OpenHandsNET.Infrastructure.Agent.SystemPrompt;

/// <summary>
/// SPEC-123: prompts embutidos (<c>prompts/*.md</c>) — carregados do
/// assembly uma vez e cacheados. Fallback para string vazia mantém o
/// consumidor funcional se o recurso sumir (não deve).
/// </summary>
public static class EmbeddedPrompts
{
    private static readonly System.Collections.Concurrent
        .ConcurrentDictionary<string, string> Cache = new();

    /// <summary>Lê <c>prompts/{name}.md</c> embutido.</summary>
    public static string Get(string name) => Cache.GetOrAdd(name, static n =>
    {
        var asm = typeof(EmbeddedPrompts).Assembly;
        var res = $"OpenHandsNET.Infrastructure.prompts.{n}.md";
        using var stream = asm.GetManifestResourceStream(res);
        if (stream is null)
        {
            return "";
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    });
}