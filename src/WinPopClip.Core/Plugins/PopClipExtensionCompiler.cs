using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinPopClip.Core.Models;

namespace WinPopClip.Core.Plugins;

public sealed class PopClipCompileResult
{
    public bool Success { get; init; }
    public string? CompiledEntryPath { get; init; }
    public string? MetadataPath { get; init; }
    public List<PluginOptionDefinition> Options { get; init; } = new();
    public string? ErrorMessage { get; init; }
    public string? CompilerInfo { get; init; }
}

public sealed class PopClipCompiledMetadata
{
    public int SchemaVersion { get; set; } = 1;
    public string SourceEntry { get; set; } = string.Empty;
    public string CompiledEntry { get; set; } = string.Empty;
    public string Compiler { get; set; } = string.Empty;
    public DateTimeOffset CompiledAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<PluginOptionDefinition> Options { get; set; } = new();
}

/// <summary>
/// Import-time recompiler for modern PopClip extensions written in TypeScript (Config.ts / *.ts)
/// or ES Modules (import / export).
/// Uses Node.js native TypeScript transformation (module.stripTypeScriptTypes in Node 22.13+/24,
/// with a built-in syntax stripper fallback for older Node versions) and rewrites ESM imports/exports
/// into clean CommonJS (.cjs) bundles while introspecting dynamic runtime `exports.options`.
/// </summary>
public static class PopClipExtensionCompiler
{
    public const string CompiledEntryFileName = "_selector_compiled.cjs";
    public const string CompiledMetaFileName = "_selector_meta.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string CompilerScript = """
const fs = require('fs');
const path = require('path');
const Module = require('module');

const payload = JSON.parse(fs.readFileSync(0, 'utf8'));
const extDir = payload.extDir;
const entryFile = payload.entryFile;
const compiledEntryName = payload.compiledEntryName || '_selector_compiled.cjs';

function stripTsFallback(code) {
  let out = code;
  // Remove `import type ...;`
  out = out.replace(/^\s*import\s+type\s+[\s\S]*?;\s*$/gm, '');
  // Remove standalone `type Foo = ...;`
  out = out.replace(/^\s*(?:export\s+)?type\s+[A-Za-z0-9_$]+\s*(?:<[^>]*>)?\s*=\s*[\s\S]*?;\s*$/gm, '');
  // Remove `interface Foo { ... }`
  out = out.replace(/^\s*(?:export\s+)?interface\s+[A-Za-z0-9_$]+(?:\s*<[^>]*>)?(?:\s+extends\s+[^{]+)?\s*\{[\s\S]*?\n\}\s*$/gm, '');
  // Remove `as const` and simple `as Type` assertions
  out = out.replace(/\s+as\s+const\b/g, '');
  out = out.replace(/\s+as\s+[A-Za-z0-9_$<>[\]|&\s.?]+(?=[,;)\]\n])/g, '');
  // Remove variable type annotations `const x: Foo =`
  out = out.replace(/\b(const|let|var)\s+([A-Za-z0-9_$]+)\s*:\s*[^=;\n]+(?=\s*[=;])/g, '$1 $2');
  return out;
}

function stripTypeScript(source, isTs) {
  if (!isTs) return { code: source, mode: 'js-pass' };
  if (typeof Module.stripTypeScriptTypes === 'function') {
    try {
      const transformed = Module.stripTypeScriptTypes(source, { mode: 'transform' });
      return { code: transformed, mode: 'node-stripTypeScriptTypes' };
    } catch (e) {
      // Fall through to strip-only mode
      try {
        const stripped = Module.stripTypeScriptTypes(source, { mode: 'strip' });
        return { code: stripped, mode: 'node-stripTypeScriptTypes-strip' };
      } catch (e2) {
        return { code: stripTsFallback(source), mode: 'fallback-stripper' };
      }
    }
  }
  return { code: stripTsFallback(source), mode: 'fallback-stripper' };
}

function rewriteSpecifier(spec, compiledMap) {
  if (!spec.startsWith('./') && !spec.startsWith('../')) return spec;
  const noExt = spec.replace(/\.(ts|tsx|mts|cts|mjs)$/i, '');
  if (compiledMap.has(spec)) return compiledMap.get(spec);
  if (compiledMap.has(noExt + '.ts')) return compiledMap.get(noExt + '.ts');
  if (compiledMap.has(noExt + '.mts')) return compiledMap.get(noExt + '.mts');
  if (compiledMap.has(noExt + '.mjs')) return compiledMap.get(noExt + '.mjs');
  return spec;
}

function transpileEsmToCjs(code, compiledMap) {
  let out = code;
  const exportedNames = new Set();
  let importCounter = 0;

  const makeDefaultImport = (ident, spec) => {
    const tmp = `__sel_imp_${++importCounter}`;
    const resolved = rewriteSpecifier(spec, compiledMap);
    return `const ${tmp} = require(${JSON.stringify(resolved)}); const ${ident} = (${tmp} && ${tmp}.__esModule && ${tmp}.default !== undefined) ? ${tmp}.default : ${tmp};`;
  };

  const formatNamedBindings = (rawList) => {
    return rawList
      .split(',')
      .map(s => s.trim())
      .filter(Boolean)
      .map(item => {
        const cleaned = item.replace(/^type\s+/, '');
        const m = cleaned.match(/^([A-Za-z0-9_$]+)\s+as\s+([A-Za-z0-9_$]+)$/);
        return m ? `${m[1]}: ${m[2]}` : cleaned;
      })
      .join(', ');
  };

  // 1. import def, { a, b as c } from "spec";
  out = out.replace(
    /^\s*import\s+([A-Za-z0-9_$]+)\s*,\s*\{([\s\S]*?)\}\s*from\s*["']([^"']+)["']\s*;?/gm,
    (_, defIdent, named, spec) => {
      const tmp = `__sel_imp_${++importCounter}`;
      const resolved = rewriteSpecifier(spec, compiledMap);
      const bindings = formatNamedBindings(named);
      return `const ${tmp} = require(${JSON.stringify(resolved)}); const ${defIdent} = (${tmp} && ${tmp}.__esModule && ${tmp}.default !== undefined) ? ${tmp}.default : ${tmp}; const { ${bindings} } = ${tmp};`;
    }
  );

  // 2. import * as ns from "spec";
  out = out.replace(
    /^\s*import\s+\*\s+as\s+([A-Za-z0-9_$]+)\s+from\s*["']([^"']+)["']\s*;?/gm,
    (_, ns, spec) => `const ${ns} = require(${JSON.stringify(rewriteSpecifier(spec, compiledMap))});`
  );

  // 3. import { a, b as c } from "spec";
  out = out.replace(
    /^\s*import\s+\{([\s\S]*?)\}\s*from\s*["']([^"']+)["']\s*;?/gm,
    (_, named, spec) => `const { ${formatNamedBindings(named)} } = require(${JSON.stringify(rewriteSpecifier(spec, compiledMap))});`
  );

  // 4. import def from "spec";
  out = out.replace(
    /^\s*import\s+([A-Za-z0-9_$]+)\s+from\s*["']([^"']+)["']\s*;?/gm,
    (_, defIdent, spec) => makeDefaultImport(defIdent, spec)
  );

  // 5. import "spec";
  out = out.replace(
    /^\s*import\s*["']([^"']+)["']\s*;?/gm,
    (_, spec) => `require(${JSON.stringify(rewriteSpecifier(spec, compiledMap))});`
  );

  // 6. export { a, b as c } from "spec";
  out = out.replace(
    /^\s*export\s+\{([\s\S]*?)\}\s*from\s*["']([^"']+)["']\s*;?/gm,
    (_, named, spec) => {
      const tmp = `__sel_reexp_${++importCounter}`;
      const resolved = rewriteSpecifier(spec, compiledMap);
      const items = named.split(',').map(s => s.trim()).filter(Boolean).map(item => {
        const m = item.match(/^([A-Za-z0-9_$]+)\s+as\s+([A-Za-z0-9_$]+)$/);
        return m ? `exports.${m[2]} = ${tmp}.${m[1]};` : `exports.${item} = ${tmp}.${item};`;
      });
      return `const ${tmp} = require(${JSON.stringify(resolved)}); ${items.join(' ')}`;
    }
  );

  // 7. export * from "spec";
  out = out.replace(
    /^\s*export\s+\*\s+from\s*["']([^"']+)["']\s*;?/gm,
    (_, spec) => `Object.assign(exports, require(${JSON.stringify(rewriteSpecifier(spec, compiledMap))}));`
  );

  // 8. export default ...
  out = out.replace(/^\s*export\s+default\s+/gm, 'exports.default = ');

  // 9. export async function name / export function name / export class name
  out = out.replace(
    /^\s*export\s+(async\s+function|function|class)\s+([A-Za-z0-9_$]+)/gm,
    (_, decl, name) => {
      exportedNames.add(name);
      return `${decl} ${name}`;
    }
  );

  // 10. export const/let/var name = ...
  out = out.replace(
    /^\s*export\s+(const|let|var)\s+([A-Za-z0-9_$]+)/gm,
    (_, decl, name) => {
      exportedNames.add(name);
      return `${decl} ${name}`;
    }
  );

  // 11. export { a, b as c };
  out = out.replace(
    /^\s*export\s+\{([\s\S]*?)\}\s*;?/gm,
    (_, named) => {
      return named.split(',').map(s => s.trim()).filter(Boolean).map(item => {
        const m = item.match(/^([A-Za-z0-9_$]+)\s+as\s+([A-Za-z0-9_$]+)$/);
        return m ? `exports.${m[2]} = ${m[1]};` : `exports.${item} = ${item};`;
      }).join(' ');
    }
  );

  const footer = [
    '"use strict";',
    'Object.defineProperty(exports, "__esModule", { value: true });',
  ];

  const exportLines = Array.from(exportedNames).map(n => `exports.${n} = ${n};`);
  return `${footer.join('\n')}\n${out}\n\n${exportLines.join('\n')}\n`;
}

function collectSourceFiles(dir, baseDir, list = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === 'node_modules' || entry.name === '__MACOSX' || entry.name.startsWith('_selector_')) continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      collectSourceFiles(full, baseDir, list);
    } else if (/\.(ts|tsx|mts|cts|mjs)$/i.test(entry.name)) {
      list.push(full);
    }
  }
  return list;
}

function pickLocalized(val, fallback) {
  if (typeof val === 'string' && val.trim()) return val.trim();
  if (val && typeof val === 'object') {
    for (const k of ['zh-Hans', 'zh-CN', 'zh', 'en']) {
      if (typeof val[k] === 'string' && val[k].trim()) return val[k].trim();
    }
    for (const v of Object.values(val)) {
      if (typeof v === 'string' && v.trim()) return v.trim();
    }
  }
  return fallback;
}

function normalizeOptions(rawOptions) {
  if (!Array.isArray(rawOptions)) return [];
  const res = [];
  for (const opt of rawOptions) {
    if (!opt || typeof opt !== 'object') continue;
    const key = opt.identifier || opt.key;
    if (!key || typeof key !== 'string') continue;
    const rawType = String(opt.type || 'string').toLowerCase();
    const type = rawType === 'boolean' ? 'Boolean'
      : (rawType === 'secret' || rawType === 'password') ? 'Password'
      : rawType === 'multiple' ? 'Choice'
      : 'String';
    const values = Array.isArray(opt.values) ? opt.values.map(v => String(v ?? '')) : [];
    const valueLabels = Array.isArray(opt.valueLabels) ? opt.valueLabels.map(v => pickLocalized(v, String(v ?? ''))) : [];
    const defVal = opt.defaultValue !== undefined && opt.defaultValue !== null
      ? String(opt.defaultValue)
      : (type === 'Boolean' ? 'false' : (values.length > 0 ? values[0] : ''));
    res.push({
      Key: key,
      Label: pickLocalized(opt.label, key),
      Type: type,
      DefaultValue: defVal,
      Description: pickLocalized(opt.description, ''),
      Choices: values,
      ChoiceLabels: valueLabels
    });
  }
  return res;
}

try {
  const fullEntry = path.resolve(extDir, entryFile);
  if (!fs.existsSync(fullEntry)) {
    throw new Error(`入口文件不存在: ${entryFile}`);
  }

  const allTsFiles = collectSourceFiles(extDir, extDir);
  const compiledMap = new Map();
  for (const file of allTsFiles) {
    const rel = './' + path.relative(extDir, file).replace(/\\/g, '/');
    const outRel = (path.resolve(file) === fullEntry)
      ? './' + compiledEntryName
      : rel.replace(/\.(ts|tsx|mts|cts|mjs)$/i, '._sel.cjs');
    compiledMap.set(rel, outRel);
  }

  let compilerMode = 'js-cjs';

  // Compile sibling TS/ESM modules first
  for (const file of allTsFiles) {
    if (path.resolve(file) === fullEntry) continue;
    const src = fs.readFileSync(file, 'utf8');
    const isTs = /\.(ts|tsx|mts|cts)$/i.test(file);
    const stripped = stripTypeScript(src, isTs);
    const cjs = transpileEsmToCjs(stripped.code, compiledMap);
    const outPath = file.replace(/\.(ts|tsx|mts|cts|mjs)$/i, '._sel.cjs');
    fs.writeFileSync(outPath, cjs, 'utf8');
  }

  // Compile entry file
  const entrySrc = fs.readFileSync(fullEntry, 'utf8');
  const entryIsTs = /\.(ts|tsx|mts|cts)$/i.test(fullEntry);
  const strippedEntry = stripTypeScript(entrySrc, entryIsTs);
  compilerMode = strippedEntry.mode;
  const compiledEntryCode = transpileEsmToCjs(strippedEntry.code, compiledMap);
  const compiledEntryPath = path.join(extDir, compiledEntryName);
  fs.writeFileSync(compiledEntryPath, compiledEntryCode, 'utf8');

  // Dry-load compiled module in sandboxed stub environment to verify syntax & extract runtime options
  const noopAxios = Object.assign(() => Promise.resolve({ status: 200, data: {} }), {
    get: () => Promise.resolve({ status: 200, data: {} }),
    post: () => Promise.resolve({ status: 200, data: {} }),
    put: () => Promise.resolve({ status: 200, data: {} }),
    patch: () => Promise.resolve({ status: 200, data: {} }),
    delete: () => Promise.resolve({ status: 200, data: {} }),
    create: () => noopAxios,
    isAxiosError: (e) => !!(e && e.isAxiosError),
    __esModule: true
  });
  noopAxios.default = noopAxios;

  const origLoad = Module._load;
  Module._load = function(requestName, parent, isMain) {
    if (requestName === 'axios') return noopAxios;
    return origLoad.apply(this, arguments);
  };

  let definedExt = null;
  globalThis.util = {
    constant: {
      MODIFIER_SHIFT: 0x20000,
      MODIFIER_CONTROL: 0x40000,
      MODIFIER_OPTION: 0x80000,
      MODIFIER_COMMAND: 0x100000,
      KEY_A: 0, KEY_S: 1, KEY_D: 2, KEY_F: 3, KEY_H: 4, KEY_G: 5, KEY_Z: 6, KEY_X: 7, KEY_C: 8, KEY_V: 9,
      KEY_B: 11, KEY_Q: 12, KEY_W: 13, KEY_E: 14, KEY_R: 15, KEY_Y: 16, KEY_T: 17,
      KEY_RETURN: 36, KEY_TAB: 48, KEY_SPACE: 49, KEY_DELETE: 51, KEY_ESCAPE: 53,
      KEY_LEFT: 123, KEY_RIGHT: 124, KEY_DOWN: 125, KEY_UP: 126
    }
  };
  globalThis.popclip = {
    input: { text: '', matchedText: '', data: { urls: [], emails: [] } },
    options: {},
    context: { appName: '', windowTitle: '' },
    modifiers: { shift: false, control: false, option: false, command: false },
    showText() {}, copyText() {}, pasteText() {}, openUrl() {}, pressKey() {}, appear() {}, showSuccess() {}, showFailure() {}
  };
  globalThis.pasteboard = { text: '' };
  globalThis.defineExtension = (ext) => { definedExt = ext; return ext; };

  const mod = require(compiledEntryPath);
  const ext = definedExt || (mod && mod.default && typeof mod.default === 'object' ? mod.default : mod);

  const hasCallable =
    typeof ext === 'function' ||
    (ext && typeof ext.action === 'function') ||
    (ext && ext.action && typeof ext.action.code === 'function') ||
    (ext && typeof ext.actions === 'function') ||
    (ext && Array.isArray(ext.actions) && ext.actions.length > 0);

  if (!hasCallable) {
    throw new Error('重编译后的模块未导出可执行的 action / actions 函数');
  }

  const rawOpts = (ext && Array.isArray(ext.options)) ? ext.options
    : (mod && Array.isArray(mod.options)) ? mod.options
    : [];
  const options = normalizeOptions(rawOpts);

  process.stdout.write(JSON.stringify({
    ok: true,
    compiledEntryPath,
    compiler: `${compilerMode} (Node ${process.version})`,
    options
  }));
} catch (err) {
  process.stdout.write(JSON.stringify({
    ok: false,
    error: err && err.message ? err.message : String(err)
  }));
  process.exitCode = 1;
}
""";

    public static bool NeedsCompilation(string entryFilePath, string? sourceText = null)
    {
        var ext = Path.GetExtension(entryFilePath);
        if (ext.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".tsx", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".mts", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".mjs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ext.Equals(".js", StringComparison.OrdinalIgnoreCase))
        {
            var text = sourceText ?? (File.Exists(entryFilePath) ? File.ReadAllText(entryFilePath) : string.Empty);
            return System.Text.RegularExpressions.Regex.IsMatch(
                text,
                @"^\s*(?:import\s+[\s\S]+?\s+from\s+|export\s+(?:default|const|let|var|function|async\s+function|class|\{))",
                System.Text.RegularExpressions.RegexOptions.Multiline);
        }

        return false;
    }

    public static PopClipCompileResult CompileExtensionEntry(string extFolderPath, string entryFileRelativeOrFull)
    {
        var entryFile = Path.IsPathRooted(entryFileRelativeOrFull)
            ? Path.GetRelativePath(extFolderPath, entryFileRelativeOrFull)
            : entryFileRelativeOrFull;

        var payloadJson = JsonSerializer.Serialize(new
        {
            extDir = Path.GetFullPath(extFolderPath),
            entryFile,
            compiledEntryName = CompiledEntryFileName
        });

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = extFolderPath,
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
        psi.ArgumentList.Add(CompilerScript);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                return new PopClipCompileResult { Success = false, ErrorMessage = "无法启动 Node.js 编译器" };
            }
        }
        catch (Exception ex)
        {
            return new PopClipCompileResult
            {
                Success = false,
                ErrorMessage = $"重编译 PopClip TypeScript/ESM 扩展需要本机安装 Node.js：{ex.Message}"
            };
        }

        using (var writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
        {
            writer.Write(payloadJson);
            writer.Flush();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(20000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new PopClipCompileResult { Success = false, ErrorMessage = "重编译 PopClip 扩展超时（>20s）" };
        }

        var stdout = stdoutTask.GetAwaiter().GetResult().Trim();
        var stderr = stderrTask.GetAwaiter().GetResult().Trim();

        if (string.IsNullOrWhiteSpace(stdout))
        {
            return new PopClipCompileResult
            {
                Success = false,
                ErrorMessage = string.IsNullOrWhiteSpace(stderr) ? "重编译无输出" : $"重编译失败: {stderr}"
            };
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            if (!ok)
            {
                var errMsg = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : stderr;
                return new PopClipCompileResult { Success = false, ErrorMessage = $"重编译 PopClip 扩展失败：{errMsg}" };
            }

            var compiledPath = root.GetProperty("compiledEntryPath").GetString()!;
            var compilerInfo = root.TryGetProperty("compiler", out var ciEl) ? ciEl.GetString() : "node";
            var options = new List<PluginOptionDefinition>();
            if (root.TryGetProperty("options", out var optsEl) && optsEl.ValueKind == JsonValueKind.Array)
            {
                options = JsonSerializer.Deserialize<List<PluginOptionDefinition>>(optsEl.GetRawText(), JsonOptions)
                          ?? new List<PluginOptionDefinition>();
            }

            var meta = new PopClipCompiledMetadata
            {
                SourceEntry = entryFile,
                CompiledEntry = Path.GetFileName(compiledPath),
                Compiler = compilerInfo ?? "node",
                CompiledAtUtc = DateTimeOffset.UtcNow,
                Options = options
            };
            var metaPath = Path.Combine(extFolderPath, CompiledMetaFileName);
            File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, JsonOptions), new UTF8Encoding(false));

            return new PopClipCompileResult
            {
                Success = true,
                CompiledEntryPath = compiledPath,
                MetadataPath = metaPath,
                Options = options,
                CompilerInfo = compilerInfo
            };
        }
        catch (Exception ex)
        {
            return new PopClipCompileResult
            {
                Success = false,
                ErrorMessage = $"解析重编译结果失败：{ex.Message}"
            };
        }
    }

    public static PopClipCompiledMetadata? TryReadMetadata(string extFolderPath)
    {
        var metaPath = Path.Combine(extFolderPath, CompiledMetaFileName);
        if (!File.Exists(metaPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<PopClipCompiledMetadata>(File.ReadAllText(metaPath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
