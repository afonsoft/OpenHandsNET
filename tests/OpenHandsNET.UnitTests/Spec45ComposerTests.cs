using System.Text;
using OpenHandsNET.Application.Sandbox;
using Shouldly;
using Xunit;
using DomainSandbox = OpenHandsNET.Domain.Sandbox.Sandbox;

namespace OpenHandsNET.UnitTests;

/// <summary>
/// SPEC-45: upload binário chunked (<c>WriteFileBytesAsync</c>) e
/// expansão de slash commands (<c>$ARGUMENTS</c>/<c>$1..$N</c>).
/// </summary>
public sealed class Spec45ComposerTests
{
    // ---- WriteFileBytesAsync (DIM chunked) ------------------------------

    /// <summary>Fake mínimo que simula o shell: captura chunks do
    /// staging e decodifica o .upload.b64 no decode final.</summary>
    private sealed class UploadFakeExecutor : ISandboxExecutor
    {
        private readonly StringBuilder _staged = new();
        public List<string> Commands { get; } = [];
        public byte[]? Decoded { get; private set; }

        public Task<IBashSession> OpenBashSessionAsync(
            DomainSandbox s, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CommandResult> ExecAsync(
            DomainSandbox s, string command, TimeSpan t,
            CancellationToken ct)
        {
            Commands.Add(command);
            if (command.StartsWith("echo -n '"))
            {
                var inner = command["echo -n '".Length..];
                inner = inner[..inner.IndexOf('\'')];
                // '>>' append mantém; '>' simples reinicia o staging.
                if (!command.Contains(">>"))
                {
                    _staged.Clear();
                }

                _staged.Append(inner);
            }
            else if (command.Contains("base64 -d"))
            {
                Decoded = Convert.FromBase64String(_staged.ToString());
            }

            return Task.FromResult(
                new CommandResult(0, "", "", false));
        }

        public Task<string> ReadFileAsync(
            DomainSandbox s, string p, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task WriteFileAsync(
            DomainSandbox s, string p, string c, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<FileEntry>> ListFilesAsync(
            DomainSandbox s, string p, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FileEntry>>([]);
        public Task<GitDiff> GetGitDiffAsync(
            DomainSandbox s, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static readonly DomainSandbox Sb = new(
        Id: Guid.NewGuid(), SpecId: Guid.NewGuid(),
        ConversationId: null, ContainerId: "c",
        Status: OpenHandsNET.Domain.Sandbox.SandboxStatus.Running,
        AgentUrl: null, CreatedAt: DateTimeOffset.UtcNow,
        ExpiresAt: null, LastError: null);

    // AC: bytes arbitrários sobrevivem (roundtrip b64 chunked + decode).
    [Fact]
    public async Task WriteFileBytes_Roundtrips_Binary()
    {
        var rng = new Random(42);
        var payload = new byte[200 * 1024]; // força ~4 chunks de 64KB b64
        rng.NextBytes(payload);

        var ex = new UploadFakeExecutor();
        await ((ISandboxExecutor)ex).WriteFileBytesAsync(
            Sb, "/workspace/uploads/20261016/a.bin", payload,
            CancellationToken.None);

        ex.Decoded.ShouldBe(payload);
        // staging chunked + decode final com mkdir e cleanup.
        ex.Commands.Count(c => c.StartsWith("echo -n '"))
            .ShouldBeGreaterThan(1);
        ex.Commands.ShouldContain(c => c.Contains("base64 -d")
            && c.Contains("mkdir -p") && c.Contains("rm -f"));
        // nenhum comando excede MAX_ARG_STRLEN (~128KB).
        ex.Commands.Max(c => c.Length).ShouldBeLessThan(100 * 1024);
    }

    // AC: payload pequeno = 1 chunk + decode.
    [Fact]
    public async Task WriteFileBytes_Small_Single_Chunk()
    {
        var ex = new UploadFakeExecutor();
        var payload = Encoding.UTF8.GetBytes("hello");
        await ((ISandboxExecutor)ex).WriteFileBytesAsync(
            Sb, "/workspace/uploads/20261016/h.txt", payload,
            CancellationToken.None);
        ex.Decoded.ShouldBe(payload);
        ex.Commands.Count(c => c.StartsWith("echo -n '")).ShouldBe(1);
    }

}
