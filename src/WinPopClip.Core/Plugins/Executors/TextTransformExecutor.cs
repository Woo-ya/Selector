using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinPopClip.Core.Models;

namespace WinPopClip.Core.Plugins.Executors;

public sealed class TextTransformExecutor : IPluginExecutor
{
    public PluginKind Kind => PluginKind.TextTransform;

    public Task<PluginExecutionResult> ExecuteAsync(
        PluginExecutionRequest request,
        IPluginHostEnvironment host,
        CancellationToken cancellationToken = default)
    {
        var op = (request.Plugin.Action.TransformOperation ?? string.Empty).Trim().ToLowerInvariant();
        var text = request.Context.Text;

        try
        {
            string output = op switch
            {
                "uppercase" => text.ToUpperInvariant(),
                "lowercase" => text.ToLowerInvariant(),
                "titlecase" => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant()),
                "trim" => text.Trim(),
                "single-line" => Regex.Replace(text, @"\s+", " ").Trim(),
                "url-encode" => Uri.EscapeDataString(text),
                "url-decode" => Uri.UnescapeDataString(text),
                "base64-encode" => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
                "base64-decode" => Encoding.UTF8.GetString(Convert.FromBase64String(text.Trim())),
                "json-pretty" => FormatJson(text),
                "regex-replace" => ApplyRegexReplace(text, request.Plugin.Action.TransformPattern, request.Plugin.Action.TransformReplacement),
                _ => throw new InvalidOperationException($"不支持的文本转换操作: {op}")
            };

            return Task.FromResult(PluginExecutionResult.Ok(outputText: output));
        }
        catch (Exception ex)
        {
            return Task.FromResult(PluginExecutionResult.Fail($"文本处理失败: {ex.Message}"));
        }
    }

    private static string FormatJson(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ApplyRegexReplace(string input, string? pattern, string? replacement)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            throw new InvalidOperationException("未配置正则表达式 pattern");
        }
        return Regex.Replace(input, pattern, replacement ?? string.Empty, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(300));
    }
}
