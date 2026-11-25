using System.Diagnostics;
using System.Text;
using WinPopClip.Core.Models;

namespace WinPopClip.Core.Plugins.Executors;

public sealed class ShellCommandExecutor : IPluginExecutor
{
    public PluginKind Kind => PluginKind.Shell;

    public async Task<PluginExecutionResult> ExecuteAsync(
        PluginExecutionRequest request,
        IPluginHostEnvironment host,
        CancellationToken cancellationToken = default)
    {
        var cfg = request.Plugin.Action;
        var executable = string.IsNullOrWhiteSpace(cfg.ShellExecutable) ? "powershell" : cfg.ShellExecutable.Trim();
        var timeoutMs = Math.Clamp(cfg.TimeoutMs <= 0 ? 10000 : cfg.TimeoutMs, 500, 60000);

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = cfg.PassTextViaStdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Pass context via environment variables (Selector + legacy WinPopClip compatibility)
        psi.Environment["SELECTOR_TEXT"] = request.Context.Text;
        psi.Environment["SELECTOR_APP"] = request.Context.ProcessName;
        psi.Environment["SELECTOR_TITLE"] = request.Context.WindowTitle;
        psi.Environment["WINPOPCLIP_TEXT"] = request.Context.Text;
        psi.Environment["WINPOPCLIP_APP"] = request.Context.ProcessName;
        psi.Environment["WINPOPCLIP_TITLE"] = request.Context.WindowTitle;
        foreach (var opt in request.Options)
        {
            psi.Environment[$"SELECTOR_OPTION_{opt.Key.ToUpperInvariant()}"] = opt.Value;
            psi.Environment[$"WINPOPCLIP_OPTION_{opt.Key.ToUpperInvariant()}"] = opt.Value;
        }

        if (!string.IsNullOrWhiteSpace(cfg.ShellScript) &&
            (executable.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
             executable.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
             executable.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
             executable.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)))
        {
            var renderedScript = TemplateRenderer.Render(cfg.ShellScript, request);
            var wrappedScript = "[Console]::InputEncoding = [System.Text.Encoding]::UTF8; [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " + renderedScript;
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrappedScript));
            psi.Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}";
        }
        else if (!string.IsNullOrWhiteSpace(cfg.ShellArguments))
        {
            psi.Arguments = TemplateRenderer.Render(cfg.ShellArguments, request);
        }
        else if (!string.IsNullOrWhiteSpace(cfg.ShellScript))
        {
            psi.Arguments = TemplateRenderer.Render(cfg.ShellScript, request);
        }

        using var process = new Process { StartInfo = psi };
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeoutMs);

        if (!process.Start())
        {
            return PluginExecutionResult.Fail($"无法启动进程: {executable}");
        }

        if (cfg.PassTextViaStdin)
        {
            await using var writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false));
            await writer.WriteAsync(request.Context.Text);
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
            return PluginExecutionResult.Fail($"脚本执行超时 ({timeoutMs}ms)");
        }

        var stdout = (await stdOutTask).TrimEnd('\r', '\n');
        var stderr = (await stdErrTask).Trim();

        if (process.ExitCode != 0)
        {
            return PluginExecutionResult.Fail(string.IsNullOrWhiteSpace(stderr)
                ? $"脚本退出码 {process.ExitCode}"
                : stderr);
        }

        return PluginExecutionResult.Ok(outputText: stdout);
    }
}
