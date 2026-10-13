namespace OpenHandsNET.Api.Options;

/// <summary>
/// Root application options, bound from configuration. Environment variables
/// prefixed <c>OPENHANDSNET_</c> map to sections via <c>__</c>
/// (e.g. <c>OPENHANDSNET_Database__Provider</c> → <c>Database:Provider</c>).
/// </summary>
public sealed class OpenHandsNetOptions
{
    /// <summary>Database provider selection and connection.</summary>
    public DatabaseOptions Database { get; set; } = new();

    /// <summary>Authentication options (SPEC-08).</summary>
    public AuthOptions Auth { get; set; } = new();

    /// <summary>Observability options.</summary>
    public ObservabilityOptions Observability { get; set; } = new();

    /// <summary>
    /// Deployment mode: <c>oss</c> (default, single-user) | <c>self_hosted</c> | <c>cloud</c>.
    /// </summary>
    public string DeploymentMode { get; set; } = "oss";
}

/// <summary>Database provider options.</summary>
public sealed class DatabaseOptions
{
    /// <summary><c>Sqlite</c> (default) | <c>PostgreSql</c>.</summary>
    public string Provider { get; set; } = "Sqlite";

    /// <summary>Provider connection string.</summary>
    public string? ConnectionString { get; set; }
}

/// <summary>Authentication options.</summary>
public sealed class AuthOptions
{
    /// <summary><c>none</c> (default, OSS local) | <c>oidc</c>.</summary>
    public string Provider { get; set; } = "none";

    /// <summary>OIDC authority (e.g. Keycloak realm URL).</summary>
    public string? Authority { get; set; }

    /// <summary>OIDC client id.</summary>
    public string? ClientId { get; set; }
}

/// <summary>Observability options.</summary>
public sealed class ObservabilityOptions
{
    /// <summary>OTLP collector endpoint; when set, traces/metrics are exported.</summary>
    public string? OtlpEndpoint { get; set; }
}
