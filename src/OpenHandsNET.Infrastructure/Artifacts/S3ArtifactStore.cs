using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Artifacts;

namespace OpenHandsNET.Infrastructure.Artifacts;

/// <summary>
/// Seam mínimo de backend S3 — isola o store do SDK AWS completo
/// (testes injetam um fake sem implementar IAmazonS3 inteiro).
/// </summary>
internal interface IS3Backend
{
    Task PutAsync(string bucket, string key, byte[] data, bool sse,
        CancellationToken ct);
    /// <summary>Null quando a chave não existe.</summary>
    Task<Stream?> GetAsync(string bucket, string key, CancellationToken ct);
    Task DeletePrefixAsync(string bucket, string prefix, CancellationToken ct);
}

/// <summary>Backend real via AWS SDK (S3/MinIO/GCS compatible).</summary>
internal sealed class AwsS3Backend : IS3Backend, IDisposable
{
    private readonly IAmazonS3 _s3;

    public AwsS3Backend(ArtifactS3Options opt)
    {
        var cfg = new AmazonS3Config
        {
            ServiceURL = opt.ServiceUrl,
            ForcePathStyle = opt.ForcePathStyle,
        };
        if (!string.IsNullOrEmpty(opt.Region))
        {
            cfg.AuthenticationRegion = opt.Region;
        }

        _s3 = new AmazonS3Client(
            new BasicAWSCredentials(opt.AccessKey, opt.SecretKey), cfg);
    }

    /// <inheritdoc />
    public async Task PutAsync(string bucket, string key, byte[] data,
        bool sse, CancellationToken ct) =>
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = new MemoryStream(data),
            // SSE-S3 quando suportado (AWS); MinIO ignora se desabilitado.
            ServerSideEncryptionMethod = sse
                ? ServerSideEncryptionMethod.AES256
                : ServerSideEncryptionMethod.None,
        }, ct);

    /// <inheritdoc />
    public async Task<Stream?> GetAsync(string bucket, string key,
        CancellationToken ct)
    {
        try
        {
            var res = await _s3.GetObjectAsync(bucket, key, ct);
            return res.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (
            ex.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task DeletePrefixAsync(string bucket, string prefix,
        CancellationToken ct)
    {
        string? token = null;
        do
        {
            var page = await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                ContinuationToken = token,
            }, ct);
            if (page.S3Objects.Count > 0)
            {
                await _s3.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = bucket,
                    Objects = page.S3Objects
                        .Select(o => new KeyVersion { Key = o.Key })
                        .ToList(),
                }, ct);
            }

            token = page.IsTruncated == true
                ? page.NextContinuationToken : null;
        }
        while (token is not null);
    }

    /// <inheritdoc />
    public void Dispose() => _s3.Dispose();
}

/// <summary>
/// SPEC-34 RF-002: provider opt-in S3-compatible (MinIO/AWS/GCS).
/// Ativado por <c>Artifacts:Provider=s3</c>; chave
/// <c>{conversationId}/{ulid}-{name}</c> no bucket configurado
/// (default <c>openhandsnet-artifacts</c>). SSE-S3 quando o backend
/// suporta (AWS); presign fica para trabalho futuro.
/// </summary>
public sealed class S3ArtifactStore : IArtifactStore, IDisposable
{
    private readonly IS3Backend _backend;
    private readonly ArtifactS3Options _opt;
    private readonly ILogger<S3ArtifactStore> _log;

    /// <summary>Constrói o backend S3 a partir das opções.</summary>
    public S3ArtifactStore(IOptions<ArtifactOptions> opt,
        ILogger<S3ArtifactStore> log)
        : this(new AwsS3Backend(opt.Value.S3), opt.Value.S3, log)
    {
    }

    /// <summary>Test hook: injeta um backend fake.</summary>
    internal S3ArtifactStore(IS3Backend backend, ArtifactS3Options opt,
        ILogger<S3ArtifactStore> log)
    {
        _backend = backend;
        _opt = opt;
        _log = log;
    }

    /// <inheritdoc />
    public async Task<string> PutAsync(Guid conversationId, string name,
        ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var id = $"{Ulid.NewUlid()}-{Sanitize(name)}";
        await _backend.PutAsync(_opt.Bucket,
            Key(conversationId, id), data.ToArray(), sse: true, ct);
        return $"art:{id}";
    }

    /// <inheritdoc />
    public Task<Stream?> OpenAsync(Guid conversationId,
        string artifactRef, CancellationToken ct) =>
        artifactRef.StartsWith("art:", StringComparison.Ordinal)
            ? _backend.GetAsync(_opt.Bucket,
                Key(conversationId, artifactRef[4..]), ct)
            : Task.FromResult<Stream?>(null);

    /// <inheritdoc />
    public async Task DeleteConversationAsync(Guid conversationId,
        CancellationToken ct)
    {
        try
        {
            await _backend.DeletePrefixAsync(
                _opt.Bucket, $"{conversationId}/", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "s3 artifact cleanup failed for conversation {Id}",
                conversationId);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_backend is IDisposable d)
        {
            d.Dispose();
        }
    }

    private static string Key(Guid conversationId, string id) =>
        $"{conversationId}/{id}";

    private static string Sanitize(string name)
    {
        var safe = new string(name.Select(c =>
            char.IsLetterOrDigit(c) || c is '.' or '-' or '_'
                ? c : '-').ToArray());
        return safe.Length is 0 or > 120 ? "artifact.bin" : safe;
    }
}
