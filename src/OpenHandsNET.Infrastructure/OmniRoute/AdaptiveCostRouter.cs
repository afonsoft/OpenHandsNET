using System;

namespace OpenHandsNET.Infrastructure.OmniRoute;

public enum TaskComplexity
{
    Low,
    Medium,
    High,
    Architectural
}

public interface IAdaptiveCostRouter
{
    string RouteModel(TaskComplexity complexity, string? preferredModel = null);
}

public class AdaptiveCostRouter : IAdaptiveCostRouter
{
    public string RouteModel(TaskComplexity complexity, string? preferredModel = null)
    {
        if (!string.IsNullOrEmpty(preferredModel))
        {
            return preferredModel;
        }

        return complexity switch
        {
            TaskComplexity.Low => "claude-3-5-haiku",
            TaskComplexity.Medium => "gpt-4o-mini",
            TaskComplexity.High => "claude-3-5-sonnet",
            TaskComplexity.Architectural => "claude-3-opus",
            _ => "claude-3-5-sonnet"
        };
    }
}