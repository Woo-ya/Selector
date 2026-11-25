using System.Text.Json;
using System.Text.RegularExpressions;
using WinPopClip.Core.Models;

namespace WinPopClip.Core.Plugins;

/// <summary>
/// Renders template placeholders for URL, Shell, and HTTP plugins:
/// {text}, {trimmedText}, {encodedText}, {jsonText}, {app}, {title}, {match}, {group.1}, {option.key}
/// </summary>
public static partial class TemplateRenderer
{
    [GeneratedRegex(@"\{([a-zA-Z0-9_.-]+)\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();

    public static string Render(string template, PluginExecutionRequest request)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return PlaceholderRegex().Replace(template, match =>
        {
            var token = match.Groups[1].Value;
            return ResolveToken(token, request) ?? match.Value;
        });
    }

    private static string? ResolveToken(string token, PluginExecutionRequest request)
    {
        var text = request.Context.Text;
        var trimmed = request.Context.TrimmedText;

        if (token.Equals("text", StringComparison.OrdinalIgnoreCase))
            return text;
        if (token.Equals("trimmedText", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        if (token.Equals("encodedText", StringComparison.OrdinalIgnoreCase))
            return Uri.EscapeDataString(trimmed);
        if (token.Equals("jsonText", StringComparison.OrdinalIgnoreCase))
        {
            // Returns JSON-escaped string content without outer quotes so users can write "{\"q\":\"{jsonText}\"}"
            var serialized = JsonSerializer.Serialize(text);
            return serialized.Length >= 2 ? serialized[1..^1] : string.Empty;
        }
        if (token.Equals("app", StringComparison.OrdinalIgnoreCase))
            return request.Context.ProcessName;
        if (token.Equals("title", StringComparison.OrdinalIgnoreCase))
            return request.Context.WindowTitle;
        if (token.Equals("match", StringComparison.OrdinalIgnoreCase))
            return request.RegexMatchValue ?? trimmed;

        if (token.StartsWith("group.", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(token["group.".Length..], out var idx) && idx >= 0 && idx < request.RegexGroups.Count)
            {
                return request.RegexGroups[idx];
            }
            return string.Empty;
        }

        if (token.StartsWith("option.", StringComparison.OrdinalIgnoreCase))
        {
            var optionKey = token["option.".Length..];
            if (request.Options.TryGetValue(optionKey, out var value))
            {
                return value;
            }
            var defaultVal = request.Plugin.Options
                .FirstOrDefault(o => o.Key.Equals(optionKey, StringComparison.OrdinalIgnoreCase))
                ?.DefaultValue;
            return defaultVal ?? string.Empty;
        }

        return null;
    }
}
