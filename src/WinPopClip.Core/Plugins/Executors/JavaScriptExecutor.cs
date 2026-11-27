using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WinPopClip.Core.Models;

namespace WinPopClip.Core.Plugins.Executors;

/// <summary>
/// Executes PopClip-compatible JavaScript snippets AND compiled CommonJS/Module extensions
/// (such as Instant Translate, Select All, and official .popclipextz modules) using Node.js with:
/// - Injected global `popclip` API (`pressKey`, `appear`, `showText`, `copyText`, `pasteText`, `openUrl`) + `util.constant` + `defineExtension()`
/// - Deferred `setTimeout` action replay (so `popclip.pressKey(...); setTimeout(() => popclip.appear(), 250)` executes cleanly on Windows)
/// - Built-in `axios` compatibility shim powered by Node native `fetch`
/// - Extension directory module resolution (`require("./langs.json")` etc.)
/// - Automatic `exports.options` default-value merging so options never crash with undefined.
/// </summary>
public sealed class JavaScriptExecutor : IPluginExecutor
{
    public PluginKind Kind => PluginKind.JavaScript;

    private const string RuntimeHarness = """
const fs = require('fs');
const path = require('path');
const Module = require('module');

(async () => {
  const payload = JSON.parse(fs.readFileSync(0, 'utf8'));
  const sideEffects = [];
  const allowNetwork = Array.isArray(payload.entitlements) && payload.entitlements.some(e => String(e).toLowerCase() === 'network');

  // PopClip util.constant compatibility (macOS CGEventFlags & ANSI virtual keycodes)
  const MAC_KEYCODE_TO_NAME = {
    0: 'A', 1: 'S', 2: 'D', 3: 'F', 4: 'H', 5: 'G', 6: 'Z', 7: 'X', 8: 'C', 9: 'V',
    11: 'B', 12: 'Q', 13: 'W', 14: 'E', 15: 'R', 16: 'Y', 17: 'T',
    18: '1', 19: '2', 20: '3', 21: '4', 22: '6', 23: '5', 24: '=', 25: '9', 26: '7', 27: '-', 28: '8', 29: '0',
    30: ']', 31: 'O', 32: 'U', 33: '[', 34: 'I', 35: 'P', 36: 'return',
    37: 'L', 38: 'J', 39: "'", 40: 'K', 41: ';', 42: '\\', 43: ',', 44: '/', 45: 'N', 46: 'M', 47: '.',
    48: 'tab', 49: 'space', 50: '`', 51: 'backspace', 53: 'escape',
    115: 'home', 116: 'pageup', 117: 'delete', 119: 'end', 121: 'pagedown',
    123: 'left', 124: 'right', 125: 'down', 126: 'up'
  };

  const utilConstant = {
    MODIFIER_SHIFT: 0x20000,
    MODIFIER_CONTROL: 0x40000,
    MODIFIER_OPTION: 0x80000,
    MODIFIER_COMMAND: 0x100000,
    KEY_A: 0, KEY_S: 1, KEY_D: 2, KEY_F: 3, KEY_H: 4, KEY_G: 5, KEY_Z: 6, KEY_X: 7, KEY_C: 8, KEY_V: 9,
    KEY_B: 11, KEY_Q: 12, KEY_W: 13, KEY_E: 14, KEY_R: 15, KEY_Y: 16, KEY_T: 17,
    KEY_1: 18, KEY_2: 19, KEY_3: 20, KEY_4: 21, KEY_6: 22, KEY_5: 23, KEY_EQUAL: 24, KEY_9: 25, KEY_7: 26, KEY_MINUS: 27, KEY_8: 28, KEY_0: 29,
    KEY_RIGHT_BRACKET: 30, KEY_O: 31, KEY_U: 32, KEY_LEFT_BRACKET: 33, KEY_I: 34, KEY_P: 35, KEY_RETURN: 36,
    KEY_L: 37, KEY_J: 38, KEY_QUOTE: 39, KEY_K: 40, KEY_SEMICOLON: 41, KEY_BACKSLASH: 42, KEY_COMMA: 43, KEY_SLASH: 44, KEY_N: 45, KEY_M: 46, KEY_PERIOD: 47,
    KEY_TAB: 48, KEY_SPACE: 49, KEY_GRAVE: 50, KEY_DELETE: 51, KEY_ESCAPE: 53,
    KEY_HOME: 115, KEY_PAGE_UP: 116, KEY_FORWARD_DELETE: 117, KEY_END: 119, KEY_PAGE_DOWN: 121,
    KEY_LEFT: 123, KEY_RIGHT: 124, KEY_DOWN: 125, KEY_UP: 126
  };

  function parseKeyPressArgs(keySpec, rawModifiers) {
    const mods = new Set();
    let keyName = '';

    const addModifierToken = (tok) => {
      const t = String(tok || '').trim().toLowerCase();
      if (!t) return false;
      if (t === 'command' || t === 'cmd' || t === '⌘' || t === 'meta' || t === 'super') { mods.add('command'); return true; }
      if (t === 'control' || t === 'ctrl' || t === '⌃') { mods.add('control'); return true; }
      if (t === 'option' || t === 'opt' || t === 'alt' || t === '⌥') { mods.add('option'); return true; }
      if (t === 'shift' || t === '⇧') { mods.add('shift'); return true; }
      return false;
    };

    if (typeof rawModifiers === 'number') {
      if (rawModifiers & utilConstant.MODIFIER_COMMAND) mods.add('command');
      if (rawModifiers & utilConstant.MODIFIER_CONTROL) mods.add('control');
      if (rawModifiers & utilConstant.MODIFIER_OPTION) mods.add('option');
      if (rawModifiers & utilConstant.MODIFIER_SHIFT) mods.add('shift');
    } else if (Array.isArray(rawModifiers)) {
      for (const m of rawModifiers) addModifierToken(m);
    } else if (typeof rawModifiers === 'string') {
      for (const part of rawModifiers.split(/[\s,+]+/)) addModifierToken(part);
    }

    if (typeof keySpec === 'number') {
      keyName = MAC_KEYCODE_TO_NAME[keySpec] || '';
    } else if (typeof keySpec === 'string') {
      const trimmed = keySpec.trim();
      const tokens = trimmed.split(/[\s+]+/).filter(Boolean);
      if (tokens.length > 1) {
        for (const tok of tokens) {
          if (!addModifierToken(tok)) {
            keyName = tok;
          }
        }
      } else {
        keyName = trimmed;
      }
    }

    return { key: keyName, modifiers: Array.from(mods) };
  }

  // Intercept short setTimeout calls so deferred host side effects like `setTimeout(() => popclip.appear(), 250)`
  // are captured in chronological order with exact delay steps for host replay (no wasted double sleep in Node).
  const realSetTimeout = globalThis.setTimeout;
  const realClearTimeout = globalThis.clearTimeout;
  const deferredTimers = [];
  let nextFakeTimerId = 1;
  globalThis.setTimeout = function(cb, delay = 0, ...args) {
    const ms = Math.max(0, Number(delay) || 0);
    if (typeof cb === 'function' && ms <= 3000) {
      const entry = { id: nextFakeTimerId++, cb, ms, args, cancelled: false };
      deferredTimers.push(entry);
      return entry.id;
    }
    return realSetTimeout(cb, delay, ...args);
  };
  globalThis.clearTimeout = function(id) {
    const found = deferredTimers.find(t => t.id === id);
    if (found) { found.cancelled = true; return; }
    return realClearTimeout(id);
  };

  // Built-in PopClip-compatible axios shim powered by Node native fetch
  function createAxiosInstance(defaults = {}) {
    async function request(configOrUrl, maybeConfig) {
      if (!allowNetwork) {
        throw new Error('此 PopClip 脚本未声明 entitlements: [network]，已禁止网络访问');
      }
      const cfg = typeof configOrUrl === 'string'
        ? { ...defaults, ...(maybeConfig || {}), url: configOrUrl }
        : { ...defaults, ...(configOrUrl || {}) };

      let url = cfg.url || '';
      if (cfg.baseURL && !/^https?:\/\//i.test(url)) {
        url = cfg.baseURL.replace(/\/+$/, '') + '/' + url.replace(/^\/+/, '');
      }
      if (cfg.params && typeof cfg.params === 'object') {
        const u = new URL(url);
        for (const [k, v] of Object.entries(cfg.params)) {
          if (v !== undefined && v !== null) u.searchParams.append(k, String(v));
        }
        url = u.toString();
      }

      const method = (cfg.method || 'GET').toUpperCase();
      const headers = { ...(defaults.headers || {}), ...(cfg.headers || {}) };
      let body = undefined;
      if (cfg.data !== undefined && method !== 'GET' && method !== 'HEAD') {
        if (typeof cfg.data === 'string' || cfg.data instanceof URLSearchParams) {
          body = cfg.data;
        } else {
          body = JSON.stringify(cfg.data);
          if (!Object.keys(headers).some(k => k.toLowerCase() === 'content-type')) {
            headers['Content-Type'] = 'application/json';
          }
        }
      }

      const res = await fetch(url, { method, headers, body });
      const rawText = await res.text();
      let data = rawText;
      try { data = JSON.parse(rawText); } catch {}

      const respHeaders = {};
      res.headers.forEach((v, k) => { respHeaders[k] = v; });
      const response = { status: res.status, statusText: res.statusText, data, headers: respHeaders, config: cfg };
      if (!res.ok) {
        const err = new Error(`Request failed with status code ${res.status}`);
        err.response = response;
        err.isAxiosError = true;
        throw err;
      }
      return response;
    }

    request.request = request;
    request.get = (url, cfg) => request({ ...(cfg || {}), method: 'GET', url });
    request.delete = (url, cfg) => request({ ...(cfg || {}), method: 'DELETE', url });
    request.head = (url, cfg) => request({ ...(cfg || {}), method: 'HEAD', url });
    request.post = (url, data, cfg) => request({ ...(cfg || {}), method: 'POST', url, data });
    request.put = (url, data, cfg) => request({ ...(cfg || {}), method: 'PUT', url, data });
    request.patch = (url, data, cfg) => request({ ...(cfg || {}), method: 'PATCH', url, data });
    request.create = (subDefaults) => createAxiosInstance({ ...defaults, ...(subDefaults || {}) });
    request.isAxiosError = (e) => !!(e && e.isAxiosError);
    request.default = request;
    request.__esModule = true;
    return request;
  }

  const axiosShim = createAxiosInstance();
  const origLoad = Module._load;
  Module._load = function(requestName, parent, isMain) {
    if (requestName === 'axios') return axiosShim;
    return origLoad.apply(this, arguments);
  };

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
    options: { ...(payload.options || {}) },
    context: {
      appName: payload.appName || '',
      windowTitle: payload.windowTitle || ''
    },
    modifiers: { shift: false, control: false, option: false, command: false },
    showText(msg) { sideEffects.push({ type: 'showText', value: String(msg ?? '') }); },
    copyText(txt) { sideEffects.push({ type: 'copyText', value: String(txt ?? '') }); },
    pasteText(txt) { sideEffects.push({ type: 'pasteText', value: String(txt ?? '') }); },
    openUrl(url) { sideEffects.push({ type: 'openUrl', value: String(url ?? '') }); },
    pressKey(keySpec, modifiers) {
      const parsed = parseKeyPressArgs(keySpec, modifiers);
      if (parsed.key) {
        sideEffects.push({ type: 'pressKey', value: parsed.key, modifiers: parsed.modifiers });
      }
    },
    appear() { sideEffects.push({ type: 'appear', value: '' }); },
    showSuccess() {},
    showFailure() {}
  };

  let definedExt = null;
  globalThis.util = { constant: utilConstant };
  globalThis.popclip = popclip;
  globalThis.pasteboard = { text: payload.text || '' };
  globalThis.defineExtension = (ext) => { definedExt = ext; return ext; };

  let returned = null;

  if (payload.modulePath && fs.existsSync(payload.modulePath)) {
    const mod = require(payload.modulePath);
    const ext = definedExt || (mod && mod.default && typeof mod.default === 'object' ? mod.default : mod);

    const modOptions = (ext && Array.isArray(ext.options)) ? ext.options : (mod && Array.isArray(mod.options) ? mod.options : []);
    for (const opt of modOptions) {
      if (!opt || !opt.identifier) continue;
      if (popclip.options[opt.identifier] === undefined) {
        popclip.options[opt.identifier] = opt.defaultValue !== undefined ? opt.defaultValue : (opt.type === 'boolean' ? false : '');
      } else if (opt.type === 'boolean' && typeof popclip.options[opt.identifier] === 'string') {
        popclip.options[opt.identifier] = popclip.options[opt.identifier].toLowerCase() === 'true';
      }
    }

    let fn = null;
    if (ext && typeof ext.action === 'function') fn = ext.action;
    else if (ext && ext.action && typeof ext.action.code === 'function') fn = ext.action.code;
    else if (ext && typeof ext.actions === 'function') {
      const dyn = await ext.actions(popclip.input, popclip.options, popclip.context);
      const first = Array.isArray(dyn) ? dyn[0] : dyn;
      if (typeof first === 'function') fn = first;
      else if (first && typeof first.code === 'function') fn = first.code;
    } else if (ext && Array.isArray(ext.actions) && ext.actions.length > 0) {
      const first = ext.actions[0];
      if (typeof first === 'function') fn = first;
      else if (first && typeof first.code === 'function') fn = first.code;
    } else if (typeof ext === 'function') {
      fn = ext;
    }

    if (typeof fn !== 'function') {
      throw new Error('模块未导出可执行的 action / actions 函数');
    }

    returned = await fn(popclip.input, popclip.options, popclip.context);
  } else {
    const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor;
    const fn = new AsyncFunction('popclip', 'util', payload.code);
    returned = await fn(popclip, globalThis.util);
  }

  // Drain scheduled short timers in chronological order, recording host-side delay steps
  const activeTimers = deferredTimers.filter(t => !t.cancelled).sort((a, b) => a.ms - b.ms);
  let elapsed = 0;
  for (const timer of activeTimers) {
    if (timer.cancelled) continue;
    const delta = Math.max(0, timer.ms - elapsed);
    if (delta > 0) {
      sideEffects.push({ type: 'delay', value: String(delta) });
      elapsed = timer.ms;
    }
    await timer.cb(...timer.args);
  }

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
        var modulePath = request.Plugin.Action.JavaScriptModulePath;

        if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(modulePath))
        {
            return PluginExecutionResult.Fail("JavaScript 脚本内容为空");
        }

        var timeoutMs = Math.Clamp(request.Plugin.Action.TimeoutMs <= 0 ? 15000 : request.Plugin.Action.TimeoutMs, 500, 60000);
        var payloadJson = JsonSerializer.Serialize(new
        {
            code = code ?? string.Empty,
            modulePath = modulePath ?? string.Empty,
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
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(RuntimeHarness);

        if (!string.IsNullOrWhiteSpace(request.Plugin.Action.WorkingDirectory) &&
            Directory.Exists(request.Plugin.Action.WorkingDirectory))
        {
            psi.WorkingDirectory = request.Plugin.Action.WorkingDirectory;
        }

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
                    var val = eff.TryGetProperty("value", out var vEl) ? vEl.GetString() ?? string.Empty : string.Empty;
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
                        case "pressKey":
                        {
                            var mods = new List<string>();
                            if (eff.TryGetProperty("modifiers", out var mEl) && mEl.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var m in mEl.EnumerateArray())
                                {
                                    var ms = m.GetString();
                                    if (!string.IsNullOrWhiteSpace(ms)) mods.Add(ms);
                                }
                            }
                            await host.SendKeyComboAsync(val, mods, cancellationToken);
                            break;
                        }
                        case "delay":
                            if (int.TryParse(val, out var delayMs) && delayMs > 0)
                            {
                                await Task.Delay(Math.Clamp(delayMs, 1, 3000), cancellationToken);
                            }
                            break;
                        case "appear":
                            await host.ReappearToolbarAsync(request.Context.AnchorPoint, cancellationToken);
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
}
