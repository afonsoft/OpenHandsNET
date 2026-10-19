using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace OpenHandsNET.Api.ProblemDetails;

/// <summary>
/// RFC 9457 problem+json responses with upstream-identical <c>detail</c>
/// strings where they exist (SPEC-04 RF-006).
/// </summary>
public static class OpenHandsNetProblemFactory
{
    /// <summary>Upstream text: sandbox not running (409 on send-message).</summary>
    public const string SandboxNotRunning =
        "Sandbox is not running. Resume it first via POST /sandboxes/{id}/resume";

    /// <summary>Upstream text: conversation deleted (410).</summary>
    public const string ConversationGone = "Conversation is archived or deleted";

    /// <summary>Upstream text: sandbox error (503).</summary>
    public const string SandboxError = "Sandbox is in an error state";

    /// <summary>Builds a ProblemDetails result.</summary>
    public static IResult Create(HttpContext http, int status, string detail) =>
        Results.Problem(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = status,
            Title = Reason(status),
            Detail = detail,
            Instance = http.Request.Path,
        });

    private static string Reason(int status) => status switch
    {
        400 => "Bad Request",
        404 => "Not Found",
        409 => "Conflict",
        410 => "Gone",
        501 => "Not Implemented",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "Error",
    };
}