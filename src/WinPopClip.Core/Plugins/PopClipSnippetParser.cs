using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WinPopClip.Core.Models;

namespace WinPopClip.Core.Plugins;

/// <summary>
/// Parses macOS PopClip snippets (#popclip / // #popclip YAML headers, JavaScript snippets,
/// URL/Shell snippets) and .popclipext Config.json files into WinPopClip PluginManifest objects.
/// </summary>
public static partial class PopClipSnippetParser
{
    [GeneratedRegex(@"^\s*(?://|#|/\*)\s*#?\s*popclip\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex HeaderMarkerRegex();

    public static bool LooksLikePopClipSnippet(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 12)
        {
            return false;
        }
        return HeaderMarkerRegex().IsMatch(text);
    }

    /// <summary>
    /// Attempts to parse a PopClip snippet (YAML or JS with // #popclip header) into a PluginManifest.
    /// </summary>
    public static bool TryParseSnippet(
        string rawText,
        out PluginManifest? manifest,
        out string? error,
        string? fallbackName = null,
        string? extFolderPath = null)
    {
        manifest = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawText))
        {
            error = "内容为空";
            return false;
        }

        var lines = rawText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var startIdx = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^\s*(?://\s*)?#\s*popclip\b", RegexOptions.IgnoreCase));
        if (startIdx < 0)
        {
            error = "未找到 #popclip 标识头（例如 #popclip 或 // #popclip）";
            return false;
        }

        var isCommentedJs = lines[startIdx].TrimStart().StartsWith("//", StringComparison.Ordinal);
        var headerLines = new List<string>();
        var bodyLines = new List<string>();

        if (isCommentedJs)
        {
            var i = startIdx + 1;
            while (i < lines.Length && lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                var stripped = lines[i].TrimStart()[2..];
                if (stripped.StartsWith(' ')) stripped = stripped[1..];
                headerLines.Add(stripped);
                i++;
            }

            while (i < lines.Length)
            {
                bodyLines.Add(lines[i]);
                i++;
            }
        }
        else
        {
            for (var i = startIdx + 1; i < lines.Length; i++)
            {
                headerLines.Add(lines[i]);
            }
        }

        var fields = ParseSimpleYaml(headerLines, out var options);

        var name = GetField(fields, "name") ?? fallbackName;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "PopClip 插件缺少 name 字段";
            return false;
        }

        var identifier = GetField(fields, "identifier")
                         ?? "popclip." + SanitizeId(name);
        var icon = NormalizePopClipIcon(GetField(fields, "icon"), name, extFolderPath);
        var description = GetField(fields, "description") ?? "从 PopClip Snippet 导入";
        var regex = GetField(fields, "regex");
        var after = ParseAfterAction(GetField(fields, "after"));
        var requirements = ParseList(GetField(fields, "requirements"));
        if (requirements.Count == 0)
        {
            requirements.Add("text");
        }

        var allowedApps = ParseList(GetField(fields, "required apps") ?? GetField(fields, "apps"));
        var blockedApps = ParseList(GetField(fields, "blocked apps"));
        var entitlements = ParseList(GetField(fields, "entitlements"));

        var jsBody = string.Join('\n', bodyLines).Trim();
        var inlineJs = GetField(fields, "javascript") ?? GetField(fields, "js");
        var keyCombo = GetField(fields, "key combo");
        var url = GetField(fields, "url");
        var shellScript = GetField(fields, "shell script");
        var interpreter = GetField(fields, "interpreter");

        PluginKind kind;
        var action = new PluginActionConfig
        {
            Entitlements = entitlements
        };

        if (!string.IsNullOrWhiteSpace(jsBody) || !string.IsNullOrWhiteSpace(inlineJs))
        {
            kind = PluginKind.JavaScript;
            action.JavaScriptCode = !string.IsNullOrWhiteSpace(jsBody) ? jsBody : inlineJs;
        }
        else if (!string.IsNullOrWhiteSpace(keyCombo))
        {
            kind = PluginKind.JavaScript;
            action.JavaScriptCode = $"popclip.pressKey({JsonSerializer.Serialize(keyCombo)});";
        }
        else if (!string.IsNullOrWhiteSpace(url))
        {
            kind = PluginKind.Url;
            action.UrlTemplate = ConvertPopClipUrlPlaceholders(url);
        }
        else if (!string.IsNullOrWhiteSpace(shellScript))
        {
            kind = PluginKind.Shell;
            action.ShellExecutable = MapInterpreter(interpreter);
            action.ShellScript = shellScript.Replace("{popclip text}", "{text}", StringComparison.OrdinalIgnoreCase);
            action.PassTextViaStdin = true;
        }
        else
        {
            error = "未能识别插件动作（支持 url、javascript / // #popclip 脚本、或 shell script）";
            return false;
        }

        manifest = new PluginManifest
        {
            Id = identifier,
            Name = name.Trim(),
            Icon = icon,
            Description = description.Trim(),
            Author = "PopClip Compat",
            Kind = kind,
            Requirements = requirements,
            Regex = regex,
            AllowedApps = allowedApps,
            BlockedApps = blockedApps,
            After = after,
            Action = action,
            Options = options,
            Source = PluginSourceType.UserCustom,
            Enabled = true
        };

        return true;
    }

    /// <summary>
    /// Parses a .popclipext directory containing Config.json, Config.yaml / Config.yml, or Apple XML Config.plist.
    /// </summary>
    public static bool TryParsePopClipExtFolder(string extFolderPath, out PluginManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;

        if (string.IsNullOrWhiteSpace(extFolderPath) || !Directory.Exists(extFolderPath))
        {
            error = "扩展目录不存在";
            return false;
        }

        var fallbackName = Path.GetFileName(extFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (fallbackName.EndsWith(".popclipext", StringComparison.OrdinalIgnoreCase))
        {
            fallbackName = fallbackName[..^".popclipext".Length];
        }

        var configJson = FindFileCaseInsensitive(extFolderPath, "Config.json");
        if (configJson is not null)
        {
            if (TryParsePopClipExtJson(File.ReadAllText(configJson), extFolderPath, out manifest, out error) && manifest is not null)
            {
                manifest.SourceFilePath = configJson;
                return true;
            }
            error ??= "Config.json 格式不受支持或缺少动作定义";
            return false;
        }

        var configYaml = FindFileCaseInsensitive(extFolderPath, "Config.yaml")
                         ?? FindFileCaseInsensitive(extFolderPath, "Config.yml");
        if (configYaml is not null)
        {
            if (TryParsePopClipExtYaml(File.ReadAllText(configYaml), extFolderPath, fallbackName, out manifest, out error) && manifest is not null)
            {
                manifest.SourceFilePath = configYaml;
                return true;
            }
            return false;
        }

        // Modern PopClip 4586+ packages ship Config.ts / Config.js with a `// #popclip` header
        // alongside a server-generated stub Config.plist. Prefer Config.ts / Config.js whenever they
        // contain a `#popclip` header; otherwise try Config.plist first and fall back to Config.ts / Config.js.
        var configTs = FindFileCaseInsensitive(extFolderPath, "Config.ts");
        if (configTs is not null && LooksLikePopClipSnippet(File.ReadAllText(configTs)))
        {
            return TryParsePopClipScriptEntry(configTs, extFolderPath, fallbackName, out manifest, out error);
        }

        var configJs = FindFileCaseInsensitive(extFolderPath, "Config.js");
        if (configJs is not null && LooksLikePopClipSnippet(File.ReadAllText(configJs)))
        {
            return TryParsePopClipScriptEntry(configJs, extFolderPath, fallbackName, out manifest, out error);
        }

        var configPlist = FindFileCaseInsensitive(extFolderPath, "Config.plist");
        if (configPlist is not null)
        {
            if (TryParsePopClipExtPlist(File.ReadAllText(configPlist), extFolderPath, fallbackName, out manifest, out error) && manifest is not null)
            {
                manifest.SourceFilePath = configPlist;
                return true;
            }
            if (configTs is null && configJs is null)
            {
                return false;
            }
        }

        if (configTs is not null)
        {
            return TryParsePopClipScriptEntry(configTs, extFolderPath, fallbackName, out manifest, out error);
        }

        if (configJs is not null)
        {
            return TryParsePopClipScriptEntry(configJs, extFolderPath, fallbackName, out manifest, out error);
        }

        error = "目录中未找到 Config.json、Config.yaml、Config.ts、Config.js 或 Config.plist";
        return false;
    }

    private static bool TryParsePopClipScriptEntry(
        string entryFilePath,
        string extFolderPath,
        string fallbackName,
        out PluginManifest? manifest,
        out string? error)
    {
        manifest = null;
        error = null;

        var rawSource = File.ReadAllText(entryFilePath);
        if (!TryParseSnippet(rawSource, out manifest, out _, fallbackName, extFolderPath) || manifest is null)
        {
            // Fallback metadata when Config.ts / Config.js lacks a // #popclip header
            manifest = new PluginManifest
            {
                Id = "popclip." + SanitizeId(fallbackName),
                Name = fallbackName,
                Icon = NormalizePopClipIcon(null, fallbackName),
                Description = "PopClip 模块扩展",
                Author = "PopClip Ext",
                Kind = PluginKind.JavaScript,
                Requirements = new() { "text" },
                After = AfterActionType.ShowResult,
                Action = new PluginActionConfig
                {
                    Entitlements = new() { "network" }
                },
                Source = PluginSourceType.ExternalFile,
                Enabled = true
            };
        }

        manifest.Source = PluginSourceType.ExternalFile;
        manifest.SourceFilePath = entryFilePath;
        manifest.Author = "PopClip Ext";
        manifest.Action.WorkingDirectory = extFolderPath;

        if (!ResolveOrCompileJsModule(extFolderPath, entryFilePath, manifest.Action, out var compiledOptions, out error))
        {
            manifest = null;
            return false;
        }

        if (compiledOptions.Count > 0)
        {
            manifest.Options = compiledOptions;
        }
        else if (manifest.Options.Count == 0 && !string.IsNullOrWhiteSpace(manifest.Action.JavaScriptCode))
        {
            manifest.Options = ExtractOptionsFromModuleSource(manifest.Action.JavaScriptCode);
        }

        return true;
    }

    /// <summary>
    /// Ensures a PopClip JS/TS module entry is compiled to Windows-ready CommonJS if it uses TypeScript
    /// or ES Modules, reusing cached _selector_compiled.cjs + _selector_meta.json on subsequent reloads.
    /// </summary>
    private static bool ResolveOrCompileJsModule(
        string extFolderPath,
        string entryFilePath,
        PluginActionConfig action,
        out List<PluginOptionDefinition> compiledOptions,
        out string? error)
    {
        compiledOptions = new List<PluginOptionDefinition>();
        error = null;

        var rawSource = File.ReadAllText(entryFilePath);
        var needsCompile = PopClipExtensionCompiler.NeedsCompilation(entryFilePath, rawSource);

        var precompiledPath = Path.Combine(extFolderPath, PopClipExtensionCompiler.CompiledEntryFileName);
        var precompiledMeta = PopClipExtensionCompiler.TryReadMetadata(extFolderPath);

        if (File.Exists(precompiledPath) && precompiledMeta is not null)
        {
            action.JavaScriptModulePath = precompiledPath;
            action.WorkingDirectory = extFolderPath;
            action.JavaScriptCode = File.ReadAllText(precompiledPath);
            compiledOptions = precompiledMeta.Options;
            return true;
        }

        if (needsCompile)
        {
            var compileRes = PopClipExtensionCompiler.CompileExtensionEntry(extFolderPath, entryFilePath);
            if (!compileRes.Success || string.IsNullOrWhiteSpace(compileRes.CompiledEntryPath))
            {
                error = compileRes.ErrorMessage ?? "重编译 PopClip 扩展失败";
                return false;
            }

            action.JavaScriptModulePath = compileRes.CompiledEntryPath;
            action.WorkingDirectory = extFolderPath;
            action.JavaScriptCode = File.ReadAllText(compileRes.CompiledEntryPath);
            compiledOptions = compileRes.Options;
            return true;
        }

        action.JavaScriptModulePath = entryFilePath;
        action.WorkingDirectory = extFolderPath;
        action.JavaScriptCode = rawSource;
        return true;
    }

    private static string? FindFileCaseInsensitive(string dir, string targetFileName)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(f).Equals(targetFileName, StringComparison.OrdinalIgnoreCase))
            {
                return f;
            }
        }
        return null;
    }

    private static bool TryParsePopClipExtYaml(
        string yamlText,
        string extFolderPath,
        string fallbackName,
        out PluginManifest? manifest,
        out string? error)
    {
        manifest = null;
        error = null;
        var lines = yamlText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        var fields = ParseSimpleYaml(lines, out var options);

        var name = GetField(fields, "name") ?? fallbackName;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Config.yaml 缺少 name 字段";
            return false;
        }

        var id = GetField(fields, "identifier") ?? "popclip." + SanitizeId(name);
        var icon = NormalizePopClipIcon(GetField(fields, "icon"), name, extFolderPath);
        var description = GetField(fields, "description") ?? "PopClip .popclipext 扩展";
        var regex = GetField(fields, "regex");
        var after = ParseAfterAction(GetField(fields, "after"));
        var requirements = ParseList(GetField(fields, "requirements"));
        if (requirements.Count == 0) requirements.Add("text");

        var entitlements = ParseList(GetField(fields, "entitlements"));
        var action = new PluginActionConfig { Entitlements = entitlements };

        var jsModule = GetField(fields, "module");
        var jsFile = GetField(fields, "javascript file");
        var jsInline = GetField(fields, "javascript") ?? GetField(fields, "js");
        var keyCombo = GetField(fields, "key combo");
        var url = GetField(fields, "url");
        var shellFile = GetField(fields, "shell script file");
        var shellScript = GetField(fields, "shell script");
        var interpreter = GetField(fields, "interpreter");

        PluginKind kind;
        if (!string.IsNullOrWhiteSpace(jsModule))
        {
            var fullPath = ResolveRelativeFile(extFolderPath, jsModule);
            if (fullPath is null)
            {
                error = $"未找到模块文件: {jsModule}";
                return false;
            }
            kind = PluginKind.JavaScript;
            if (!ResolveOrCompileJsModule(extFolderPath, fullPath, action, out var compiledOpts, out error))
            {
                return false;
            }
            if (options.Count == 0 && compiledOpts.Count > 0) options = compiledOpts;
        }
        else if (!string.IsNullOrWhiteSpace(jsFile))
        {
            var fullPath = ResolveRelativeFile(extFolderPath, jsFile);
            if (fullPath is null)
            {
                error = $"未找到脚本文件: {jsFile}";
                return false;
            }
            kind = PluginKind.JavaScript;
            if (PopClipExtensionCompiler.NeedsCompilation(fullPath))
            {
                if (!ResolveOrCompileJsModule(extFolderPath, fullPath, action, out var compiledOpts, out error))
                {
                    return false;
                }
                if (options.Count == 0 && compiledOpts.Count > 0) options = compiledOpts;
            }
            else
            {
                action.JavaScriptCode = File.ReadAllText(fullPath);
                action.WorkingDirectory = extFolderPath;
            }
        }
        else if (!string.IsNullOrWhiteSpace(jsInline))
        {
            kind = PluginKind.JavaScript;
            action.JavaScriptCode = jsInline;
        }
        else if (!string.IsNullOrWhiteSpace(keyCombo))
        {
            kind = PluginKind.JavaScript;
            action.JavaScriptCode = $"popclip.pressKey({JsonSerializer.Serialize(keyCombo)});";
        }
        else if (!string.IsNullOrWhiteSpace(url))
        {
            kind = PluginKind.Url;
            action.UrlTemplate = ConvertPopClipUrlPlaceholders(url);
        }
        else if (!string.IsNullOrWhiteSpace(shellFile))
        {
            var fullPath = Path.Combine(extFolderPath, shellFile);
            if (!File.Exists(fullPath))
            {
                error = $"未找到脚本文件: {shellFile}";
                return false;
            }
            kind = PluginKind.Shell;
            action.ShellExecutable = MapInterpreter(interpreter);
            action.ShellScript = File.ReadAllText(fullPath);
            action.PassTextViaStdin = true;
        }
        else if (!string.IsNullOrWhiteSpace(shellScript))
        {
            kind = PluginKind.Shell;
            action.ShellExecutable = MapInterpreter(interpreter);
            action.ShellScript = shellScript.Replace("{popclip text}", "{text}", StringComparison.OrdinalIgnoreCase);
            action.PassTextViaStdin = true;
        }
        else
        {
            error = "Config.yaml 未包含受支持的动作（支持 url、javascript、shell script）";
            return false;
        }

        manifest = new PluginManifest
        {
            Id = id,
            Name = name.Trim(),
            Icon = icon,
            Description = description.Trim(),
            Author = "PopClip Ext",
            Kind = kind,
            Requirements = requirements,
            Regex = regex,
            After = after,
            Action = action,
            Options = options,
            Source = PluginSourceType.ExternalFile,
            Enabled = true
        };
        return true;
    }

    private static bool TryParsePopClipExtPlist(
        string plistXml,
        string extFolderPath,
        string fallbackName,
        out PluginManifest? manifest,
        out string? error)
    {
        manifest = null;
        error = null;
        try
        {
            var doc = XDocument.Parse(plistXml);
            var rootDict = doc.Root?.Element("dict");
            if (rootDict is null)
            {
                error = "Config.plist 缺少顶层 dict 结构";
                return false;
            }

            var rootMap = ParsePlistDict(rootDict);
            var name = GetPlistString(rootMap, "Extension Name")
                       ?? GetPlistString(rootMap, "name")
                       ?? fallbackName;
            var id = GetPlistString(rootMap, "Extension Identifier")
                     ?? GetPlistString(rootMap, "identifier")
                     ?? "popclip." + SanitizeId(name);
            var description = GetPlistString(rootMap, "Extension Description")
                              ?? GetPlistString(rootMap, "description")
                              ?? "PopClip .popclipext 扩展";

            Dictionary<string, object>? firstAction = null;
            if (rootMap.TryGetValue("Actions", out var actionsObj) && actionsObj is List<object> actionList)
            {
                firstAction = actionList.OfType<Dictionary<string, object>>().FirstOrDefault();
            }

            var actionMap = firstAction ?? rootMap;
            var title = GetPlistString(actionMap, "Title")
                        ?? GetPlistString(actionMap, "title")
                        ?? name;
            var rawIcon = GetPlistString(actionMap, "Icon")
                          ?? GetPlistString(actionMap, "Image File")
                          ?? GetPlistString(rootMap, "Icon")
                          ?? GetPlistString(rootMap, "Image File")
                          ?? GetPlistString(rootMap, "Extension Image File");
            var icon = NormalizePopClipIcon(rawIcon, title, extFolderPath);
            var regex = GetPlistString(actionMap, "Regular Expression")
                        ?? GetPlistString(actionMap, "regex")
                        ?? GetPlistString(rootMap, "Regular Expression")
                        ?? GetPlistString(rootMap, "regex");
            var after = ParseAfterAction(GetPlistString(actionMap, "After") ?? GetPlistString(rootMap, "After"));

            var reqs = GetPlistStringList(actionMap, "Requirements");
            if (reqs.Count == 0) reqs = GetPlistStringList(rootMap, "Requirements");
            if (reqs.Count == 0) reqs.Add("text");

            var entitlements = GetPlistStringList(rootMap, "Entitlements");
            if (entitlements.Count == 0) entitlements = GetPlistStringList(actionMap, "Entitlements");

            var options = ParsePlistOptions(rootMap);

            var moduleFile = GetPlistString(actionMap, "Module")
                             ?? GetPlistString(rootMap, "Module")
                             ?? GetPlistString(actionMap, "module")
                             ?? GetPlistString(rootMap, "module");
            var url = GetPlistString(actionMap, "URL") ?? GetPlistString(rootMap, "URL");
            var jsFile = GetPlistString(actionMap, "JavaScript File")
                         ?? GetPlistString(rootMap, "JavaScript File")
                         ?? GetPlistString(actionMap, "javaScriptFile")
                         ?? GetPlistString(rootMap, "javaScriptFile");
            var jsInline = GetPlistString(actionMap, "JavaScript")
                           ?? GetPlistString(rootMap, "JavaScript")
                           ?? GetPlistString(actionMap, "javaScript")
                           ?? GetPlistString(rootMap, "javaScript");
            var keyCombo = GetPlistString(actionMap, "Key Combo")
                           ?? GetPlistString(rootMap, "Key Combo")
                           ?? GetPlistString(actionMap, "keyCombo")
                           ?? GetPlistString(rootMap, "keyCombo");
            var shellFile = GetPlistString(actionMap, "Shell Script File") ?? GetPlistString(rootMap, "Shell Script File");
            var shellInline = GetPlistString(actionMap, "Shell Script") ?? GetPlistString(rootMap, "Shell Script");
            var interpreter = GetPlistString(actionMap, "Script Interpreter")
                              ?? GetPlistString(actionMap, "Interpreter")
                              ?? GetPlistString(rootMap, "Interpreter");

            // Modern PopClip builds often ship Config.ts / Config.js alongside Config.plist
            if (string.IsNullOrWhiteSpace(moduleFile) &&
                string.IsNullOrWhiteSpace(url) &&
                string.IsNullOrWhiteSpace(jsFile) &&
                string.IsNullOrWhiteSpace(jsInline) &&
                string.IsNullOrWhiteSpace(keyCombo) &&
                string.IsNullOrWhiteSpace(shellFile) &&
                string.IsNullOrWhiteSpace(shellInline))
            {
                var implicitScript = FindFileCaseInsensitive(extFolderPath, "Config.ts")
                                     ?? FindFileCaseInsensitive(extFolderPath, "Config.js")
                                     ?? FindFileCaseInsensitive(extFolderPath, "index.ts")
                                     ?? FindFileCaseInsensitive(extFolderPath, "index.js")
                                     ?? Directory.EnumerateFiles(extFolderPath, "*.ts", SearchOption.TopDirectoryOnly).FirstOrDefault()
                                     ?? Directory.EnumerateFiles(extFolderPath, "*.js", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (implicitScript is not null)
                {
                    moduleFile = Path.GetFileName(implicitScript);
                }
            }

            var action = new PluginActionConfig
            {
                Entitlements = entitlements,
                WorkingDirectory = extFolderPath
            };
            PluginKind kind;

            if (!string.IsNullOrWhiteSpace(moduleFile))
            {
                var fullModule = ResolveRelativeFile(extFolderPath, moduleFile);
                if (fullModule is null)
                {
                    error = $"Config.plist 引用的模块文件不存在: {moduleFile}";
                    return false;
                }
                kind = PluginKind.JavaScript;
                if (!ResolveOrCompileJsModule(extFolderPath, fullModule, action, out var compiledOpts, out error))
                {
                    return false;
                }
                if (options.Count == 0 && compiledOpts.Count > 0)
                {
                    options = compiledOpts;
                }
                else if (options.Count == 0 && !string.IsNullOrWhiteSpace(action.JavaScriptCode))
                {
                    options = ExtractOptionsFromModuleSource(action.JavaScriptCode);
                }
            }
            else if (!string.IsNullOrWhiteSpace(jsFile))
            {
                var fullJs = ResolveRelativeFile(extFolderPath, jsFile);
                if (fullJs is null)
                {
                    error = $"Config.plist 引用的脚本文件不存在: {jsFile}";
                    return false;
                }
                kind = PluginKind.JavaScript;
                action.JavaScriptCode = File.ReadAllText(fullJs);
            }
            else if (!string.IsNullOrWhiteSpace(jsInline))
            {
                kind = PluginKind.JavaScript;
                action.JavaScriptCode = jsInline;
            }
            else if (!string.IsNullOrWhiteSpace(keyCombo))
            {
                kind = PluginKind.JavaScript;
                action.JavaScriptCode = $"popclip.pressKey({JsonSerializer.Serialize(keyCombo)});";
            }
            else if (!string.IsNullOrWhiteSpace(url))
            {
                kind = PluginKind.Url;
                action.UrlTemplate = ConvertPopClipUrlPlaceholders(url);
            }
            else if (!string.IsNullOrWhiteSpace(shellFile))
            {
                var fullShell = ResolveRelativeFile(extFolderPath, shellFile);
                if (fullShell is null)
                {
                    error = $"Config.plist 引用的脚本文件不存在: {shellFile}";
                    return false;
                }
                kind = PluginKind.Shell;
                action.ShellExecutable = MapInterpreter(interpreter);
                action.ShellScript = File.ReadAllText(fullShell);
                action.PassTextViaStdin = true;
            }
            else if (!string.IsNullOrWhiteSpace(shellInline))
            {
                kind = PluginKind.Shell;
                action.ShellExecutable = MapInterpreter(interpreter);
                action.ShellScript = shellInline.Replace("{popclip text}", "{text}", StringComparison.OrdinalIgnoreCase);
                action.PassTextViaStdin = true;
            }
            else
            {
                error = "该 macOS Config.plist 仅包含 macOS 专属动作（如 AppleScript / Service），Windows 暂不支持";
                return false;
            }

            manifest = new PluginManifest
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(title) ? name : title,
                Icon = icon,
                Description = description,
                Author = "PopClip Ext",
                Kind = kind,
                Requirements = reqs,
                Regex = regex,
                After = after,
                Action = action,
                Options = options,
                Source = PluginSourceType.ExternalFile,
                Enabled = true
            };
            return true;
        }
        catch (Exception ex)
        {
            error = $"解析 Config.plist 失败: {ex.Message}";
            return false;
        }
    }

    private static Dictionary<string, object> ParsePlistDict(XElement dictElement)
    {
        var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var children = dictElement.Elements().ToList();
        for (var i = 0; i + 1 < children.Count; i += 2)
        {
            if (!children[i].Name.LocalName.Equals("key", StringComparison.OrdinalIgnoreCase)) continue;
            var key = children[i].Value.Trim();
            var val = ParsePlistValue(children[i + 1]);
            if (val is not null) map[key] = val;
        }
        return map;
    }

    private static object? ParsePlistValue(XElement el) => el.Name.LocalName.ToLowerInvariant() switch
    {
        "string" => el.Value,
        "true" => true,
        "false" => false,
        "integer" => el.Value,
        "dict" => ParsePlistDict(el),
        "array" => el.Elements().Select(ParsePlistValue).Where(v => v is not null).Cast<object>().ToList(),
        _ => null
    };

    private static string? GetPlistString(Dictionary<string, object> map, string key)
    {
        if (!map.TryGetValue(key, out var val)) return null;
        if (val is string s && !string.IsNullOrWhiteSpace(s)) return s.Trim();
        if (val is Dictionary<string, object> localized)
        {
            return GetPlistString(localized, "zh-Hans")
                   ?? GetPlistString(localized, "zh")
                   ?? GetPlistString(localized, "en")
                   ?? localized.Values.OfType<string>().FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
        }
        return null;
    }

    private static List<string> GetPlistStringList(Dictionary<string, object> map, string key)
    {
        if (map.TryGetValue(key, out var obj) && obj is List<object> list)
        {
            return list.OfType<string>().Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }
        return new List<string>();
    }

    private static List<PluginOptionDefinition> ParsePlistOptions(Dictionary<string, object> rootMap)
    {
        var result = new List<PluginOptionDefinition>();
        if (!rootMap.TryGetValue("Options", out var obj) || obj is not List<object> list)
        {
            return result;
        }

        foreach (var optDict in list.OfType<Dictionary<string, object>>())
        {
            var key = GetPlistString(optDict, "Option Identifier") ?? GetPlistString(optDict, "identifier");
            if (string.IsNullOrWhiteSpace(key)) continue;

            var label = GetPlistString(optDict, "Option Label") ?? GetPlistString(optDict, "label") ?? key;
            var defVal = GetPlistString(optDict, "Option Default Value")
                         ?? GetPlistString(optDict, "defaultValue")
                         ?? GetPlistString(optDict, "default value")
                         ?? string.Empty;
            var desc = GetPlistString(optDict, "Option Description") ?? GetPlistString(optDict, "description") ?? string.Empty;
            var rawType = (GetPlistString(optDict, "Option Type") ?? GetPlistString(optDict, "type") ?? "string").ToLowerInvariant();
            var choices = GetPlistStringList(optDict, "Option Values");
            if (choices.Count == 0) choices = GetPlistStringList(optDict, "values");

            result.Add(new PluginOptionDefinition
            {
                Key = key,
                Label = label,
                DefaultValue = defVal,
                Description = desc,
                Choices = choices,
                Type = rawType switch
                {
                    "boolean" => PluginOptionType.Boolean,
                    "password" or "secret" => PluginOptionType.Password,
                    "multiple" => PluginOptionType.Choice,
                    _ => PluginOptionType.String
                }
            });
        }

        return result;
    }

    private static string? ResolveRelativeFile(string extFolderPath, string relativeOrName)
    {
        var candidate = Path.Combine(extFolderPath, relativeOrName);
        if (File.Exists(candidate)) return candidate;
        return FindFileCaseInsensitive(extFolderPath, Path.GetFileName(relativeOrName));
    }

    private static List<PluginOptionDefinition> ExtractOptionsFromModuleSource(string jsSource)
    {
        var result = new List<PluginOptionDefinition>();
        try
        {
            // Lightweight extraction of option identifiers & defaults from compiled PopClip JS modules
            var matches = Regex.Matches(jsSource, @"identifier\s*:\s*[""']([a-zA-Z0-9_\-]+)[""']([\s\S]{0,400}?)(?=(?:identifier\s*:|\]))", RegexOptions.IgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in matches)
            {
                var key = m.Groups[1].Value;
                if (!seen.Add(key)) continue;
                var block = m.Groups[2].Value;
                var labelMatch = Regex.Match(block, @"(?:""zh-Hans""|label)\s*:\s*[""']([^""']+)[""']");
                var defMatch = Regex.Match(block, @"defaultValue\s*:\s*[""']([^""']*)[""']");
                var typeMatch = Regex.Match(block, @"type\s*:\s*[""']([^""']+)[""']");
                var rawType = typeMatch.Success ? typeMatch.Groups[1].Value.ToLowerInvariant() : "string";

                result.Add(new PluginOptionDefinition
                {
                    Key = key,
                    Label = labelMatch.Success ? labelMatch.Groups[1].Value : key,
                    DefaultValue = defMatch.Success ? defMatch.Groups[1].Value : string.Empty,
                    Type = rawType switch
                    {
                        "boolean" => PluginOptionType.Boolean,
                        "password" or "secret" => PluginOptionType.Password,
                        "multiple" => PluginOptionType.Choice,
                        _ => PluginOptionType.String
                    }
                });
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Parses a .popclipext folder's Config.json file if present.
    /// </summary>
    public static bool TryParsePopClipExtJson(string jsonText, string extFolderPath, out PluginManifest? manifest)
        => TryParsePopClipExtJson(jsonText, extFolderPath, out manifest, out _);

    public static bool TryParsePopClipExtJson(string jsonText, string extFolderPath, out PluginManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(jsonText, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var name = TryGetString(root, "name");
            if (string.IsNullOrWhiteSpace(name)) return false;

            var id = TryGetString(root, "identifier") ?? "popclip." + SanitizeId(name);
            var description = TryGetString(root, "description") ?? "PopClip .popclipext 扩展";
            var after = ParseAfterAction(TryGetString(root, "after"));
            var regex = TryGetString(root, "regex");

            // Support top-level or first action in "actions" array
            var actionElement = root;
            if (root.TryGetProperty("actions", out var actionsEl) &&
                actionsEl.ValueKind == JsonValueKind.Array &&
                actionsEl.GetArrayLength() > 0)
            {
                actionElement = actionsEl[0];
                after = ParseAfterAction(TryGetString(actionElement, "after") ?? TryGetString(root, "after"));
                regex ??= TryGetString(actionElement, "regex");
            }

            var rawIcon = TryGetString(actionElement, "icon")
                          ?? TryGetString(actionElement, "image file")
                          ?? TryGetString(root, "icon")
                          ?? TryGetString(root, "image file");
            var icon = NormalizePopClipIcon(rawIcon, name, extFolderPath);

            var hasExplicitReqs = HasProperty(actionElement, "requirements") || HasProperty(root, "requirements");
            var requirements = TryGetStringList(actionElement, "requirements");
            if (requirements.Count == 0 && !HasProperty(actionElement, "requirements"))
            {
                requirements = TryGetStringList(root, "requirements");
            }
            if (!hasExplicitReqs && requirements.Count == 0)
            {
                requirements.Add("text");
            }

            var entitlements = TryGetStringList(root, "entitlements");
            var moduleFile = TryGetString(actionElement, "module") ?? TryGetString(root, "module");
            var url = TryGetString(actionElement, "url");
            var jsFile = TryGetString(actionElement, "javascript file") ?? TryGetString(root, "javascript file");
            var jsInline = TryGetString(actionElement, "javascript") ?? TryGetString(root, "javascript");
            var keyCombo = TryGetString(actionElement, "key combo") ?? TryGetString(root, "key combo");
            var shellScript = TryGetString(actionElement, "shell script");

            PluginKind kind;
            var action = new PluginActionConfig { Entitlements = entitlements, WorkingDirectory = extFolderPath };
            var options = new List<PluginOptionDefinition>();

            if (!string.IsNullOrWhiteSpace(moduleFile))
            {
                var fullModule = ResolveRelativeFile(extFolderPath, moduleFile);
                if (fullModule is null) { error = $"未找到模块文件: {moduleFile}"; return false; }
                kind = PluginKind.JavaScript;
                if (!ResolveOrCompileJsModule(extFolderPath, fullModule, action, out options, out error))
                {
                    return false;
                }
            }
            else if (!string.IsNullOrWhiteSpace(jsFile))
            {
                var fullJsPath = ResolveRelativeFile(extFolderPath, jsFile);
                if (fullJsPath is null) { error = $"未找到脚本文件: {jsFile}"; return false; }
                kind = PluginKind.JavaScript;
                if (PopClipExtensionCompiler.NeedsCompilation(fullJsPath))
                {
                    if (!ResolveOrCompileJsModule(extFolderPath, fullJsPath, action, out options, out error))
                    {
                        return false;
                    }
                }
                else
                {
                    action.JavaScriptCode = File.ReadAllText(fullJsPath);
                }
            }
            else if (!string.IsNullOrWhiteSpace(jsInline))
            {
                kind = PluginKind.JavaScript;
                action.JavaScriptCode = jsInline;
            }
            else if (!string.IsNullOrWhiteSpace(keyCombo))
            {
                kind = PluginKind.JavaScript;
                action.JavaScriptCode = $"popclip.pressKey({JsonSerializer.Serialize(keyCombo)});";
            }
            else if (!string.IsNullOrWhiteSpace(url))
            {
                kind = PluginKind.Url;
                action.UrlTemplate = ConvertPopClipUrlPlaceholders(url);
            }
            else if (!string.IsNullOrWhiteSpace(shellScript))
            {
                kind = PluginKind.Shell;
                action.ShellExecutable = MapInterpreter(TryGetString(actionElement, "interpreter"));
                action.ShellScript = shellScript;
                action.PassTextViaStdin = true;
            }
            else
            {
                return false;
            }

            manifest = new PluginManifest
            {
                Id = id,
                Name = name,
                Icon = icon,
                Description = description,
                Author = "PopClip Ext",
                Kind = kind,
                Requirements = requirements,
                Regex = regex,
                After = after,
                Action = action,
                Options = options,
                Source = PluginSourceType.ExternalFile,
                SourceFilePath = Path.Combine(extFolderPath, "Config.json"),
                Enabled = true
            };
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string ConvertPopClipUrlPlaceholders(string url)
    {
        return url
            .Replace("***", "{encodedText}", StringComparison.Ordinal)
            .Replace("{popclip text}", "{encodedText}", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] SupportedImageExtensions =
    {
        ".png", ".svg", ".jpg", ".jpeg", ".ico", ".bmp", ".webp"
    };

    public static bool IsImageIconFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var ext = Path.GetExtension(value.Trim());
        return SupportedImageExtensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    public static string ResolveIconFileIfLocal(string rawIcon, string baseDirectory)
    {
        var trimmed = rawIcon.Trim();
        if (trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("file-color:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("symbol:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (IsImageIconFileName(trimmed))
        {
            var resolved = Path.IsPathRooted(trimmed)
                ? (File.Exists(trimmed) ? Path.GetFullPath(trimmed) : null)
                : ResolveRelativeFile(baseDirectory, trimmed);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return $"file:{resolved}";
            }
        }

        return trimmed;
    }

    public static string NormalizePopClipIcon(string? rawIcon, string? fallbackName = null, string? extFolderPath = null)
    {
        if (string.IsNullOrWhiteSpace(rawIcon))
        {
            return GuessFallbackSymbol(fallbackName);
        }

        var trimmed = rawIcon.Trim().Trim('"', '\'');
        var preserveColor = Regex.IsMatch(trimmed, @"\bpreserve-color\b", RegexOptions.IgnoreCase);
        // Strip PopClip icon transform prefixes such as "scale=120 move-x=3 circle filled Tc"
        trimmed = Regex.Replace(trimmed, @"\b(?:scale|move-x|move-y|rotate|flip|preserve-color|monochrome)(?:=[^\s]+)?\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
        var lower = trimmed.ToLowerInvariant();

        if (lower.StartsWith("file:", StringComparison.Ordinal) || lower.StartsWith("file-color:", StringComparison.Ordinal))
        {
            return trimmed;
        }

        // Bundled image/SVG file icons (e.g. g.png, Baidu.png, itrans.png, select-all.svg):
        // Resolve the actual asset inside the .popclipext folder when present so PopClip extensions display
        // their authentic icons; fall back to a crisp symbol/badge only if the referenced asset file is missing.
        if (IsImageIconFileName(trimmed) || lower.EndsWith(".icns", StringComparison.Ordinal))
        {
            if (IsImageIconFileName(trimmed))
            {
                string? resolvedPath = null;
                if (Path.IsPathRooted(trimmed) && File.Exists(trimmed))
                {
                    resolvedPath = Path.GetFullPath(trimmed);
                }
                else if (!string.IsNullOrWhiteSpace(extFolderPath) && Directory.Exists(extFolderPath))
                {
                    resolvedPath = ResolveRelativeFile(extFolderPath, trimmed);
                }

                if (!string.IsNullOrWhiteSpace(resolvedPath))
                {
                    return preserveColor ? $"file-color:{resolvedPath}" : $"file:{resolvedPath}";
                }
            }

            return GuessFallbackSymbol(fallbackName ?? Path.GetFileNameWithoutExtension(trimmed));
        }

        // Keep PopClip monogram syntax ("square filled AB", "circle G", "square AI", "search filled E")
        if (lower.StartsWith("square ") || lower.StartsWith("circle "))
        {
            return trimmed;
        }
        if (lower.StartsWith("search "))
        {
            return "symbol:search";
        }

        return lower switch
        {
            "search" or "symbol:magnifyingglass" or "symbol:search" => "symbol:search",
            "symbol:globe" or "globe" => "symbol:globe",
            "symbol:link" or "link" => "symbol:link",
            "symbol:doc.on.doc" or "copy" or "symbol:copy" => "symbol:copy",
            "symbol:scissors" or "cut" or "symbol:cut" => "symbol:cut",
            "symbol:paste" or "paste" => "symbol:paste",
            "symbol:sparkles" or "sparkles" or "symbol:sparkle" => "symbol:sparkle",
            "symbol:terminal" or "terminal" => "symbol:terminal",
            "symbol:envelope" or "mail" or "symbol:mail" => "symbol:mail",
            "symbol:book" or "book" => "symbol:book",
            "symbol:translate" or "translate" => "symbol:translate",
            "symbol:code" or "code" => "symbol:code",
            "symbol:braces" => "symbol:braces",
            "symbol:swap" => "symbol:swap",
            "symbol:count" => "symbol:count",
            _ when lower.StartsWith("symbol:") => GuessFallbackSymbol(fallbackName),
            _ => trimmed
        };
    }

    private static string GuessFallbackSymbol(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "symbol:sparkle";
        var lower = name.ToLowerInvariant();
        if (lower.Contains("translat") || lower.Contains("翻译") || lower.Contains("deepl") || lower.Contains("itrans")) return "symbol:translate";
        if (lower.Contains("search") || lower.Contains("搜索") || lower.Contains("google") || lower.Contains("bing") || lower.Contains("wiki")) return "symbol:search";
        if (lower.Contains("link") || lower.Contains("url") || lower.Contains("open")) return "symbol:link";
        if (lower.Contains("code") || lower.Contains("json") || lower.Contains("dev")) return "symbol:code";
        if (lower.Contains("mail") || lower.Contains("email")) return "symbol:mail";
        if (lower.Contains("term") || lower.Contains("shell")) return "symbol:terminal";

        var words = name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        var initials = words.Length >= 2
            ? $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}"
            : name.Trim()[..Math.Min(2, name.Trim().Length)].ToUpperInvariant();
        return $"square {initials}";
    }

    private static Dictionary<string, string> ParseSimpleYaml(List<string> lines, out List<PluginOptionDefinition> options)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        options = new List<PluginOptionDefinition>();

        var inOptions = false;
        PluginOptionDefinition? currentOption = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;

            var indent = raw.Length - raw.TrimStart().Length;

            if (indent == 0)
            {
                inOptions = false;
                currentOption = null;

                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx <= 0) continue;

                var key = trimmed[..colonIdx].Trim();
                var val = Unquote(trimmed[(colonIdx + 1)..].Trim());

                if (key.Equals("options", StringComparison.OrdinalIgnoreCase))
                {
                    inOptions = true;
                    continue;
                }

                // Multi-line block scalar (| or >)
                if (val == "|" || val == ">" || string.IsNullOrEmpty(val))
                {
                    var blockLines = new List<string>();
                    var j = i + 1;
                    while (j < lines.Count)
                    {
                        var nextRaw = lines[j];
                        if (string.IsNullOrWhiteSpace(nextRaw))
                        {
                            blockLines.Add(string.Empty);
                            j++;
                            continue;
                        }
                        var nextIndent = nextRaw.Length - nextRaw.TrimStart().Length;
                        if (nextIndent == 0) break;
                        blockLines.Add(nextRaw.TrimStart());
                        j++;
                    }
                    if (blockLines.Count > 0)
                    {
                        // Check if this is a YAML bullet list (e.g., requirements:\n  - url)
                        if (blockLines.All(b => string.IsNullOrEmpty(b) || b.StartsWith("- ")))
                        {
                            fields[key] = "[" + string.Join(", ", blockLines.Where(b => b.StartsWith("- ")).Select(b => Unquote(b[2..].Trim()))) + "]";
                        }
                        else
                        {
                            fields[key] = string.Join('\n', blockLines).TrimEnd();
                        }
                        i = j - 1;
                        continue;
                    }
                }

                fields[key] = val;
            }
            else if (inOptions)
            {
                var lineContent = trimmed;
                if (lineContent.StartsWith("- "))
                {
                    currentOption = new PluginOptionDefinition { Key = "option", Label = "选项" };
                    options.Add(currentOption);
                    lineContent = lineContent[2..].Trim();
                }

                if (currentOption is not null)
                {
                    var cIdx = lineContent.IndexOf(':');
                    if (cIdx > 0)
                    {
                        var ok = lineContent[..cIdx].Trim().ToLowerInvariant();
                        var ov = Unquote(lineContent[(cIdx + 1)..].Trim());
                        switch (ok)
                        {
                            case "identifier" or "key":
                                currentOption.Key = ov;
                                if (currentOption.Label == "选项") currentOption.Label = ov;
                                break;
                            case "label":
                                currentOption.Label = ov;
                                break;
                            case "default value" or "default_value" or "default":
                                currentOption.DefaultValue = ov;
                                break;
                            case "description":
                                currentOption.Description = ov;
                                break;
                            case "type":
                                currentOption.Type = ov.ToLowerInvariant() switch
                                {
                                    "boolean" => PluginOptionType.Boolean,
                                    "password" or "secret" => PluginOptionType.Password,
                                    "multiple" => PluginOptionType.Choice,
                                    _ => PluginOptionType.String
                                };
                                break;
                        }
                    }
                }
            }
        }

        return fields;
    }

    private static string? GetField(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static List<string> ParseList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        var cleaned = raw.Trim().TrimStart('[').TrimEnd(']');
        return cleaned.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Unquote)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static AfterActionType ParseAfterAction(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return AfterActionType.None;
        return raw.Trim().ToLowerInvariant() switch
        {
            "copy-result" or "copyresult" => AfterActionType.CopyResult,
            "paste-result" or "pasteresult" => AfterActionType.PasteResult,
            "show-result" or "showresult" or "preview-result" or "show-status" => AfterActionType.ShowResult,
            _ => AfterActionType.None
        };
    }

    private static string MapInterpreter(string? interpreter)
    {
        if (string.IsNullOrWhiteSpace(interpreter)) return "powershell";
        var lower = interpreter.ToLowerInvariant();
        if (lower.Contains("python")) return "python";
        if (lower.Contains("node")) return "node";
        if (lower.Contains("pwsh")) return "pwsh";
        if (lower.Contains("cmd")) return "cmd";
        return "powershell";
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
        {
            return s[1..^1];
        }
        return s;
    }

    private static string SanitizeId(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is ' ' or '-' or '_') sb.Append('-');
        }
        var res = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(res) ? Guid.NewGuid().ToString("N")[..8] : res;
    }

    private static bool HasProperty(JsonElement el, string prop)
    {
        foreach (var p in el.EnumerateObject())
        {
            if (p.Name.Equals(prop, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string? TryGetString(JsonElement el, string prop)
    {
        foreach (var p in el.EnumerateObject())
        {
            if (p.Name.Equals(prop, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
            {
                return p.Value.GetString();
            }
        }
        return null;
    }

    private static List<string> TryGetStringList(JsonElement el, string prop)
    {
        foreach (var p in el.EnumerateObject())
        {
            if (p.Name.Equals(prop, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array)
            {
                return p.Value.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }
        }
        return new List<string>();
    }
}
