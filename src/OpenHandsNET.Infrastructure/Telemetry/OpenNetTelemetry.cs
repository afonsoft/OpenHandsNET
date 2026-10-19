using System.Diagnostics;
using System.Diagnostics.Metrics;

using Prometheus;

namespace OpenHandsNET.Infrastructure.Telemetry;

/// <summary>
/// Central telemetry registry (SPEC-13 RF-004): ActivitySource spans and
/// counters exported via /metrics (prometheus-net) and OTLP.
/// Metric names map 1:1 — the prometheus-net collector appends
/// <c>_total</c> automatically to counters.
/// </summary>
public static class OpenNetTelemetry
{
    /// <summary>Trace source name — registered in Program.cs.</summary>
    public const string ActivitySourceName = "OpenHandsNet";

    /// <summary>Meter name — registered in Program.cs.</summary>
    public const string MeterName = "OpenHandsNet";

    /// <summary>Shared activity source for custom spans.</summary>
    public static readonly ActivitySource Source = new(ActivitySourceName);

    /// <summary>Shared meter (OTLP path).</summary>
    public static readonly Meter Meter = new(MeterName);

    /// <summary>Events appended to the stream.</summary>
    public static readonly Prometheus.Counter EventsAppended =
        Metrics.CreateCounter("openhandsnet_events_appended",
            "Events appended to the conversation stream",
            new CounterConfiguration { LabelNames = ["kind"] });

    /// <summary>Conversations started (start-task → READY).</summary>
    public static readonly Prometheus.Counter ConversationsStarted =
        Metrics.CreateCounter("openhandsnet_conversations_started",
            "App conversations that reached READY");

    /// <summary>LLM token usage.</summary>
    public static readonly Prometheus.Counter LlmTokens =
        Metrics.CreateCounter("openhandsnet_llm_tokens",
            "LLM tokens consumed",
            new CounterConfiguration { LabelNames = ["direction"] });

    /// <summary>Tool calls executed by the agent.</summary>
    public static readonly Prometheus.Counter ToolCalls =
        Metrics.CreateCounter("openhandsnet_tool_calls",
            "Agent tool executions",
            new CounterConfiguration { LabelNames = ["tool"] });

    /// <summary>Sandbox wall-clock seconds (observe on delete).</summary>
    public static readonly Prometheus.Counter SandboxSeconds =
        Metrics.CreateCounter("openhandsnet_sandbox_seconds",
            "Sandbox lifetime seconds");

    /// <summary>Sandboxes paused/stopped for sleep (SPEC-16).</summary>
    public static readonly Prometheus.Counter SandboxSleeps =
        Metrics.CreateCounter("openhandsnet_sandbox_sleeps",
            "Sandbox sleeps (pause/stop)");

    /// <summary>Sandbox wakes — label <c>from</c> = <c>pause|stop</c> (SPEC-16).</summary>
    public static readonly Prometheus.Counter SandboxWakes =
        Metrics.CreateCounter("openhandsnet_sandbox_wakes",
            "Sandbox resumes from sleep",
            new CounterConfiguration { LabelNames = ["from"] });

    /// <summary>SPEC-40 RF-006: requests do gateway OpenAI
    /// (<c>/v1/chat/completions</c>) — label <c>model</c>.</summary>
    public static readonly Prometheus.Counter GatewayRequests =
        Metrics.CreateCounter("openhandsnet_gateway_requests",
            "OpenAI gateway chat completions requests",
            new CounterConfiguration { LabelNames = ["model"] });

    /// <summary>SPEC-40 RF-006: latência do gateway até a resposta
    /// terminal — labels <c>model</c> e <c>stream</c>.</summary>
    public static readonly Prometheus.Histogram GatewayLatency =
        Metrics.CreateHistogram("openhandsnet_gateway_request_seconds",
            "OpenAI gateway completion latency",
            new HistogramConfiguration
            {
                LabelNames = ["model", "stream"],
                Buckets = Histogram.ExponentialBuckets(1, 2, 10),
            });

    /// <summary>Starts a telemetry span with conversation correlation.</summary>
    public static Activity? StartSpan(
        string name, Guid? conversationId = null, string? sandboxId = null)
    {
        var activity = Source.StartActivity(name);
        if (activity is not null)
        {
            if (conversationId is { } cid)
            {
                activity.SetTag("conversation_id", cid.ToString());
            }

            if (sandboxId is not null)
            {
                activity.SetTag("sandbox_id", sandboxId);
            }
        }

        return activity;
    }
}