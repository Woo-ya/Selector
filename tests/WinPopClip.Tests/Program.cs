using System.Text.Json;
using WinPopClip.Core.Models;
using WinPopClip.Core.Plugins;
using WinPopClip.Core.Plugins.Executors;
using WinPopClip.Core.Storage;

var passed = 0;
var failed = 0;

async Task RunTest(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine($"[PASS] {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] {name}: {ex.Message}");
        failed++;
    }
}

void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var tempDir = Path.Combine(Path.GetTempPath(), "WinPopClipTests_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tempDir);

try
{
    await RunTest("ContextMatcher detects URLs, filters editable & password fields", async () =>
    {
        var matcher = new PluginContextMatcher();
        var catalog = new BuiltinPluginsCatalog().LoadPlugins().ToList();

        var pwdCtx = new SelectionContext { Text = "secret123", IsPasswordField = true, IsEditable = true };
        Assert(matcher.MatchPlugins(catalog, pwdCtx).Count == 0, "Password field should match no plugins");

        var urlCtx = new SelectionContext { Text = "https://github.com/pilotmoon/PopClip", IsEditable = false };
        var urlMatches = matcher.MatchPlugins(catalog, urlCtx);
        Assert(urlMatches.Any(m => m.Plugin.Id == "builtin.open-url"), "URL selection should match open-url");
        Assert(!urlMatches.Any(m => m.Plugin.Id == "builtin.cut"), "Read-only selection should not match cut");

        var jsonCtx = new SelectionContext { Text = "{\"a\":1}", IsEditable = true };
        var jsonMatches = matcher.MatchPlugins(catalog, jsonCtx);
        Assert(jsonMatches.Any(m => m.Plugin.Id == "builtin.cut"), "Editable selection should match cut");
        Assert(jsonMatches.Any(m => m.Plugin.Id == "builtin.json-pretty"), "JSON selection should match json-pretty");

        await Task.CompletedTask;
    });

    await RunTest("ContextMatcher respects AllowedApps, BlockedApps and Regex capture groups", async () =>
    {
        var matcher = new PluginContextMatcher();
        var plugin = new PluginManifest
        {
            Id = "test.ticket",
            Name = "Open Ticket",
            Kind = PluginKind.Url,
            Regex = @"PROJ-(\d+)",
            AllowedApps = new() { "Code", "notepad" },
            Action = new PluginActionConfig { UrlTemplate = "https://tracker.local/issue/{group.1}" },
            Enabled = true
        };

        var matchAllowed = matcher.MatchPlugins(new[] { plugin }, new SelectionContext
        {
            Text = "Fix PROJ-4092 asap",
            ProcessName = "Code"
        });
        Assert(matchAllowed.Count == 1, "Should match allowed app + regex");
        Assert(matchAllowed[0].RegexGroups is { Count: > 1 } && matchAllowed[0].RegexGroups![1] == "4092", "Should extract capture group 1");

        var matchBlocked = matcher.MatchPlugins(new[] { plugin }, new SelectionContext
        {
            Text = "Fix PROJ-4092 asap",
            ProcessName = "chrome"
        });
        Assert(matchBlocked.Count == 0, "Should not match disallowed app");

        await Task.CompletedTask;
    });

    await RunTest("PluginRegistry persists enable/disable, reorder, custom icon, display mode and options", async () =>
    {
        var settingsPath = Path.Combine(tempDir, "settings.json");
        var extPluginsDir = Path.Combine(tempDir, "ext_plugins");
        Directory.CreateDirectory(extPluginsDir);

        File.WriteAllText(Path.Combine(extPluginsDir, "demo.json"), JsonSerializer.Serialize(new
        {
            id = "ext.demo",
            name = "External Demo",
            icon = "★",
            kind = "Url",
            requirements = new[] { "text" },
            after = "None",
            action = new { urlTemplate = "https://example.com/?q={encodedText}" }
        }));

        var settingsService = new SettingsService(settingsPath);
        settingsService.Settings.DisplayMode = ToolbarDisplayMode.IconOnly;
        settingsService.Save();

        var providers = new IPluginProvider[]
        {
            new BuiltinPluginsCatalog(),
            new DirectoryJsonPluginProvider(extPluginsDir)
        };

        var registry = new PluginRegistry(settingsService, providers);
        Assert(registry.GetById("ext.demo") is not null, "External JSON plugin should be loaded");

        registry.SetEnabled("builtin.copy", false);
        registry.SetCustomIcon("builtin.web-search", "circle S");

        var custom = registry.AddOrUpdateCustomPlugin(new PluginManifest
        {
            Id = "custom.upper",
            Name = "Custom Upper",
            Kind = PluginKind.TextTransform,
            After = AfterActionType.CopyResult,
            Action = new PluginActionConfig { TransformOperation = "uppercase" }
        });
        Assert(registry.GetById("custom.upper") is not null, "Custom plugin should exist");

        var oldOrder = registry.GetById("custom.upper")!.Order;
        registry.MovePlugin("custom.upper", -1);
        Assert(registry.GetById("custom.upper")!.Order == oldOrder - 1, "Plugin order should move up");

        registry.MovePluginToIndex("custom.upper", 0);
        Assert(registry.GetById("custom.upper")!.Order == 0, "MovePluginToIndex(0) should move plugin to top");
        registry.MovePluginToIndex("custom.upper", 2);
        Assert(registry.GetById("custom.upper")!.Order == 2, "MovePluginToIndex(2) should move plugin to index 2");

        registry.SaveOptionValues("builtin.web-search", new Dictionary<string, string>
        {
            ["searchBaseUrl"] = "https://www.google.com/search?q="
        });

        // Fresh reload from disk
        var reloadedSettings = new SettingsService(settingsPath);
        Assert(reloadedSettings.Settings.DisplayMode == ToolbarDisplayMode.IconOnly, "DisplayMode should persist");
        var reloadedRegistry = new PluginRegistry(reloadedSettings, providers);
        Assert(reloadedRegistry.GetById("builtin.copy")!.Enabled == false, "Disabled state should persist");
        Assert(reloadedRegistry.GetById("builtin.web-search")!.Icon == "circle S", "Custom icon override should persist");
        var opts = reloadedRegistry.GetResolvedOptions(reloadedRegistry.GetById("builtin.web-search")!);
        Assert(opts["searchBaseUrl"] == "https://www.google.com/search?q=", "Saved option should persist");

        Assert(reloadedRegistry.RemoveCustomPlugin("custom.upper"), "Custom plugin should be removable");
        Assert(reloadedRegistry.GetById("custom.upper") is null, "Deleted custom plugin should be gone");

        await Task.CompletedTask;
    });

    await RunTest("PopClipSnippetParser parses YAML URL snippets and JS // #popclip snippets with options", async () =>
    {
        var yamlSnippet = """
            #popclip
            name: DuckDuckGo
            icon: search
            requirements: [text, singleline]
            url: https://duckduckgo.com/?q=***
            """;
        Assert(PopClipSnippetParser.LooksLikePopClipSnippet(yamlSnippet), "Should detect #popclip header");
        Assert(PopClipSnippetParser.TryParseSnippet(yamlSnippet, out var urlManifest, out var err1) && urlManifest is not null,
            $"YAML snippet should parse: {err1}");
        Assert(urlManifest!.Kind == PluginKind.Url, "Expected Url kind");
        Assert(urlManifest.Icon == "symbol:search", $"Expected search icon mapped to symbol:search, got {urlManifest.Icon}");
        Assert(urlManifest.Action.UrlTemplate == "https://duckduckgo.com/?q={encodedText}", "Should convert *** to {encodedText}");

        var jsSnippet = """
            // #popclip
            // name: Wrap Prefix
            // icon: square filled WP
            // after: paste-result
            // options:
            //   - identifier: prefix
            //     label: Prefix
            //     type: string
            //     default value: "[TAG] "
            return (popclip.options.prefix || "") + popclip.input.text.toUpperCase();
            """;
        Assert(PopClipSnippetParser.TryParseSnippet(jsSnippet, out var jsManifest, out var err2) && jsManifest is not null,
            $"JS snippet should parse: {err2}");
        Assert(jsManifest!.Kind == PluginKind.JavaScript, "Expected JavaScript kind");
        Assert(jsManifest.Icon == "square filled WP", "Should preserve PopClip badge spec");
        Assert(jsManifest.After == AfterActionType.PasteResult, "Should map paste-result");
        Assert(jsManifest.Options.Count == 1 && jsManifest.Options[0].Key == "prefix" && jsManifest.Options[0].DefaultValue == "[TAG] ",
            "Should parse PopClip options block");

        await Task.CompletedTask;
    });

    await RunTest("JavaScriptExecutor runs PopClip JS snippet end-to-end with popclip.input & options", async () =>
    {
        var jsSnippet = """
            // #popclip
            // name: Title Case Demo
            // icon: square filled Aa
            // after: paste-result
            // options:
            //   - identifier: suffix
            //     label: Suffix
            //     default value: "!"
            return popclip.input.text.trim().toUpperCase() + popclip.options.suffix;
            """;
        Assert(PopClipSnippetParser.TryParseSnippet(jsSnippet, out var manifest, out _) && manifest is not null, "Snippet should parse");

        var host = new FakeHostEnvironment();
        var engine = new PluginExecutionEngine(new IPluginExecutor[] { new JavaScriptExecutor() });
        var result = await engine.ExecuteAsync(new PluginExecutionRequest
        {
            Plugin = manifest!,
            Context = new SelectionContext { Text = "hello popclip", IsEditable = true },
            Options = new Dictionary<string, string> { ["suffix"] = "!!!" }
        }, host);

        Assert(result.Success, $"JS execution failed: {result.ErrorMessage}");
        Assert(host.LastPastedText == "HELLO POPCLIP!!!", $"Expected 'HELLO POPCLIP!!!', got '{host.LastPastedText}'");
    });

    await RunTest("PluginExecutionEngine executes Builtin, Url, TextTransform and Paste/Copy/Show after-actions", async () =>
    {
        var host = new FakeHostEnvironment();
        var engine = new PluginExecutionEngine(new IPluginExecutor[]
        {
            new BuiltinExecutor(),
            new UrlTemplateExecutor(),
            new TextTransformExecutor(),
            new ShellCommandExecutor()
        });

        var searchPlugin = new BuiltinPluginsCatalog().LoadPlugins().First(p => p.Id == "builtin.web-search");
        var urlRes = await engine.ExecuteAsync(new PluginExecutionRequest
        {
            Plugin = searchPlugin,
            Context = new SelectionContext { Text = "WinPopClip 插件" },
            Options = new Dictionary<string, string> { ["searchBaseUrl"] = "https://www.bing.com/search?q=" }
        }, host);
        Assert(urlRes.Success && host.OpenedUrls.Count == 1 && host.OpenedUrls[0].StartsWith("https://www.bing.com/search?q="),
            "Should open encoded URL");

        var upperPlugin = new BuiltinPluginsCatalog().LoadPlugins().First(p => p.Id == "builtin.uppercase");
        var pasteRes = await engine.ExecuteAsync(new PluginExecutionRequest
        {
            Plugin = upperPlugin,
            Context = new SelectionContext { Text = "hello", IsEditable = true },
            Options = new Dictionary<string, string>()
        }, host);
        Assert(pasteRes.Success && host.LastPastedText == "HELLO", "Editable PasteResult should paste HELLO");

        host.LastCopiedText = null;
        var readonlyRes = await engine.ExecuteAsync(new PluginExecutionRequest
        {
            Plugin = upperPlugin,
            Context = new SelectionContext { Text = "world", IsEditable = false },
            Options = new Dictionary<string, string>()
        }, host);
        Assert(readonlyRes.Success && host.LastCopiedText == "WORLD", "Read-only PasteResult should fall back to copy");

        var wcPlugin = new BuiltinPluginsCatalog().LoadPlugins().First(p => p.Id == "builtin.word-count");
        var wcRes = await engine.ExecuteAsync(new PluginExecutionRequest
        {
            Plugin = wcPlugin,
            Context = new SelectionContext { Text = "one two three" },
            Options = new Dictionary<string, string>()
        }, host);
        Assert(wcRes.Success && host.LastToast is not null && host.LastToast.Contains("3 词"), "Word count should show toast with 3 words");
    });

    await RunTest("HttpWebhookExecutor JSON path extractor works for nested arrays & objects", async () =>
    {
        var sample = """{"choices":[{"message":{"content":"你好世界"}}]}""";
        var extracted = HttpWebhookExecutor.ExtractJsonPath(sample, "choices.0.message.content");
        Assert(extracted == "你好世界", $"Expected 你好世界, got {extracted}");
        await Task.CompletedTask;
    });

    await RunTest("ThemeResolver, ToolbarMaterial and ToolbarCornerRadius clamp & persist cleanly", async () =>
    {
        Assert(ThemeResolver.ResolveEffectiveTheme(ToolbarTheme.Light, systemPrefersDark: true) == ToolbarTheme.Light, "Light overrides dark system");
        Assert(ThemeResolver.ResolveEffectiveTheme(ToolbarTheme.Dark, systemPrefersDark: false) == ToolbarTheme.Dark, "Dark overrides light system");
        Assert(ThemeResolver.ResolveEffectiveTheme(ToolbarTheme.System, systemPrefersDark: true) == ToolbarTheme.Dark, "System follows dark");
        Assert(ThemeResolver.ResolveEffectiveTheme(ToolbarTheme.System, systemPrefersDark: false) == ToolbarTheme.Light, "System follows light");
        Assert(ThemeResolver.ClampCornerRadius(1) == 4 && ThemeResolver.ClampCornerRadius(60) == 28, "CornerRadius should clamp to 4..28");
        Assert(ThemeResolver.ClampToolbarScale(0.2) == ThemeResolver.MinToolbarScale && ThemeResolver.ClampToolbarScale(3.0) == ThemeResolver.MaxToolbarScale, "ToolbarScale should clamp to 0.75..1.50");
        Assert(ThemeResolver.ClampToolbarScale(0) == 1.0 && ThemeResolver.ClampToolbarScale(double.NaN) == 1.0, "Invalid ToolbarScale should fall back to 1.0");
        Assert(ThemeResolver.ClampCustomWidth(50) == ThemeResolver.MinCustomWidth && ThemeResolver.ClampCustomWidth(3000) == ThemeResolver.MaxCustomWidth, "CustomWidth should clamp");
        Assert(ThemeResolver.ClampCustomHeight(10) == ThemeResolver.MinCustomHeight && ThemeResolver.ClampCustomHeight(500) == ThemeResolver.MaxCustomHeight, "CustomHeight should clamp");

        var themeSettingsPath = Path.Combine(tempDir, "theme_settings.json");
        var svc = new SettingsService(themeSettingsPath);
        Assert(svc.Settings.EnableClipboardFallback == false, "EnableClipboardFallback should default to false so selecting text never copies");
        Assert(svc.Settings.Theme == ToolbarTheme.System, "Default theme should be System");
        Assert(svc.Settings.ToolbarMaterial == ToolbarMaterial.Acrylic, "Default material should be Acrylic");
        Assert(svc.Settings.ToolbarCornerRadius == 14, "Default corner radius should be 14");
        Assert(Math.Abs(svc.Settings.ToolbarScale - 1.0) < 0.001, "Default toolbar scale should be 1.0");
        Assert(svc.Settings.ToolbarSizePreset == ToolbarSizePreset.Auto, "Default size preset should be Auto");

        // Verify legacy v1 settings with EnableClipboardFallback=true migrate cleanly to false, while explicit opt-in persists
        var legacySettingsPath = Path.Combine(tempDir, "legacy_v1_settings.json");
        File.WriteAllText(legacySettingsPath, """{"EnableClipboardFallback":true,"Theme":"Dark"}""");
        var migratedSvc = new SettingsService(legacySettingsPath);
        Assert(migratedSvc.Settings.EnableClipboardFallback == false, "Legacy v1 settings should migrate EnableClipboardFallback to false");
        Assert(migratedSvc.Settings.SettingsSchemaVersion == AppSettings.CurrentSchemaVersion, "Schema version should be upgraded");
        migratedSvc.Settings.EnableClipboardFallback = true;
        migratedSvc.Save();
        var explicitOptInSvc = new SettingsService(legacySettingsPath);
        Assert(explicitOptInSvc.Settings.EnableClipboardFallback == true, "Explicit v2 user opt-in should persist");

        foreach (var mode in new[] { ToolbarTheme.Dark, ToolbarTheme.Light, ToolbarTheme.System })
        {
            svc.Settings.Theme = mode;
            svc.Save();
            var reloaded = new SettingsService(themeSettingsPath);
            Assert(reloaded.Settings.Theme == mode, $"Theme {mode} should persist");
        }

        foreach (var mat in new[] { ToolbarMaterial.Solid, ToolbarMaterial.LiquidGlass, ToolbarMaterial.Acrylic })
        {
            svc.Settings.ToolbarMaterial = mat;
            svc.Settings.ToolbarCornerRadius = 22;
            svc.Settings.ToolbarScale = 1.25;
            svc.Settings.ToolbarSizePreset = ToolbarSizePreset.Custom;
            svc.Settings.ToolbarCustomWidth = 520;
            svc.Settings.ToolbarCustomHeight = 84;
            svc.Save();
            var reloaded = new SettingsService(themeSettingsPath);
            Assert(reloaded.Settings.ToolbarMaterial == mat, $"Material {mat} should persist");
            Assert(reloaded.Settings.ToolbarCornerRadius == 22, "CornerRadius should persist");
            Assert(Math.Abs(reloaded.Settings.ToolbarScale - 1.25) < 0.001, "ToolbarScale should persist");
            Assert(reloaded.Settings.ToolbarSizePreset == ToolbarSizePreset.Custom, "SizePreset should persist");
            Assert(reloaded.Settings.ToolbarCustomWidth == 520 && reloaded.Settings.ToolbarCustomHeight == 84, "Custom WxH should persist");
        }

        await Task.CompletedTask;
    });

    await RunTest("DirectoryJsonPluginProvider loads WinPopClip JSON AND PopClip .js/.popcliptxt samples", async () =>
    {
        var repoPluginsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "plugins"));
        Assert(Directory.Exists(repoPluginsDir), $"Plugins directory not found at {repoPluginsDir}");
        var provider = new DirectoryJsonPluginProvider(repoPluginsDir);
        var loaded = provider.LoadPlugins().ToList();
        Assert(loaded.Count >= 5, $"Expected >= 5 sample plugins, got {loaded.Count}");
        Assert(loaded.Any(p => p.Id == "sample.bing-translate"), "bing-translate JSON should load");
        Assert(loaded.Any(p => p.Id == "popclip.sample.titlecase" && p.Kind == PluginKind.JavaScript), "PopClip JS sample should load");
        Assert(loaded.Any(p => p.Id == "popclip.sample.wikipedia" && p.Kind == PluginKind.Url), "PopClip YAML snippet sample should load");
        await Task.CompletedTask;
    });
}
finally
{
    try { Directory.Delete(tempDir, recursive: true); } catch { }
}

Console.WriteLine($"\nSummary: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

file sealed class FakeHostEnvironment : IPluginHostEnvironment
{
    public string? LastCopiedText { get; set; }
    public string? LastPastedText { get; set; }
    public string? LastToast { get; set; }
    public List<string> OpenedUrls { get; } = new();

    public Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        LastCopiedText = text;
        return Task.CompletedTask;
    }

    public Task<string?> ReadClipboardTextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(LastCopiedText);

    public Task SendCopyShortcutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SendCutShortcutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PasteTextAsync(string text, bool restorePreviousClipboard = true, CancellationToken cancellationToken = default)
    {
        LastPastedText = text;
        return Task.CompletedTask;
    }

    public Task SendPasteShortcutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task OpenUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        OpenedUrls.Add(url);
        return Task.CompletedTask;
    }

    public void ShowToast(string message, ScreenPoint? anchor = null)
    {
        LastToast = message;
    }
}
