using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Git;

/// <summary>Identidade de commit resolvida para um autor.</summary>
/// <param name="AuthorName"><c>user.name</c> efetivo do commit.</param>
/// <param name="AuthorEmail"><c>user.email</c> efetivo do commit.</param>
/// <param name="CoAuthorTrailer">
/// Trailer <c>Co-authored-by: Name &lt;email&gt;</c> a acrescentar à
/// mensagem, ou null quando desabilitado/sem e-mail verificado.
/// </param>
public sealed record GitCommitIdentity(
    string AuthorName, string AuthorEmail, string? CoAuthorTrailer);

/// <summary>
/// SPEC-147 RF-002: resolve a autoria de commit do agente com
/// precedência <b>user → org → global</b> (mesmo padrão de settings do
/// repo — <c>users.preferences_json</c> e
/// <c>organizations.settings_json</c> são os stores key-value já
/// existentes, sem migration).
/// </summary>
public sealed class GitAuthorResolver(
    AppDbContext db, IOptions<GitOptions> options)
{
    /// <summary>User preference (<c>users.preferences_json</c>).</summary>
    public const string UserPrefAuthorName = "git_author_name";

    /// <summary>User preference (<c>users.preferences_json</c>).</summary>
    public const string UserPrefAuthorEmail = "git_author_email";

    /// <summary>User preference — opt-out pessoal do trailer.</summary>
    public const string UserPrefCoAuthor = "git_coauthor";

    /// <summary>Org setting (<c>organizations.settings_json</c>).</summary>
    public const string OrgSettingAuthorName = "git_author_name";

    /// <summary>Org setting (<c>organizations.settings_json</c>).</summary>
    public const string OrgSettingAuthorEmail = "git_author_email";

    /// <summary>Org setting — <c>false</c> força author neutro (sem trailer).</summary>
    public const string OrgSettingCoAuthor = "coauthor_enabled";

    /// <summary>
    /// Resolve a identidade para a conversa dona do sandbox
    /// (<c>null</c>/não encontrada → defaults globais sem trailer).
    /// </summary>
    public async Task<GitCommitIdentity> ResolveForConversationAsync(
        Guid? conversationId, CancellationToken ct)
    {
        if (conversationId is not { } cid)
        {
            return Global(options.Value);
        }

        var conv = await db.Conversations.AsNoTracking()
            .Where(c => c.Id == cid)
            .Select(c => new { c.CreatedByUserId, c.OrgId })
            .FirstOrDefaultAsync(ct);
        if (conv is null)
        {
            return Global(options.Value);
        }

        Dictionary<string, string>? userPrefs = null;
        string? displayName = null, email = null;
        var emailVerified = false;
        if (conv.CreatedByUserId is { } uidStr
            && Guid.TryParse(uidStr, out var uid))
        {
            var u = await db.Users.AsNoTracking()
                .Where(x => x.Id == uid)
                .Select(x => new
                {
                    x.PreferencesJson,
                    x.DisplayName,
                    x.Email,
                    x.EmailVerified,
                })
                .FirstOrDefaultAsync(ct);
            if (u is not null)
            {
                userPrefs = ParseDict(u.PreferencesJson);
                displayName = u.DisplayName;
                email = u.Email;
                emailVerified = u.EmailVerified;
            }
        }

        Dictionary<string, string>? orgSettings = null;
        if (conv.OrgId is { } orgId)
        {
            var json = await db.Organizations.AsNoTracking()
                .Where(o => o.Id == orgId)
                .Select(o => o.SettingsJson)
                .FirstOrDefaultAsync(ct);
            orgSettings = ParseDict(json);
        }

        return Resolve(userPrefs, orgSettings, options.Value,
            displayName, email, emailVerified);
    }

    /// <summary>
    /// Núcleo puro da cadeia user→org→global — estático e testável.
    /// </summary>
    public static GitCommitIdentity Resolve(
        IReadOnlyDictionary<string, string>? userPrefs,
        IReadOnlyDictionary<string, string>? orgSettings,
        GitOptions options,
        string? userDisplayName, string? userEmail,
        bool userEmailVerified)
    {
        var name = FirstNonEmpty(
            userPrefs?.GetValueOrDefault(UserPrefAuthorName),
            orgSettings?.GetValueOrDefault(OrgSettingAuthorName),
            options.AuthorName) ?? "sandcastle-agent";
        var mail = FirstNonEmpty(
            userPrefs?.GetValueOrDefault(UserPrefAuthorEmail),
            orgSettings?.GetValueOrDefault(OrgSettingAuthorEmail),
            options.AuthorEmail) ?? "agent@sandcastle.local";

        var coAuthorEnabled = FirstBool(
            userPrefs?.GetValueOrDefault(UserPrefCoAuthor),
            orgSettings?.GetValueOrDefault(OrgSettingCoAuthor),
            options.CoAuthorUser);

        // PII: o trailer só sai com e-mail verificado (NFR da SPEC-147).
        var trailer = coAuthorEnabled && userEmailVerified
            && !string.IsNullOrWhiteSpace(userEmail)
            ? $"Co-authored-by: {SanitizeName(userDisplayName, userEmail)}"
                + $" <{SanitizeEmail(userEmail)}>"
            : null;

        return new GitCommitIdentity(
            SanitizeField(name), SanitizeField(mail), trailer);
    }

    private static GitCommitIdentity Global(GitOptions o) => new(
        SanitizeField(o.AuthorName), SanitizeField(o.AuthorEmail), null);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static bool FirstBool(
        string? userValue, string? orgValue, bool globalValue) =>
        TryBool(userValue) ?? TryBool(orgValue) ?? globalValue;

    private static bool? TryBool(string? v) =>
        bool.TryParse(v, out var b) ? b : null;

    /// <summary>Remove CR/LF e aspas — autor vai para <c>-c user.name='…'</c>.</summary>
    private static string SanitizeField(string v) =>
        v.Replace("\r", "").Replace("\n", "")
            .Replace("'", "").Trim();

    /// <summary>Display name seguro p/ trailer (sem quebra/ângulos).</summary>
    private static string SanitizeName(string? displayName, string email)
    {
        var n = (displayName ?? email)
            .Replace("\r", "").Replace("\n", "")
            .Replace("<", "").Replace(">", "").Trim();
        return n.Length > 0 ? n : "user";
    }

    /// <summary>E-mail seguro p/ trailer (sem espaço/ângulos).</summary>
    private static string SanitizeEmail(string email) =>
        email.Replace("\r", "").Replace("\n", "")
            .Replace("<", "").Replace(">", "")
            .Replace(" ", "").Trim();

    private static Dictionary<string, string>? ParseDict(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer
                .Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}