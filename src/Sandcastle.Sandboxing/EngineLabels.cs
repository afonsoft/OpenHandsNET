namespace Sandcastle.Sandboxing;

/// <summary>Label convention shared by every sandbox engine (SPEC-35).</summary>
public static class EngineLabels
{
    /// <summary>Label prefix on all managed containers.</summary>
    public const string Prefix = "sandcastle.";

    /// <summary><c>sandcastle.managed=true</c> — engine-owned container.</summary>
    public const string Managed = $"{Prefix}managed";

    /// <summary><c>sandcastle.owner</c> — instance discriminator for sweeps.</summary>
    public const string Owner = $"{Prefix}owner";

    /// <summary><c>sandcastle.sandbox_id</c> — sandbox record id.</summary>
    public const string SandboxId = $"{Prefix}sandbox_id";

    /// <summary><c>sandcastle.conversation_id</c>.</summary>
    public const string ConversationId = $"{Prefix}conversation_id";

    /// <summary><c>sandcastle.pool=warm</c> — free pool container.</summary>
    public const string Pool = $"{Prefix}pool";

    /// <summary><c>sandcastle.pool_key</c> — image the pool slot serves.</summary>
    public const string PoolKey = $"{Prefix}pool_key";

    /// <summary><c>sandcastle.init_key</c> — dormant-agent bootstrap key.</summary>
    public const string InitKey = $"{Prefix}init_key";

    /// <summary><c>sandcastle.dind_for</c> — DinD sidecar of a sandbox.</summary>
    public const string DinDFor = $"{Prefix}dind_for";

    /// <summary><c>sandcastle.role</c> — infra role (egress-proxy…).</summary>
    public const string Role = $"{Prefix}role";

    /// <summary><c>sc-pool-</c> container name prefix — claimed slots are
    /// renamed to <c>sc-sandbox-*</c>, which is the stateless claim marker
    /// (labels are immutable post-create).</summary>
    public const string PoolNamePrefix = "sc-pool-";
}