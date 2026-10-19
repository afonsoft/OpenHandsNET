using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OpenHandsNET.Eval;

/// <summary>
/// SPEC-39 RF-001: schema de uma task avaliável (<c>evals/tasks/*.yaml</c>).
/// </summary>
public sealed class EvalTask
{
    /// <summary>Identificador único do caso.</summary>
    public string Name { get; set; } = "";

    /// <summary>Repo alvo (<c>github.com/org/repo@sha</c>) — opcional.</summary>
    public string? Repo { get; set; }

    /// <summary>Instrução enviada ao agente.</summary>
    public string Task { get; set; } = "";

    /// <summary>Timeout do run (parse: <c>10m</c>, <c>90s</c>, <c>1h</c>).</summary>
    public string Timeout { get; set; } = "10m";

    /// <summary>Comandos pre-run no workspace (exec no sandbox).</summary>
    public List<string> Setup { get; set; } = [];

    /// <summary>Assertions avaliadas após o run.</summary>
    public List<EvalAssert> Assert { get; set; } = [];

    /// <summary>Tags para filtro de suite.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Peso relativo da task na suite (default 1).</summary>
    public double Weight { get; set; } = 1.0;
}

/// <summary>Assert tipado de uma task (RF-001).</summary>
public sealed class EvalAssert
{
    /// <summary><c>exit_code</c>|<c>file_contains</c>|<c>output_contains</c>|<c>llm_judge</c>.</summary>
    public string Type { get; set; } = "";

    /// <summary>Comando (<c>exit_code</c>) executado no sandbox pós-run.</summary>
    public string? Cmd { get; set; }

    /// <summary>Valor esperado (exit code) — default 0.</summary>
    public int? Expect { get; set; }

    /// <summary>Caminho sob <c>/workspace</c> (<c>file_contains</c>).</summary>
    public string? Path { get; set; }

    /// <summary>Substring esperada no arquivo ou saída do agente.</summary>
    public string? Pattern { get; set; }

    /// <summary>Rubrica avaliada pelo juiz LLM (<c>llm_judge</c>).</summary>
    public string? Rubric { get; set; }

    /// <summary>Peso da assertion no score (default 1).</summary>
    public double Weight { get; set; } = 1.0;
}

/// <summary>Suite = lista ordenada de referências a tasks.</summary>
public sealed class EvalSuite
{
    /// <summary>Nome da suite.</summary>
    public string Name { get; set; } = "";

    /// <summary>Tasks (nome do arquivo sem extensão em <c>evals/tasks</c> ou caminho).</summary>
    public List<string> Tasks { get; set; } = [];

    /// <summary>Tags — quando presente, roda só tasks marcadas.</summary>
    public List<string> Tags { get; set; } = [];
}

/// <summary>Parser + validador de tasks/suites YAML (SPEC-39 task plan 1).</summary>
public static class EvalParser
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Parse de um arquivo de task.</summary>
    public static EvalTask ParseTask(string yaml)
    {
        var t = Yaml.Deserialize<EvalTask>(yaml)
            ?? throw new InvalidDataException("empty task document");
        return t;
    }

    /// <summary>Parse de um arquivo de suite.</summary>
    public static EvalSuite ParseSuite(string yaml)
    {
        var s = Yaml.Deserialize<EvalSuite>(yaml)
            ?? throw new InvalidDataException("empty suite document");
        return s;
    }

    /// <summary>Timeout em formato <c>Nm|Ns|Nh</c> ou ISO; default 10 min.</summary>
    public static TimeSpan ParseTimeout(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.FromMinutes(10);
        }

        var v = value.Trim();
        var suffix = v[^1];
        if (double.TryParse(v[..^1], out var n))
        {
            return suffix switch
            {
                's' => TimeSpan.FromSeconds(n),
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                _ => TimeSpan.FromMinutes(10),
            };
        }

        return TimeSpan.TryParse(v, out var ts) ? ts : TimeSpan.FromMinutes(10);
    }

    /// <summary>Valida a task; devolve lista de erros (vazia = ok).</summary>
    public static IReadOnlyList<string> Validate(EvalTask t)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(t.Name))
        {
            errors.Add("name is required");
        }

        if (string.IsNullOrWhiteSpace(t.Task))
        {
            errors.Add("task is required");
        }

        if (t.Weight <= 0)
        {
            errors.Add("weight must be > 0");
        }

        foreach (var (a, i) in t.Assert.Select((a, i) => (a, i)))
        {
            var label = $"assert[{i}]";
            switch (a.Type)
            {
                case "exit_code":
                    if (string.IsNullOrWhiteSpace(a.Cmd))
                    {
                        errors.Add($"{label}: exit_code requires cmd");
                    }
                    break;
                case "file_contains":
                    if (string.IsNullOrWhiteSpace(a.Path) || a.Path.Contains(".."))
                    {
                        errors.Add($"{label}: file_contains requires safe path");
                    }
                    if (string.IsNullOrEmpty(a.Pattern))
                    {
                        errors.Add($"{label}: file_contains requires pattern");
                    }
                    break;
                case "output_contains":
                    if (string.IsNullOrEmpty(a.Pattern))
                    {
                        errors.Add($"{label}: output_contains requires pattern");
                    }
                    break;
                case "llm_judge":
                    if (string.IsNullOrWhiteSpace(a.Rubric))
                    {
                        errors.Add($"{label}: llm_judge requires rubric");
                    }
                    break;
                default:
                    errors.Add($"{label}: unknown type '{a.Type}'");
                    break;
            }

            if (a.Weight <= 0)
            {
                errors.Add($"{label}: weight must be > 0");
            }
        }

        return errors;
    }
}