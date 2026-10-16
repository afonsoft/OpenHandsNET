namespace OpenHandsNET.Client.Services;

/// <summary>
/// SPEC-45 RF-003: modelo + expansão de slash commands do composer —
/// builtins (<c>/plan /build /compact /new</c>) + skills da conversa
/// (prompt template com <c>$ARGUMENTS</c>/<c>$1..$N</c>). A execução é
/// client-side: expande para texto/ação — o wire continua send-message.
/// </summary>
public static class ComposerSlash
{
    /// <summary>Um item do popup de slash.</summary>
    public sealed record Command(
        string Name, string Description, CommandKind Kind,
        string? SkillContent = null);

    /// <summary>Tipo de execução do command.</summary>
    public enum CommandKind
    {
        /// <summary>Ação de modo: `mode plan|build`.</summary>
        Mode,
        /// <summary>Ação de compactação.</summary>
        Compact,
        /// <summary>Nova conversa (navega p/ home).</summary>
        New,
        /// <summary>Skill: expande o template p/ texto no composer.</summary>
        Skill,
    }

    /// <summary>Builtins (só fazem sentido com conversa aberta).</summary>
    public static readonly IReadOnlyList<Command> Builtins =
    [
        new("plan", "Modo plan (read-only overlay)", CommandKind.Mode),
        new("build", "Modo build (execução normal)", CommandKind.Mode),
        new("compact", "Compacta o contexto da conversa",
            CommandKind.Compact),
        new("new", "Nova conversa", CommandKind.New),
    ];

    /// <summary>
    /// Skills → commands <c>/{name}</c> (normaliza espaços p/ '-').
    /// </summary>
    public static IEnumerable<Command> FromSkills(
        IEnumerable<SkillItem> skills) =>
        skills.Select(s => new Command(
            s.Name.Replace(' ', '-').ToLowerInvariant(),
            s.Description ?? s.Name,
            CommandKind.Skill, s.ContentMd));

    /// <summary>
    /// Expande o template da skill: <c>$ARGUMENTS</c> = resto da linha;
    /// <c>$1..$N</c> = tokens whitespace. Sem template → <c>/{name} args</c>
    /// literal (o agente resolve pela descrição).
    /// </summary>
    public static string Expand(Command cmd, string args)
    {
        if (cmd.SkillContent is not { Length: > 0 } tpl)
        {
            return string.IsNullOrWhiteSpace(args)
                ? $"/{cmd.Name}" : $"/{cmd.Name} {args}";
        }

        var toks = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var expanded = tpl.Replace("$ARGUMENTS", args);
        for (var i = 0; i < toks.Length && i < 9; i++)
        {
            expanded = expanded.Replace($"${i + 1}", toks[i]);
        }

        return expanded;
    }

    /// <summary>Filtro do popup — substring case-insensitive.</summary>
    public static IEnumerable<Command> Filter(
        IEnumerable<Command> cmds, string prefix) =>
        string.IsNullOrEmpty(prefix)
            ? cmds
            : cmds.Where(c => c.Name.Contains(prefix,
                StringComparison.OrdinalIgnoreCase));
}
