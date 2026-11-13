namespace Sandcastle.Infrastructure.Alerts;

/// <summary>
/// SPEC-151 — opções do pipeline de alertas: caps de sessões
/// automáticas e rate limit inbound por source.
/// </summary>
public sealed class AlertOptions
{
    /// <summary>Seção de configuração.</summary>
    public const string Section = "Alerts";

    /// <summary>Master switch — off ignora inbound (200 ignored).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Teto de sessões auto-criadas por source por hora.</summary>
    public int MaxSessionsPerHour { get; set; } = 10;

    /// <summary>Teto de entregas inbound por source por minuto.</summary>
    public int MaxInboundPerMin { get; set; } = 60;
}