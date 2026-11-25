using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    public static bool TryParseSnippet(string rawText, out PluginManifest? manifest, out string? error, string? fallbackName = null)
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
        var icon = NormalizePopClipIcon(GetField(fields, "icon"));
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
    /// Parses a .popclipext folder's Config.json file if present.
    /// </summary>
    public static bool TryParsePopClipExtJson(string jsonText, string extFolderPath, out PluginManifest? manifest)
    {
        manifest = null;
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
            var icon = NormalizePopClipIcon(TryGetString(root, "icon"));
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

            var requirements = TryGetStringList(actionElement, "requirements");
            if (requirements.Count == 0) requirements = TryGetStringList(root, "requirements");
            if (requirements.Count == 0) requirements.Add("text");

            var entitlements = TryGetStringList(root, "entitlements");
            var url = TryGetString(actionElement, "url");
            var jsFile = TryGetString(actionElement, "javascript file") ?? TryGetString(root, "javascript file");
            var jsInline = TryGetString(actionElement, "javascript") ?? TryGetString(root, "javascript");
            var shellScript = TryGetString(actionElement, "shell script");

            PluginKind kind;
            var action = new PluginActionConfig { Entitlements = entitlements };

            if (!string.IsNullOrWhiteSpace(jsFile))
            {
                var fullJsPath = Path.Combine(extFolderPath, jsFile);
                if (!File.Exists(fullJsPath)) return false;
                kind = PluginKind.JavaScript;
                action.JavaScriptCode = File.ReadAllText(fullJsPath);
            }
            else if (!string.IsNullOrWhiteSpace(jsInline))
            {
                kind = PluginKind.JavaScript;
                action.JavaScriptCode = jsInline;
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
                Source = PluginSourceType.ExternalFile,
                SourceFilePath = Path.Combine(extFolderPath, "Config.json"),
                Enabled = true
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ConvertPopClipUrlPlaceholders(string url)
    {
        return url
            .Replace("***", "{encodedText}", StringComparison.Ordinal)
            .Replace("{popclip text}", "{encodedText}", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizePopClipIcon(string? rawIcon)
    {
        if (string.IsNullOrWhiteSpace(rawIcon))
        {
            return "symbol:sparkle";
        }

        var trimmed = rawIcon.Trim().Trim('"', '\'');
        var lower = trimmed.ToLowerInvariant();

        // Keep PopClip monogram syntax ("square filled AB", "circle G", "square AI") intact for badge rendering!
        if (lower.StartsWith("square ") || lower.StartsWith("circle "))
        {
            return trimmed;
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
            _ when lower.StartsWith("symbol:") => "symbol:sparkle",
            _ => trimmed
        };
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
