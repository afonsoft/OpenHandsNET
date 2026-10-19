using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OpenHandsNET.Api.Routing;
using OpenHandsNET.Sandboxing;

using Shouldly;

namespace OpenHandsNET.UnitTests.Eval;

/// <summary>
/// SPEC-40 RF-004: preview tokens stateless-HMAC — binding
/// sandbox+port, exp ≤15min, assinatura verificada.
/// </summary>
public sealed class PreviewTokenTests
{
    private static PreviewTokenService NewSvc(string? secret = "s3cret") =>
        new(Options.Create(new SandboxOptions
        { PreviewTokenSecret = secret }),
            NullLogger<PreviewTokenService>.Instance);

    [Fact]
    public void Mint_Then_Validate_Succeeds()
    {
        var svc = NewSvc();
        var id = Guid.NewGuid();
        var (token, exp) = svc.Mint(id, 7681);
        token.ShouldStartWith($"pv1.{id:N}.7681.");
        exp.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        svc.Validate(token, id, 7681).ShouldBeTrue();
    }

    [Fact]
    public void Validate_Rejects_Wrong_Port_Or_Sandbox()
    {
        var svc = NewSvc();
        var id = Guid.NewGuid();
        var (token, _) = svc.Mint(id, 7681);
        svc.Validate(token, id, 9999).ShouldBeFalse();
        svc.Validate(token, Guid.NewGuid(), 7681).ShouldBeFalse();
    }

    [Fact]
    public void Validate_Rejects_Tampered_Signature()
    {
        var svc = NewSvc();
        var id = Guid.NewGuid();
        var (token, _) = svc.Mint(id, 7681);
        var parts = token.Split('.');
        parts[3] = "1"; // exp adulterado → assinatura quebra
        svc.Validate(string.Join('.', parts), id, 7681).ShouldBeFalse();
        parts[4] = new string('0', parts[4].Length); // sig inválida
        svc.Validate(string.Join('.', parts), id, 7681).ShouldBeFalse();
        svc.Validate("not-a-token", id, 7681).ShouldBeFalse();
        svc.Validate(null, id, 7681).ShouldBeFalse();
    }

    [Fact]
    public void Ephemeral_Secret_Still_Works()
    {
        var svc = NewSvc(secret: null); // secret efêmera por boot
        var id = Guid.NewGuid();
        var (token, _) = svc.Mint(id, 8000);
        svc.Validate(token, id, 8000).ShouldBeTrue();
    }
}