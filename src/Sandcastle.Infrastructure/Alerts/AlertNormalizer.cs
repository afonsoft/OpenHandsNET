using System.Text.Json;

using Sandcastle.Application.Alerts;

namespace Sandcastle.Infrastructure.Alerts;

/// <summary>
/// Evento de alerta normalizado a partir do payload bruto.
/// <c>DeliveryId</c> = id de entrega p/ idempotência (delivery header
/// ou derivado); <c>Kind</c> = issue | incident | resolved;
/// <c>Severity</c> canônica (<see cref="AlertSeverities"/>);
/// <c>RoutingHint</c> = chave de routing alternativa (project/repo
/// do payload).
/// </summary>
public sealed record NormalizedAlert(
    string ExternalId,
    string? DeliveryId,
    string Kind,
    string Title,
    string? Url,
    string Severity,
    string? Service,
    string? Culprit,
    string? RoutingHint,
    IReadOnlyDictionary<string, string>? Labels,
    DateTimeOffset FirstSeen);

/// <summary>
/// SPEC-151 RF-001 — normaliza payloads de Sentry, PagerDuty e generic
/// para <see cref="NormalizedAlert"/>. Retorna null quando o payload
/// não é um alerta utilizável (sem external_id/título). Todo texto é
/// untrusted — o caller redige via <c>SecretRedactor</c> e envelopa
/// antes de prompts.
/// </summary>
public static class AlertNormalizer
{
    /// <summary>Despacha pelo provider; null = payload não utilizável.</summary>
    public static NormalizedAlert? Normalize(
        AlertProvider provider, byte[] payload, string? deliveryHeader)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            return provider switch
            {
                AlertProvider.Sentry => NormalizeSentry(
                    doc.RootElement, deliveryHeader),
                AlertProvider.PagerDuty => NormalizePagerDuty(
                    doc.RootElement, deliveryHeader),
                AlertProvider.Generic => NormalizeGeneric(
                    doc.RootElement, deliveryHeader),
                _ => null,
            };
        }
    }

    /// <summary>
    /// Sentry internal-integration webhook: <c>{action, data:{issue|event}}</c>.
    /// Issue resource (action created/resolved) e error event resource
    /// são aceitos; <c>resolved</c> marca kind=resolved.
    /// </summary>
    internal static NormalizedAlert? NormalizeSentry(
        JsonElement root, string? deliveryHeader)
    {
        var action = Str(root, "action");
        if (!root.TryGetProperty("data", out var data))
        {
            return null;
        }

        string? externalId, title, url, culprit, service, level, firstSeen;
        string? eventId = null;
        if (data.TryGetProperty("issue", out var issue))
        {
            externalId = Str(issue, "id");
            title = Str(issue, "title")
                ?? Nested(issue, "metadata", "title");
            url = Str(issue, "permalink") ?? Str(issue, "web_url");
            culprit = Str(issue, "culprit");
            service = ProjectSlug(issue);
            level = Str(issue, "level");
            firstSeen = Str(issue, "firstSeen");
            // issue.status resolved também fecha o loop.
            var status = Str(issue, "status");
            if (status is null && action is null)
            {
                return null;
            }
            if (status is "resolved")
            {
                action = "resolved";
            }
        }
        else if (data.TryGetProperty("event", out var ev))
        {
            externalId = Str(ev, "issue_id") ?? Str(ev, "event_id");
            eventId = Str(ev, "event_id");
            title = Str(ev, "title") ?? Nested(ev, "metadata", "title");
            url = Str(ev, "web_url") ?? Str(ev, "url");
            culprit = Str(ev, "culprit");
            service = ProjectSlug(ev);
            level = Str(ev, "level");
            firstSeen = Str(ev, "datetime") ?? Str(ev, "received");
        }
        else
        {
            return null;
        }

        if (externalId is null || title is null)
        {
            return null;
        }

        return new NormalizedAlert(
            externalId,
            deliveryHeader ?? eventId
                ?? (externalId + ":" + (action ?? "event")),
            action is "resolved" or "archived" or "ignored"
                ? AlertEventKinds.Resolved : AlertEventKinds.Issue,
            title,
            url,
            AlertSeverities.Normalize(level),
            service,
            culprit,
            RoutingHint: service,
            Labels: null,
            FirstSeen: ParseTs(firstSeen));
    }

    /// <summary>
    /// PagerDuty webhook v3: <c>{event:{id,event_type,data:{…incident}}}</c>.
    /// Dedup por <c>event.id</c>; external_id é o incident id.
    /// </summary>
    internal static NormalizedAlert? NormalizePagerDuty(
        JsonElement root, string? deliveryHeader)
    {
        if (!root.TryGetProperty("event", out var ev))
        {
            return null;
        }
        var eventType = Str(ev, "event_type") ?? "";
        var delivery = Str(ev, "id") ?? deliveryHeader;
        var data = ev.TryGetProperty("data", out var d) ? d : ev;

        var externalId = Str(data, "id") ?? Str(data, "incident_key");
        var title = Str(data, "title");
        if (externalId is null || title is null)
        {
            return null;
        }

        var priority = data.TryGetProperty("priority", out var pr)
            ? Str(pr, "summary") : null;
        var urgency = Str(data, "urgency");
        var service = data.TryGetProperty("service", out var svc)
            ? Str(svc, "summary") : null;

        return new NormalizedAlert(
            externalId,
            delivery,
            eventType.Contains("resolved", StringComparison.Ordinal)
            || eventType.Contains("closed", StringComparison.Ordinal)
                ? AlertEventKinds.Resolved : AlertEventKinds.Incident,
            title,
            Str(data, "html_url") ?? Str(data, "self"),
            priority is not null
                ? AlertSeverities.Normalize(priority)
                : AlertSeverities.Normalize(
                    urgency == "high" ? "high" : "low"),
            service,
            Culprit: null,
            RoutingHint: service,
            Labels: null,
            FirstSeen: ParseTs(
                Str(data, "created_at") ?? Str(ev, "occurred_at")));
    }

    /// <summary>
    /// Generic JSON: <c>{external_id|id, title, url?, severity?,
    /// kind?, service?, culprit?, first_seen?, repository?,
    /// project?, labels?{}}</c> — cobre Datadog/Grafana/etc.
    /// <c>repository</c> roteia direto (hint prioritário sobre service).
    /// </summary>
    internal static NormalizedAlert? NormalizeGeneric(
        JsonElement root, string? deliveryHeader)
    {
        var externalId = Str(root, "external_id")
            ?? Str(root, "id") ?? Str(root, "alert_id");
        var title = Str(root, "title") ?? Str(root, "summary")
            ?? Str(root, "name");
        if (externalId is null || title is null)
        {
            return null;
        }

        var kind = Str(root, "kind") ?? Str(root, "status");
        var resolved = kind is "resolved" or "ok" or "closed"
            or "recovery" or "recovered";
        var routingHint = Str(root, "repository")
            ?? Str(root, "project") ?? Str(root, "service");
        IReadOnlyDictionary<string, string>? labels = null;
        if (root.TryGetProperty("labels", out var ls)
            && ls.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<string, string>();
            foreach (var p in ls.EnumerateObject())
            {
                map[p.Name] = p.Value.ValueKind == JsonValueKind.String
                    ? p.Value.GetString() ?? "" : p.Value.GetRawText();
            }
            labels = map;
        }

        return new NormalizedAlert(
            externalId,
            deliveryHeader ?? Str(root, "delivery_id"),
            resolved ? AlertEventKinds.Resolved
                : kind is "incident" ? AlertEventKinds.Incident
                : AlertEventKinds.Issue,
            title,
            Str(root, "url") ?? Str(root, "link"),
            AlertSeverities.Normalize(
                Str(root, "severity") ?? Str(root, "priority")),
            Str(root, "service"),
            Str(root, "culprit"),
            routingHint,
            labels,
            ParseTs(Str(root, "first_seen") ?? Str(root, "timestamp")
                ?? Str(root, "created_at")));
    }

    /// <summary>Chaves de routing preferidas p/ lookup no mapping.</summary>
    public static IEnumerable<string> RoutingKeys(NormalizedAlert alert)
    {
        if (alert.RoutingHint is { } hint && hint.Length > 0)
        {
            yield return hint;
        }
        if (alert.Service is { } svc && svc.Length > 0
            && !string.Equals(svc, alert.RoutingHint, StringComparison.Ordinal))
        {
            yield return svc;
        }
    }

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(prop, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number or JsonValueKind.True
                    or JsonValueKind.False => v.GetRawText(),
                _ => null,
            }
            : null;

    private static string? Nested(JsonElement e, string outer, string inner) =>
        e.TryGetProperty(outer, out var o) ? Str(o, inner) : null;

    private static string? ProjectSlug(JsonElement e)
    {
        if (!e.TryGetProperty("project", out var p))
        {
            return null;
        }
        return p.ValueKind == JsonValueKind.Object
            ? Str(p, "slug") ?? Str(p, "name")
            : p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    private static DateTimeOffset ParseTs(string? raw)
    {
        if (raw is null)
        {
            return DateTimeOffset.UtcNow;
        }
        if (DateTimeOffset.TryParse(raw, out var ts))
        {
            return ts;
        }
        // epoch segundos/ms (alguns providers mandam número).
        if (long.TryParse(raw, out var epoch))
        {
            return epoch > 9_999_999_999
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }
        return DateTimeOffset.UtcNow;
    }
}