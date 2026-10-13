using System.Text.Json.Serialization;

namespace OpenHandsNET.Domain.Events;

/// <summary>
/// Event origin (upstream <c>SourceType</c> literal union).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<EventSource>))]
public enum EventSource
{
    /// <summary>Emitted by the agent.</summary>
    [JsonStringEnumMemberName("agent")]
    Agent,

    /// <summary>Emitted by the user.</summary>
    [JsonStringEnumMemberName("user")]
    User,

    /// <summary>Emitted by the environment / runtime.</summary>
    [JsonStringEnumMemberName("environment")]
    Environment,

    /// <summary>Emitted by a lifecycle hook.</summary>
    [JsonStringEnumMemberName("hook")]
    Hook,
}
