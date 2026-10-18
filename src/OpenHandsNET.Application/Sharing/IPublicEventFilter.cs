using System.Text.Json;

namespace OpenHandsNET.Application.Sharing;

/// <summary>
/// Security filter applied to event payloads before they are served on
/// public share links (SPEC-56 RF-003). Implemented as a separate layer
/// so a redaction regression is a failing test, not a leak.
/// </summary>
public interface IPublicEventFilter
{
    /// <summary>
    /// Returns a sanitized copy of the raw stored event JSON
    /// (<paramref name="kind"/> discriminator + serialized payload), or
    /// <see langword="null"/> when the event kind is internal/ephemeral
    /// and must never be published.
    /// </summary>
    JsonElement? Filter(
        string kind, string rawPayload, IReadOnlyList<string>? secretValues);
}
