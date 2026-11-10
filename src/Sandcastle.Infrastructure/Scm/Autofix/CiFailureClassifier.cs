using Sandcastle.Application.Scm;

namespace Sandcastle.Infrastructure.Scm.Autofix;

/// <summary>
/// SPEC-141 RF-002/RF-003 — heurística determinística de classificação
/// do log de falha: classes não-autofixáveis (permissão, segredo/env,
/// infra, timeout, flaky) são avaliadas PRIMEIRO no log; depois hints
/// de nome do check e de log para lint/type/test/build; fallback
/// <see cref="CiFailureClass.Unknown"/> (autofixável — o agente tenta).
/// </summary>
public static class CiFailureClassifier
{
    /// <summary>Classes que NÃO disparam fix (bail-out — RF-002/RF-003).</summary>
    public static bool IsFixable(CiFailureClass kind) => kind is not
        (CiFailureClass.Permission
            or CiFailureClass.Secrets
            or CiFailureClass.Infra
            or CiFailureClass.Timeout
            or CiFailureClass.Flaky);

    /// <summary>Classifica pelo nome do check + cauda do log (redigida).</summary>
    public static CiFailureClass Classify(
        string? checkName, string? logTail,
        IReadOnlyList<string>? flakyPatterns)
    {
        var name = checkName ?? "";
        var log = logTail ?? "";

        // 1) bail-outs — sempre no log (um job de lint pode falhar por
        //    permissão de checkout; isso continua sendo não-autofixável).
        if (ContainsAny(log, PermissionNeedles)
            || ContainsAny(name, PermissionNeedles))
        {
            return CiFailureClass.Permission;
        }
        if (ContainsSecretOrEnvFailure(log))
        {
            return CiFailureClass.Secrets;
        }
        if (ContainsAny(log, InfraNeedles))
        {
            return CiFailureClass.Infra;
        }
        if (ContainsAny(log, TimeoutNeedles)
            || ContainsAny(name, TimeoutNeedles))
        {
            return CiFailureClass.Timeout;
        }
        if (flakyPatterns is not null
            && ContainsAny(log, flakyPatterns)
            || ContainsAny(log, FlakyNeedles))
        {
            return CiFailureClass.Flaky;
        }

        // 2) hints no nome do check (mais confiável que o log).
        if (ContainsAny(name, LintNameHints))
        {
            return CiFailureClass.Lint;
        }
        if (ContainsAny(name, TypeNameHints))
        {
            return CiFailureClass.Type;
        }
        if (ContainsAny(name, TestNameHints))
        {
            return CiFailureClass.Test;
        }
        if (ContainsAny(name, BuildNameHints))
        {
            return CiFailureClass.Build;
        }

        // 3) hints no log.
        if (ContainsAny(log, LintLogHints))
        {
            return CiFailureClass.Lint;
        }
        if (ContainsAny(log, TypeLogHints))
        {
            return CiFailureClass.Type;
        }
        if (ContainsAny(log, TestLogHints))
        {
            return CiFailureClass.Test;
        }
        if (ContainsAny(log, BuildLogHints))
        {
            return CiFailureClass.Build;
        }
        return CiFailureClass.Unknown;
    }

    /// <summary>Descrição curta do bail-out p/ comentário/erro.</summary>
    public static string Describe(CiFailureClass kind) => kind switch
    {
        CiFailureClass.Permission =>
            "the failure looks like a permission/access issue",
        CiFailureClass.Secrets =>
            "the failure looks like a missing secret or environment variable",
        CiFailureClass.Infra =>
            "the failure looks like CI infrastructure (runner/docker/network)",
        CiFailureClass.Timeout =>
            "the failure is a job timeout/cancellation",
        CiFailureClass.Flaky =>
            "the failure matches a flaky/transient signature",
        _ => "the failure cannot be fixed by a code change",
    };

    private static bool ContainsAny(string haystack, IEnumerable<string> needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsSecretOrEnvFailure(string log)
    {
        // "env var X is not set", "SECRET_X missing", "Input required and
        // not supplied: token", "api key not configured"…
        foreach (var anchor in EnvAnchors)
        {
            var idx = log.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }
            var window = log[idx..Math.Min(log.Length, idx + 220)];
            if (MissingMarkers.Any(m => window.Contains(
                    m, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    private static readonly string[] PermissionNeedles =
    [
        "permission denied", "not accessible by integration",
        "insufficient permission", "requires authentication",
        "authentication failed", "could not read username",
        "eacces", "403 forbidden", "http 403", "status 403",
        "access denied", "write access to repository",
        "resource not accessible",
    ];

    private static readonly string[] EnvAnchors =
    [
        "secret", "environment variable", "env var", "variable",
        "api key", "apikey", "api_key", "token", "credential",
        "input required",
    ];

    private static readonly string[] MissingMarkers =
    [
        "not set", "not defined", "missing", "is required",
        "not supplied", "not provided", "not configured",
        "unavailable", "not found",
    ];

    private static readonly string[] InfraNeedles =
    [
        "no space left on device", "cannot connect to the docker daemon",
        "docker daemon is not running", "error response from daemon",
        "network is unreachable", "could not resolve host",
        "name or service not known", "eai_again",
        "temporary failure in name resolution",
        "runner is unavailable", "waiting for a runner",
        "self-hosted runner", "lost communication with the server",
        "rate limit", "502 bad gateway", "503 service",
        "service unavailable", "internal server error",
        "failed to fetch", "unable to locate package",
        "npm err! network", "unable to restore", "failed to download package",
        "connection refused", "connection reset",
    ];

    private static readonly string[] TimeoutNeedles =
    [
        "timed out", "timeout", "deadline exceeded",
        "operation was canceled", "the operation was cancelled",
        "canceled by", "cancelled by",
        "received a shutdown signal", "execution expired",
    ];

    private static readonly string[] FlakyNeedles =
    [
        "flaky", "intermittent", "transient failure",
        "retry attempt", "segfault", "segmentation fault",
    ];

    private static readonly string[] LintNameHints =
    [
        "lint", "format", "prettier", "eslint", "flake8", "rubocop",
        "checkstyle", "golangci", "clippy", "shellcheck", "hadolint",
        "stylecop", "black", "ktlint", "swiftlint", "style",
    ];

    private static readonly string[] TypeNameHints =
    [
        "typecheck", "type-check", "type check", "tsc", "mypy",
        "pyright", "typecheck", "static analysis", "sast",
    ];

    private static readonly string[] TestNameHints =
    [
        "test", "jest", "pytest", "xunit", "nunit", "spec",
        "integration", "e2e", "coverage", "unittest",
    ];

    private static readonly string[] BuildNameHints =
    [
        "build", "compile", "msbuild", "webpack", "vite", "bundle",
        "packaging", "publish",
    ];

    private static readonly string[] LintLogHints =
    [
        "eslint error", "prettier --check", "lint error",
        "would reformat", "gofmt", "goimports",
        "dotnet format", "flake8 ", "rubocop",
    ];

    private static readonly string[] TypeLogHints =
    [
        "error ts", ": error cs", "mypy:", "type error",
        "cannot find name", "does not exist on type",
        "not assignable to type", "typecheck failed",
        "argument of type", "expected type",
    ];

    private static readonly string[] TestLogHints =
    [
        "tests failed", "test failed", "failing tests",
        "assertionerror", "assertion failed", "expected:",
        "actual:", "✕", "[failed]", "failed:",
        "expected true", "expected false", "xunit.net",
        "shouldly", "junit",
    ];

    private static readonly string[] BuildLogHints =
    [
        "build failed", "error msb", "compilation failed",
        "cannot compile", "compile error", "webpack error",
        "msbuild error", "csc : error", "link error",
        "fatal error", "build error",
    ];
}