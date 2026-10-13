using OpenHandsNET.Domain.Common;

namespace OpenHandsNET.Domain.Configuration;

/// <summary>
/// Key-value application setting persisted in the database.
/// </summary>
public sealed class AppSetting : Entity
{
    /// <summary>Gets the setting key (unique).</summary>
    public required string Key { get; set; }

    /// <summary>Gets or sets the JSON-encoded setting value.</summary>
    public string? Value { get; set; }

    /// <summary>Gets the UTC timestamp of the last update.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
