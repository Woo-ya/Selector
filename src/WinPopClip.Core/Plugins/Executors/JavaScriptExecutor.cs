using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WinPopClip.Core.Models;

namespace WinPopClip.Core.Plugins.Executors;

/// <summary>
/// Executes PopClip-compatible JavaScript snippets using Node.js with an injected global `popclip` API:
/// popclip.input.text, popclip.input.matchedText, popclip.input.data.{urls,emails},
/// popclip.options, popclip.context, popclip.showText(), popclip.copyText(), popclip.pasteText(), popclip.openUrl().
/// </summary>
public sealed class JavaScriptExecutor : IPluginExecutor
{
    public PluginKind Kind => PluginKind.JavaScript;

    private const string RuntimeHarness = """
const fs = require('fs');
(async () => {
  const payload = JSON.parse(fs.readFileSync(0, 'utf8'));
  const sideEffects = [];
  const allowNetwork = Array.isArray(payload.entitlements) && payload.entitlements.some(e => String(e).toLowerCase() === 'network');
  if (!allowNetwork && typeof globalThis.fetch === 'function') {
    globalThis.fetch = () => Promise.reject(new Error('此 PopClip 脚本未声明 entitlements: [network]，已禁止网络访问'));
  }

  const popclip = {
    input: {
      text: payload.text || '',
      matchedText: payload.matchedText || payload.text || '',
      data: {
        urls: payload.urls || [],
        emails: payload.emails || []
      }
    },
    options: payload.options || {},
    context: {
      appName: payload.appName || '',
      windowTitle: payload.windowTitle || ''
    },
    showText(msg) { sideEffects.push({ type: 'showText', value: String(msg ?? '') }); },
    copyText(txt) { sideEffects.push({ type: 'copyText', value: String(txt ?? '') }); },
    pasteText(txt) { sideEffects.push({ type: 'pasteText', value: String(txt ?? '') }); },
    openUrl(url) { sideEffects.push({ type: 'openUrl', value: String(url ?? '') }); }
  };

  globalThis.popclip = popclip;

  const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor;
  const fn = new AsyncFunction('popclip', payload.code);
  const returned = await fn(popclip);

  const out = {
    ok: true,
    returned: returned === undefined || returned === null ? null : String(returned),
    sideEffects
  };
  process.stdout.write(JSON.stringify(out));
})().catch(err => {
  process.stdout.write(JSON.stringify({ ok: false, error: err && err.message ? err.message : String(err) }));
  process.exitCode = 1;
});
""";

    public async Task<PluginExecutionResult> ExecuteAsync(
        PluginExecutionRequest request,
        IPluginHostEnvironment host,
        CancellationToken cancellationToken = default)
    {
        var code = request.Plugin.Action.JavaScriptCode;
        if (string.IsNullOrWhiteSpace(code))
        {
            return PluginExecutionResult.Fail("JavaScript 脚本内容为空");
        }

        var timeoutMs = Math.Clamp(request.Plugin.Action.TimeoutMs <= 0 ? 10000 : request.Plugin.Action.TimeoutMs, 500, 60000);
        var payloadJson = JsonSerializer.Serialize(new
        {
            code,
            text = request.Context.Text,
            matchedText = request.RegexMatchValue ?? request.Context.Text,
            urls = request.Context.DetectedUrls,
            emails = request.Context.DetectedEmails,
            options = request.Options,
            appName = request.Context.ProcessName,
            windowTitle = request.Context.WindowTitle,
            entitlements = request.Plugin.Action.Entitlements
        });

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = "-e " + QuoteArg(RuntimeHarness),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                return PluginExecutionResult.Fail("无法启动 Node.js 运行时");
            }
        }
        catch (Exception ex)
        {
            return PluginExecutionResult.Fail($"运行 PopClip JS 插件需要本机安装 Node.js：{ex.Message}");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeoutMs);

        await using (var writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
        {
            await writer.WriteAsync(payloadJson);
            await writer.FlushAsync();
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
        var stdErrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return PluginExecutionResult.Fail($"JS 插件执行超时 ({timeoutMs}ms)");
        }

        var stdout = (await stdOutTask).Trim();
        var stderr = (await stdErrTask).Trim();

        if (string.IsNullOrWhiteSpace(stdout))
        {
            return PluginExecutionResult.Fail(string.IsNullOrWhiteSpace(stderr) ? "JS 脚本未返回有效输出" : stderr);
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            if (!ok)
            {
                var errMsg = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : stderr;
                return PluginExecutionResult.Fail($"JS 执行错误: {errMsg}");
            }

            var hasSideEffect = false;
            if (root.TryGetProperty("sideEffects", out var effects) && effects.ValueKind == JsonValueKind.Array)
            {
                foreach (var eff in effects.EnumerateArray())
                {
                    var type = eff.GetProperty("type").GetString();
                    var val = eff.GetProperty("value").GetString() ?? string.Empty;
                    hasSideEffect = true;
                    switch (type)
                    {
                        case "showText":
                            host.ShowToast(val, request.Context.AnchorPoint);
                            break;
                        case "copyText":
                            await host.CopyTextAsync(val, cancellationToken);
                            break;
                        case "pasteText":
                            if (request.Context.IsEditable)
                                await host.PasteTextAsync(val, restorePreviousClipboard: true, cancellationToken);
                            else
                                await host.CopyTextAsync(val, cancellationToken);
                            break;
                        case "openUrl":
                            await host.OpenUrlAsync(val, cancellationToken);
                            break;
                    }
                }
            }

            var returned = root.TryGetProperty("returned", out var retEl) && retEl.ValueKind == JsonValueKind.String
                ? retEl.GetString()
                : null;

            return PluginExecutionResult.Ok(
                outputText: returned,
                suppressAfterHandling: returned is null && hasSideEffect);
        }
        catch (Exception ex)
        {
            return PluginExecutionResult.Fail($"解析 JS 插件输出失败: {ex.Message}");
        }
    }

    private static string QuoteArg(string arg) => "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
