using WinPopClip.Core.Models;

namespace WinPopClip.Core.Plugins.Executors;

public sealed class UrlTemplateExecutor : IPluginExecutor
{
    public PluginKind Kind => PluginKind.Url;

    public async Task<PluginExecutionResult> ExecuteAsync(
        PluginExecutionRequest request,
        IPluginHostEnvironment host,
        CancellationToken cancellationToken = default)
    {
        var template = request.Plugin.Action.UrlTemplate;
        if (string.IsNullOrWhiteSpace(template))
        {
            return PluginExecutionResult.Fail("URL 模板为空");
        }

        var rendered = TemplateRenderer.Render(template, request).Trim();
        if (!Uri.TryCreate(rendered, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "mailto"))
        {
            return PluginExecutionResult.Fail($"无效的目标链接: {rendered}");
        }

        await host.OpenUrlAsync(uri.AbsoluteUri, cancellationToken);
        return PluginExecutionResult.Ok(outputText: uri.AbsoluteUri);
    }
}
