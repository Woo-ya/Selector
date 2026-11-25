namespace WinPopClip.Core.Models;

public sealed class PluginExecutionRequest
{
    public required PluginManifest Plugin { get; init; }
    public required SelectionContext Context { get; init; }
    public required IReadOnlyDictionary<string, string> Options { get; init; }
    public string? RegexMatchValue { get; init; }
    public IReadOnlyList<string> RegexGroups { get; init; } = Array.Empty<string>();
}

public sealed class PluginExecutionResult
{
    public bool Success { get; init; }
    public string? OutputText { get; init; }
    public string? StatusMessage { get; init; }
    public string? ErrorMessage { get; init; }
    public AfterActionType? OverrideAfter { get; init; }
    public bool SuppressAfterHandling { get; init; }

    public static PluginExecutionResult Ok(
        string? outputText = null,
        string? statusMessage = null,
        AfterActionType? overrideAfter = null,
        bool suppressAfterHandling = false) => new()
    {
        Success = true,
        OutputText = outputText,
        StatusMessage = statusMessage,
        OverrideAfter = overrideAfter,
        SuppressAfterHandling = suppressAfterHandling
    };

    public static PluginExecutionResult Fail(string errorMessage) => new()
    {
        Success = false,
        ErrorMessage = errorMessage
    };
}
