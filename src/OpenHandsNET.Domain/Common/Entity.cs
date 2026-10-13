namespace OpenHandsNET.Domain.Common;

/// <summary>
/// Base class for aggregate entities with integer identity.
/// </summary>
public abstract class Entity
{
    /// <summary>Gets the entity identifier.</summary>
    public int Id { get; protected set; }
}
