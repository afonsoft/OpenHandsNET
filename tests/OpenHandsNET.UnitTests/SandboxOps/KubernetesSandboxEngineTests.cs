using OpenHandsNET.Sandboxing;

namespace OpenHandsNET.UnitTests.SandboxOps;

/// <summary>SPEC-88: workspace PVC naming + conversation label extraction.</summary>
public sealed class KubernetesSandboxEngineTests
{
    [Fact]
    public void WorkspacePvcName_MatchesCreateFormat()
    {
        var conv = Guid.Parse("12345678-9abc-def0-1234-56789abcdef0");

        KubernetesSandboxEngine.WorkspacePvcName(conv)
            .ShouldBe("ohn-ws-123456789abcdef0123456789abcdef0");
    }

    [Fact]
    public void TryGetConversationId_ParsesEngineLabel()
    {
        var conv = Guid.NewGuid();

        KubernetesSandboxEngine.TryGetConversationId(
            new Dictionary<string, string>
            {
                [EngineLabels.ConversationId] = conv.ToString(),
            }).ShouldBe(conv);
    }

    [Fact]
    public void TryGetConversationId_ReturnsNull_WhenLabelMissing()
    {
        KubernetesSandboxEngine.TryGetConversationId(null).ShouldBeNull();
        KubernetesSandboxEngine.TryGetConversationId(
            new Dictionary<string, string>()).ShouldBeNull();
    }

    [Fact]
    public void TryGetConversationId_ReturnsNull_WhenLabelInvalid()
    {
        KubernetesSandboxEngine.TryGetConversationId(
            new Dictionary<string, string>
            {
                [EngineLabels.ConversationId] = "not-a-guid",
            }).ShouldBeNull();
    }
}