namespace OpenHandsNET.Infrastructure.Artifacts;

/// <summary>SPEC-34 RF-002: configuração do artifact store.</summary>
public sealed class ArtifactOptions
{
    /// <summary>Configuration section (<c>Artifacts</c>).</summary>
    public const string Section = "Artifacts";

    /// <summary>Provider: <c>workspace</c> (default) ou <c>s3</c>.</summary>
    public string Provider { get; set; } = "workspace";

    /// <summary>Raiz do provider workspace (default <c>/tmp/ohn-artifacts</c>).</summary>
    public string WorkspaceRoot { get; set; } = "/tmp/ohn-artifacts";

    /// <summary>
    /// SPEC-34 RF-003: dias de retenção para artefatos cujo sandbox foi
    /// removido (reaper opcional; <see langword="null"/> = sem reaper).
    /// </summary>
    public int? RetentionDays { get; set; }

    /// <summary>Opções do provider S3-compatible (MinIO/AWS/GCS).</summary>
    public ArtifactS3Options S3 { get; set; } = new();
}

/// <summary>SPEC-34 RF-002: endpoint S3-compatible + credenciais.</summary>
public sealed class ArtifactS3Options
{
    /// <summary>Endpoint do serviço (ex.: <c>http://localhost:9000</c> MinIO).</summary>
    public string ServiceUrl { get; set; } = "";

    /// <summary>Bucket destino (default <c>openhandsnet-artifacts</c>).</summary>
    public string Bucket { get; set; } = "openhandsnet-artifacts";

    /// <summary>Access key (nunca logada/serializada).</summary>
    public string AccessKey { get; set; } = "";

    /// <summary>Secret key (nunca logada/serializada).</summary>
    public string SecretKey { get; set; } = "";

    /// <summary>Região (AWS; ignorada por MinIO).</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>Path-style addressing (obrigatório p/ MinIO).</summary>
    public bool ForcePathStyle { get; set; } = true;
}