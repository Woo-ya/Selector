using System.Text.RegularExpressions;
using WinPopClip.Core.Models;

namespace WinPopClip.Core.Plugins.Executors;

public sealed partial class BuiltinExecutor : IPluginExecutor
{
    public PluginKind Kind => PluginKind.Builtin;

    [GeneratedRegex(@"\S+", RegexOptions.Compiled)]
    private static partial Regex WordTokenRegex();

    public async Task<PluginExecutionResult> ExecuteAsync(
        PluginExecutionRequest request,
        IPluginHostEnvironment host,
        CancellationToken cancellationToken = default)
    {
        var cmd = (request.Plugin.Action.BuiltinCommand ?? string.Empty).Trim().ToLowerInvariant();
        switch (cmd)
        {
            case "copy":
                if (request.Context.IsDeferredCapture && string.IsNullOrWhiteSpace(request.Context.Text))
                {
                    // Doubao-style single-write copy: send Ctrl+C directly once to the active app (no restore/double clipboard event!)
                    await host.SendCopyShortcutAsync(cancellationToken);
                }
                else
                {
                    await host.CopyTextAsync(request.Context.Text, cancellationToken);
                }
                return PluginExecutionResult.Ok(statusMessage: "已复制", suppressAfterHandling: true);

            case "cut":
                await host.SendCutShortcutAsync(cancellationToken);
                return PluginExecutionResult.Ok(suppressAfterHandling: true);

            case "paste":
                await host.SendPasteShortcutAsync(cancellationToken);
                return PluginExecutionResult.Ok(suppressAfterHandling: true);

            case "open-url":
                var url = request.Context.DetectedUrls.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(url))
                {
                    return PluginExecutionResult.Fail("未检测到有效链接");
                }
                await host.OpenUrlAsync(url, cancellationToken);
                return PluginExecutionResult.Ok(suppressAfterHandling: true);

            case "word-count":
                var text = request.Context.Text;
                var charCount = text.Length;
                var nonSpaceChars = text.Count(c => !char.IsWhiteSpace(c));
                var wordCount = WordTokenRegex().Count(text);
                var lineCount = text.Split('\n').Length;
                var summary = $"{charCount} 字符 ({nonSpaceChars} 非空) · {wordCount} 词 · {lineCount} 行";
                return PluginExecutionResult.Ok(outputText: summary);

            default:
                return PluginExecutionResult.Fail($"未知内置命令: {cmd}");
        }
    }
}
