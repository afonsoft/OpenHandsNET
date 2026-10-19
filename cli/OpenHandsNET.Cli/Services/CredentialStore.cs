using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenHandsNET.Cli.Services;

/// <summary>
/// Credenciais por servidor em <c>~/.openhandsnet/credentials.json</c>
/// (0600). Nunca loga tokens — <see cref="Credential.Masked"/> para exibição.
/// </summary>
public sealed class CredentialStore
{
    private readonly string _dir;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public CredentialStore(string? dir = null) =>
        _dir = dir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openhandsnet");

    public sealed class Credential
    {
        public required string ServerUrl { get; init; }
        public string? AccessToken { get; init; }
        public string? RefreshToken { get; init; }
        public string? ApiKey { get; init; }
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

        [JsonIgnore]
        public string Masked => ApiKey is not null
            ? $"sk-***{ApiKey[^Math.Min(4, ApiKey.Length)..]}"
            : AccessToken is not null ? "***token" : "(none)";
    }

    private string FilePath => Path.Combine(_dir, "credentials.json");

    public async Task SaveAsync(Credential cred, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_dir);
        var all = (await LoadAllAsync(ct)).ToList();
        all.RemoveAll(c => c.ServerUrl == cred.ServerUrl);
        all.Add(cred);
        await File.WriteAllTextAsync(
            FilePath, JsonSerializer.Serialize(all, Json), ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(FilePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public async Task<Credential?> GetAsync(
        string serverUrl, CancellationToken ct = default) =>
        (await LoadAllAsync(ct)).FirstOrDefault(c =>
            c.ServerUrl.Equals(serverUrl, StringComparison.OrdinalIgnoreCase));

    public async Task<bool> DeleteAsync(
        string serverUrl, CancellationToken ct = default)
    {
        var all = (await LoadAllAsync(ct)).ToList();
        var removed = all.RemoveAll(c =>
            c.ServerUrl.Equals(serverUrl, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            await File.WriteAllTextAsync(
                FilePath, JsonSerializer.Serialize(all, Json), ct);
        }

        return removed > 0;
    }

    public async Task<IReadOnlyList<Credential>> LoadAllAsync(
        CancellationToken ct = default)
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<Credential>>(
                await File.ReadAllTextAsync(FilePath, ct), Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}