using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WinPopClip.Core.Models;

namespace WinPopClip.Core.Plugins.Executors;

public sealed class HttpWebhookExecutor : IPluginExecutor
{
    private readonly HttpClient _httpClient;

    public HttpWebhookExecutor(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public PluginKind Kind => PluginKind.Http;

    public async Task<PluginExecutionResult> ExecuteAsync(
        PluginExecutionRequest request,
        IPluginHostEnvironment host,
        CancellationToken cancellationToken = default)
    {
        var cfg = request.Plugin.Action;
        if (string.IsNullOrWhiteSpace(cfg.HttpUrl))
        {
            return PluginExecutionResult.Fail("未配置 HTTP 请求 URL");
        }

        var url = TemplateRenderer.Render(cfg.HttpUrl, request).Trim();
        var methodName = string.IsNullOrWhiteSpace(cfg.HttpMethod) ? "POST" : cfg.HttpMethod.Trim().ToUpperInvariant();
        using var message = new HttpRequestMessage(new HttpMethod(methodName), url);

        foreach (var header in cfg.HttpHeaders)
        {
            var headerVal = TemplateRenderer.Render(header.Value, request);
            if (!message.Headers.TryAddWithoutValidation(header.Key, headerVal))
            {
                // Content-Type will be handled on HttpContent if provided
            }
        }

        if (!string.IsNullOrWhiteSpace(cfg.HttpBodyTemplate) && methodName != "GET" && methodName != "HEAD")
        {
            var body = TemplateRenderer.Render(cfg.HttpBodyTemplate, request);
            var contentType = cfg.HttpHeaders.TryGetValue("Content-Type", out var ct) && !string.IsNullOrWhiteSpace(ct)
                ? ct
                : "application/json";
            message.Content = new StringContent(body, Encoding.UTF8);
            message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return PluginExecutionResult.Fail($"HTTP {(int)response.StatusCode}: {responseText[..Math.Min(responseText.Length, 160)]}");
        }

        var extracted = ExtractJsonPath(responseText, cfg.HttpResponseJsonPath) ?? responseText.Trim();
        return PluginExecutionResult.Ok(outputText: extracted);
    }

    /// <summary>
    /// Simple dot/index path extractor (e.g. "choices.0.message.content" or "data.result").
    /// </summary>
    public static string? ExtractJsonPath(string json, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement current = doc.RootElement;
            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index))
                {
                    if (index < 0 || index >= current.GetArrayLength()) return null;
                    current = current[index];
                }
                else if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var prop))
                {
                    current = prop;
                }
                else
                {
                    return null;
                }
            }

            return current.ValueKind == JsonValueKind.String ? current.GetString() : current.GetRawText();
        }
        catch
        {
            return null;
        }
    }
}
