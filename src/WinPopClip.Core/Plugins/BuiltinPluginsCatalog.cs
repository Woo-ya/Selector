using WinPopClip.Core.Models;

namespace WinPopClip.Core.Plugins;

/// <summary>
/// Provides the default built-in plugins shipped with Selector, using crisp macOS-style monochrome vector symbols.
/// </summary>
public sealed class BuiltinPluginsCatalog : IPluginProvider
{
    public string ProviderName => "BuiltIn";

    public IEnumerable<PluginManifest> LoadPlugins()
    {
        return new List<PluginManifest>
        {
            new()
            {
                Id = "builtin.copy",
                Name = "复制",
                Icon = "symbol:copy",
                Description = "复制当前选中文本到剪贴板",
                Author = "Selector",
                Kind = PluginKind.Builtin,
                Requirements = new() { "text" },
                After = AfterActionType.None,
                Action = new PluginActionConfig { BuiltinCommand = "copy" },
                Source = PluginSourceType.BuiltIn,
                Enabled = true,
                Order = 0
            },
            new()
            {
                Id = "builtin.cut",
                Name = "剪切",
                Icon = "symbol:cut",
                Description = "在可编辑区域剪切选中文本",
                Author = "Selector",
                Kind = PluginKind.Builtin,
                Requirements = new() { "text", "editable" },
                After = AfterActionType.None,
                Action = new PluginActionConfig { BuiltinCommand = "cut" },
                Source = PluginSourceType.BuiltIn,
                Enabled = true,
                Order = 1
            },
            new()
            {
                Id = "builtin.paste",
                Name = "粘贴",
                Icon = "symbol:paste",
                Description = "在可编辑区域用剪贴板内容替换选中文本",
                Author = "Selector",
                Kind = PluginKind.Builtin,
                Requirements = new() { "text", "editable" },
                After = AfterActionType.None,
                Action = new PluginActionConfig { BuiltinCommand = "paste" },
                Source = PluginSourceType.BuiltIn,
                Enabled = true,
                Order = 2
            },
            new()
            {
                Id = "builtin.open-url",
                Name = "打开链接",
                Icon = "symbol:link",
                Description = "当选中文本包含网址时在默认浏览器打开",
                Author = "Selector",
                Kind = PluginKind.Builtin,
                Requirements = new() { "url" },
                After = AfterActionType.None,
                Action = new PluginActionConfig { BuiltinCommand = "open-url" },
                Source = PluginSourceType.BuiltIn,
                Enabled = true,
                Order = 3
            },
            new()
            {
                Id = "builtin.web-search",
                Name = "网页搜索",
                Icon = "symbol:search",
                Description = "使用配置的搜索引擎检索选中文本",
                Author = "Selector",
                Kind = PluginKind.Url,
                Requirements = new() { "text" },
                MaxLength = 300,
                After = AfterActionType.None,
                Action = new PluginActionConfig
                {
                    UrlTemplate = "{option.searchBaseUrl}{encodedText}"
                },
                Options = new()
                {
                    new PluginOptionDefinition
                    {
                        Key = "searchBaseUrl",
                        Label = "搜索引擎前缀 URL",
                        Type = PluginOptionType.String,
                        DefaultValue = "https://www.bing.com/search?q=",
                        Description = "如 https://www.google.com/search?q= 或 https://www.bing.com/search?q="
                    }
                },
                Source = PluginSourceType.BuiltIn,
                Enabled = true,
                Order = 4
            },
            new()
            {
                Id = "builtin.word-count",
                Name = "字数统计",
                Icon = "symbol:count",
                Description = "统计字符数、词数与行数并以气泡提示",
                Author = "Selector",
                Kind = PluginKind.Builtin,
                Requirements = new() { "text" },
                After = AfterActionType.ShowResult,
                Action = new PluginActionConfig { BuiltinCommand = "word-count" },
                Source = PluginSourceType.BuiltIn,
                Enabled = true,
                Order = 5
            },
            new()
            {
                Id = "builtin.uppercase",
                Name = "转大写",
                Icon = "square AA",
                Description = "将英文文本转为大写（可编辑区域原位替换，否则复制）",
                Author = "Selector",
                Kind = PluginKind.TextTransform,
                Requirements = new() { "text" },
                Regex = @"[a-z]",
                After = AfterActionType.PasteResult,
                Action = new PluginActionConfig { TransformOperation = "uppercase" },
                Source = PluginSourceType.BuiltIn,
                Enabled = true,
                Order = 6
            },
            new()
            {
                Id = "builtin.lowercase",
                Name = "转小写",
                Icon = "square aa",
                Description = "将英文文本转为小写（可编辑区域原位替换，否则复制）",
                Author = "Selector",
                Kind = PluginKind.TextTransform,
                Requirements = new() { "text" },
                Regex = @"[A-Z]",
                After = AfterActionType.PasteResult,
                Action = new PluginActionConfig { TransformOperation = "lowercase" },
                Source = PluginSourceType.BuiltIn,
                Enabled = false,
                Order = 7
            },
            new()
            {
                Id = "builtin.json-pretty",
                Name = "JSON 格式化",
                Icon = "symbol:braces",
                Description = "识别 JSON 文本并格式化输出",
                Author = "Selector",
                Kind = PluginKind.TextTransform,
                Requirements = new() { "text" },
                Regex = @"^\s*[\{\[][\s\S]*[\}\]]\s*$",
                After = AfterActionType.CopyResult,
                Action = new PluginActionConfig { TransformOperation = "json-pretty" },
                Source = PluginSourceType.BuiltIn,
                Enabled = true,
                Order = 8
            },
            new()
            {
                Id = "builtin.base64-encode",
                Name = "Base64 编码",
                Icon = "square B64",
                Description = "将选中文本进行 Base64 编码并复制",
                Author = "Selector",
                Kind = PluginKind.TextTransform,
                Requirements = new() { "text" },
                After = AfterActionType.CopyResult,
                Action = new PluginActionConfig { TransformOperation = "base64-encode" },
                Source = PluginSourceType.BuiltIn,
                Enabled = false,
                Order = 9
            }
        };
    }
}
