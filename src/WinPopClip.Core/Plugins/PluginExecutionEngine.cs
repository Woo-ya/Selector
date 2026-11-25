using WinPopClip.Core.Models;

namespace WinPopClip.Core.Plugins;

/// <summary>
/// Dispatches plugin executions to registered IPluginExecutor instances and handles post-action lifecycle (after).
/// </summary>
public sealed class PluginExecutionEngine
{
    private readonly Dictionary<PluginKind, IPluginExecutor> _executors = new();
    private readonly Dictionary<string, IPluginAction> _programmaticActions = new(StringComparer.OrdinalIgnoreCase);

    public PluginExecutionEngine(IEnumerable<IPluginExecutor> executors)
    {
        foreach (var executor in executors)
        {
            RegisterExecutor(executor);
        }
    }

    public void RegisterExecutor(IPluginExecutor executor)
    {
        _executors[executor.Kind] = executor;
    }

    public void RegisterProgrammaticAction(IPluginAction action)
    {
        _programmaticActions[action.Metadata.Id] = action;
    }

    public async Task<PluginExecutionResult> ExecuteAsync(
        PluginExecutionRequest request,
        IPluginHostEnvironment host,
        CancellationToken cancellationToken = default)
    {
        PluginExecutionResult result;
        try
        {
            if (_programmaticActions.TryGetValue(request.Plugin.Id, out var programmatic))
            {
                result = await programmatic.ExecuteAsync(request, host, cancellationToken);
            }
            else if (_executors.TryGetValue(request.Plugin.Kind, out var executor))
            {
                result = await executor.ExecuteAsync(request, host, cancellationToken);
            }
            else
            {
                return PluginExecutionResult.Fail($"未注册类型为 {request.Plugin.Kind} 的插件执行器");
            }
        }
        catch (OperationCanceledException)
        {
            return PluginExecutionResult.Fail("插件执行超时或已取消");
        }
        catch (Exception ex)
        {
            return PluginExecutionResult.Fail($"插件执行异常: {ex.Message}");
        }

        if (!result.Success)
        {
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                host.ShowToast(result.ErrorMessage, request.Context.AnchorPoint);
            }
            return result;
        }

        if (result.SuppressAfterHandling)
        {
            return result;
        }

        var effectiveAfter = result.OverrideAfter ?? request.Plugin.After;
        var output = result.OutputText;

        switch (effectiveAfter)
        {
            case AfterActionType.CopyResult when output is not null:
                await host.CopyTextAsync(output, cancellationToken);
                host.ShowToast(result.StatusMessage ?? "已复制结果", request.Context.AnchorPoint);
                break;

            case AfterActionType.PasteResult when output is not null:
                if (request.Context.IsEditable)
                {
                    await host.PasteTextAsync(output, restorePreviousClipboard: true, cancellationToken);
                }
                else
                {
                    await host.CopyTextAsync(output, cancellationToken);
                    host.ShowToast("当前区域不可编辑，已复制结果", request.Context.AnchorPoint);
                }
                break;

            case AfterActionType.ShowResult:
                var msg = output ?? result.StatusMessage ?? "执行完成";
                host.ShowToast(msg, request.Context.AnchorPoint);
                break;

            default:
                if (!string.IsNullOrWhiteSpace(result.StatusMessage))
                {
                    host.ShowToast(result.StatusMessage, request.Context.AnchorPoint);
                }
                break;
        }

        return result;
    }
}
