using OpenHandsNET.Infrastructure.Agent;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-123: deferred tool loading — Set/IsDeferredOnly/Load/Clear por
/// conversa; Load nunca promove tool fora do conjunto deferido.
/// </summary>
public sealed class DeferredToolStateTests
{
    private readonly DeferredToolState _sut = new();
    private readonly Guid _conv = Guid.NewGuid();

    [Fact]
    public void Set_RegistraDeferidosPorConversa()
    {
        _sut.Set(_conv, ["lsp_hover", "job_run"]);
        _sut.IsDeferredOnly(_conv, "lsp_hover").ShouldBeTrue();
        _sut.IsDeferredOnly(_conv, "file_read").ShouldBeFalse();
    }

    [Fact]
    public void Load_PromoveDeferida_EIsolamentoPorConversa()
    {
        _sut.Set(_conv, ["lsp_hover"]);
        _sut.Set(Guid.NewGuid(), ["lsp_hover"]);
        _sut.Load(_conv, "lsp_hover").ShouldBeTrue();
        _sut.IsDeferredOnly(_conv, "lsp_hover").ShouldBeFalse();
        _sut.Loaded(_conv).ShouldContain("lsp_hover");
    }

    [Fact]
    public void Load_ForaDoConjunto_RetornaFalse()
    {
        _sut.Set(_conv, ["lsp_hover"]);
        _sut.Load(_conv, "bash").ShouldBeFalse();
        _sut.Load(_conv, "lsp_hover").ShouldBeTrue();
        _sut.Load(_conv, "lsp_hover").ShouldBeFalse(); // idempotente
    }

    [Fact]
    public void Clear_LiberaEstado()
    {
        _sut.Set(_conv, ["lsp_hover"]);
        _sut.Clear(_conv);
        _sut.IsDeferredOnly(_conv, "lsp_hover").ShouldBeFalse();
        _sut.Loaded(_conv).ShouldBeEmpty();
    }

    [Fact]
    public void Set_Vazio_NaoCriaScope()
    {
        _sut.Set(_conv, []);
        _sut.IsDeferredOnly(_conv, "x").ShouldBeFalse();
        _sut.Load(_conv, "x").ShouldBeFalse();
    }
}