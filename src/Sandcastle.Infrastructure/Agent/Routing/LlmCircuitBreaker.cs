using System.Collections.Concurrent;

namespace Sandcastle.Infrastructure.Agent.Routing;

/// <summary>
/// SPEC-149 RF-002: in-memory per-worker circuit breaker keyed by
/// <c>provider:model</c>. States: closed → open (fails fast until
/// cooldown) → half-open (the first resolution after cooldown is the
/// probe — a new failure re-opens). Per spec, the state is allowed to be
/// in-memory and fails open after restart.
/// </summary>
public sealed class LlmCircuitBreaker(
    Func<DateTimeOffset>? clock = null)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _openUntil =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock =
        clock ?? (() => DateTimeOffset.UtcNow);

    private static string Key(string? provider, string model) =>
        $"{provider ?? ""}:{model}";

    /// <summary>True while the candidate is circuit-open (skip it).</summary>
    public bool IsOpen(string? provider, string model)
    {
        if (!_openUntil.TryGetValue(Key(provider, model), out var until))
        {
            return false;
        }

        // Cooldown expirado ⇒ half-open: a próxima resolução é a probe.
        return _clock() < until;
    }

    /// <summary>Opens the circuit for <paramref name="cooldown"/>.</summary>
    public void ReportFailure(
        string? provider, string model, TimeSpan cooldown)
    {
        if (cooldown <= TimeSpan.Zero)
        {
            return;
        }

        _openUntil[Key(provider, model)] = _clock() + cooldown;
    }

    /// <summary>Closes the circuit (probe/call succeeded).</summary>
    public void ReportSuccess(string? provider, string model) =>
        _openUntil.TryRemove(Key(provider, model), out _);
}