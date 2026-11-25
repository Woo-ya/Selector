using System.Text.Json.Serialization;

namespace WinPopClip.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginKind
{
    Builtin,
    Url,
    TextTransform,
    Shell,
    Http,
    JavaScript
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AfterActionType
{
    None,
    CopyResult,
    PasteResult,
    ShowResult
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginSourceType
{
    BuiltIn,
    ExternalFile,
    UserCustom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginOptionType
{
    String,
    Boolean,
    Password,
    Choice
}

public sealed class PluginOptionDefinition
{
    public required string Key { get; set; }
    public required string Label { get; set; }
    public PluginOptionType Type { get; set; } = PluginOptionType.String;
    public string DefaultValue { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Choices { get; set; } = new();
}

/// <summary>
/// Declarative execution payload consumed by the matching IPluginExecutor.
/// </summary>
public sealed class PluginActionConfig
{
    /// <summary>Built-in command key: copy, cut, paste, open-url, word-count, install-popclip.</summary>
    public string? BuiltinCommand { get; set; }

    /// <summary>URL template supporting {text}, {encodedText}, {option.xxx}.</summary>
    public string? UrlTemplate { get; set; }

    /// <summary>Transform operation: uppercase, lowercase, titlecase, trim, json-pretty, url-encode, url-decode, base64-encode, base64-decode, regex-replace.</summary>
    public string? TransformOperation { get; set; }
    public string? TransformPattern { get; set; }
    public string? TransformReplacement { get; set; }

    /// <summary>Shell execution configuration.</summary>
    public string? ShellExecutable { get; set; } = "powershell";
    public string? ShellArguments { get; set; }
    public string? ShellScript { get; set; }
    public bool PassTextViaStdin { get; set; } = true;
    public int TimeoutMs { get; set; } = 10000;

    /// <summary>HTTP Webhook / API call configuration.</summary>
    public string? HttpMethod { get; set; } = "POST";
    public string? HttpUrl { get; set; }
    public Dictionary<string, string> HttpHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? HttpBodyTemplate { get; set; }
    public string? HttpResponseJsonPath { get; set; }

    /// <summary>PopClip-compatible JavaScript snippet body (has access to global popclip object).</summary>
    public string? JavaScriptCode { get; set; }

    /// <summary>Declared permissions such as "network" for PopClip JS extensions.</summary>
    public List<string> Entitlements { get; set; } = new();
}

/// <summary>
/// Declarative plugin definition compatible with WinPopClip JSON and PopClip snippets/extensions.
/// </summary>
public sealed class PluginManifest
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string Icon { get; set; } = "⚡";
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = "WinPopClip";
    public PluginKind Kind { get; set; } = PluginKind.Url;

    /// <summary>
    /// Supported requirements: text, url, email, number, editable, paste, multiline, singleline.
    /// </summary>
    public List<string> Requirements { get; set; } = new() { "text" };

    /// <summary>Optional regular expression the selected text must match.</summary>
    public string? Regex { get; set; }

    /// <summary>Optional length constraints.</summary>
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }

    /// <summary>Process name whitelist/blacklist (without .exe, case-insensitive).</summary>
    public List<string> AllowedApps { get; set; } = new();
    public List<string> BlockedApps { get; set; } = new();

    /// <summary>Post-execution behavior (copy-result, paste-result, show-result, none).</summary>
    public AfterActionType After { get; set; } = AfterActionType.None;

    /// <summary>Action execution config interpreted by the executor corresponding to Kind.</summary>
    public PluginActionConfig Action { get; set; } = new();

    /// <summary>Configurable user options exposed in Settings UI.</summary>
    public List<PluginOptionDefinition> Options { get; set; } = new();

    /// <summary>Runtime metadata (not necessarily stored in external files).</summary>
    [JsonIgnore]
    public PluginSourceType Source { get; set; } = PluginSourceType.BuiltIn;

    [JsonIgnore]
    public string? SourceFilePath { get; set; }

    [JsonIgnore]
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public int Order { get; set; }

    public PluginManifest Clone()
    {
        return new PluginManifest
        {
            Id = Id,
            Name = Name,
            Icon = Icon,
            Description = Description,
            Version = Version,
            Author = Author,
            Kind = Kind,
            Requirements = new List<string>(Requirements),
            Regex = Regex,
            MinLength = MinLength,
            MaxLength = MaxLength,
            AllowedApps = new List<string>(AllowedApps),
            BlockedApps = new List<string>(BlockedApps),
            After = After,
            Action = new PluginActionConfig
            {
                BuiltinCommand = Action.BuiltinCommand,
                UrlTemplate = Action.UrlTemplate,
                TransformOperation = Action.TransformOperation,
                TransformPattern = Action.TransformPattern,
                TransformReplacement = Action.TransformReplacement,
                ShellExecutable = Action.ShellExecutable,
                ShellArguments = Action.ShellArguments,
                ShellScript = Action.ShellScript,
                PassTextViaStdin = Action.PassTextViaStdin,
                TimeoutMs = Action.TimeoutMs,
                HttpMethod = Action.HttpMethod,
                HttpUrl = Action.HttpUrl,
                HttpHeaders = new Dictionary<string, string>(Action.HttpHeaders, StringComparer.OrdinalIgnoreCase),
                HttpBodyTemplate = Action.HttpBodyTemplate,
                HttpResponseJsonPath = Action.HttpResponseJsonPath,
                JavaScriptCode = Action.JavaScriptCode,
                Entitlements = new List<string>(Action.Entitlements)
            },
            Options = Options.Select(o => new PluginOptionDefinition
            {
                Key = o.Key,
                Label = o.Label,
                Type = o.Type,
                DefaultValue = o.DefaultValue,
                Description = o.Description,
                Choices = new List<string>(o.Choices)
            }).ToList(),
            Source = Source,
            SourceFilePath = SourceFilePath,
            Enabled = Enabled,
            Order = Order
        };
    }
}
