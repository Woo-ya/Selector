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

    await RunTest("Dialog double-click empty-input summon matches Paste safely and guards empty/password/read-only cases", async () =>
    {
        var matcher = new PluginContextMatcher();
        var catalog = new BuiltinPluginsCatalog().LoadPlugins().ToList();

        // 1. Empty editable dialog + clipboard has text -> matches ONLY builtin.paste
        var emptyWithClip = new SelectionContext
        {
            Text = string.Empty,
            IsEditable = true,
            IsEmptyInputSummon = true,
            HasClipboardText = true
        };
        var matches = matcher.MatchPlugins(catalog, emptyWithClip);
        Assert(matches.Count == 1 && matches[0].Plugin.Id == "builtin.paste",
            $"Expected only builtin.paste on empty editable summon with clipboard, got [{string.Join(", ", matches.Select(m => m.Plugin.Id))}]");

        // 2. Empty editable dialog + empty clipboard -> 0 matches (handled cleanly by coordinator toast)
        var emptyNoClip = new SelectionContext
        {
            Text = string.Empty,
            IsEditable = true,
            IsEmptyInputSummon = true,
            HasClipboardText = false
        };
        Assert(matcher.MatchPlugins(catalog, emptyNoClip).Count == 0, "Empty clipboard should yield 0 matches on empty dialog summon");

        // 3. Non-editable area or password field -> 0 matches
        Assert(matcher.MatchPlugins(catalog, new SelectionContext { Text = "", IsEditable = false, IsEmptyInputSummon = true, HasClipboardText = true }).Count == 0,
            "Read-only area must not match on empty summon");
        Assert(matcher.MatchPlugins(catalog, new SelectionContext { Text = "", IsEditable = true, IsPasswordField = true, IsEmptyInputSummon = true, HasClipboardText = true }).Count == 0,
            "Password field must never match");

        // 4. Normal non-empty selection in editable box still matches copy, cut, paste
        var normalEditable = matcher.MatchPlugins(catalog, new SelectionContext { Text = "hello", IsEditable = true });
        Assert(normalEditable.Any(m => m.Plugin.Id == "builtin.copy") &&
               normalEditable.Any(m => m.Plugin.Id == "builtin.cut") &&
               normalEditable.Any(m => m.Plugin.Id == "builtin.paste"),
            "Normal editable selection must remain compatible with copy/cut/paste");

        await Task.CompletedTask;
    });

    await RunTest("PluginPackageInstaller installs .popclipextz (YAML+JS and XML Plist), handles duplicates & invalid files", async () =>
    {
        var installRoot = Path.Combine(tempDir, "pkg_install_test");
        var userPlugins = Path.Combine(installRoot, "user_plugins");
        var pkgSettings = Path.Combine(installRoot, "settings.json");
        Directory.CreateDirectory(installRoot);

        // Create a real .popclipextz archive (Config.yaml + script.js)
        var extzYaml = Path.Combine(installRoot, "Shout.popclipextz");
        using (var zip = System.IO.Compression.ZipFile.Open(extzYaml, System.IO.Compression.ZipArchiveMode.Create))
        {
            var cfg = zip.CreateEntry("Shout.popclipext/Config.yaml");
            using (var w = new StreamWriter(cfg.Open()))
            {
                w.WriteLine("name: Shout Demo");
                w.WriteLine("identifier: com.test.shout");
                w.WriteLine("icon: scale=120 circle filled !");
                w.WriteLine("javascript file: shout.js");
                w.WriteLine("after: copy-result");
            }
            var js = zip.CreateEntry("Shout.popclipext/shout.js");
            using (var w = new StreamWriter(js.Open()))
            {
                w.Write("return popclip.input.text.toUpperCase() + '!';");
            }
        }

        // Create a classic Apple XML Config.plist .popclipextz archive
        var extzPlist = Path.Combine(installRoot, "SearchDocs.popclipextz");
        using (var zip = System.IO.Compression.ZipFile.Open(extzPlist, System.IO.Compression.ZipArchiveMode.Create))
        {
            var plist = zip.CreateEntry("SearchDocs.popclipext/Config.plist");
            using (var w = new StreamWriter(plist.Open()))
            {
                w.Write("""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <plist version="1.0"><dict>
                      <key>Extension Name</key><string>Docs Search</string>
                      <key>Extension Identifier</key><string>com.test.docs</string>
                      <key>Actions</key><array><dict>
                        <key>Title</key><string>Docs Search</string>
                        <key>URL</key><string>https://learn.microsoft.com/search/?terms={popclip text}</string>
                      </dict></array>
                    </dict></plist>
                    """);
            }
        }

        // Create an unsupported file to verify graceful error handling
        var badFile = Path.Combine(installRoot, "random.bin");
        File.WriteAllText(badFile, "not a plugin");

        var settingsSvc = new SettingsService(pkgSettings);
        Assert(settingsSvc.Settings.EnableDialogDoubleClickSummon == true, "Dialog double-click summon should default to true");
        var registry = new PluginRegistry(settingsSvc, new IPluginProvider[]
        {
            new BuiltinPluginsCatalog(),
            new DirectoryJsonPluginProvider(userPlugins)
        });

        var batch = PluginPackageInstaller.InstallFromPaths(new[] { extzYaml, extzPlist, badFile }, userPlugins, registry);
        Assert(batch.SucceededCount == 2 && batch.FailedCount == 1, $"Expected 2 succeeded, 1 failed; got {batch.SucceededCount}/{batch.FailedCount}");

        var shout = registry.GetById("com.test.shout");
        Assert(shout is not null && shout.Enabled && shout.Kind == PluginKind.JavaScript && shout.Icon == "circle filled !",
            "YAML+JS .popclipextz should install, normalize icon, and auto-enable");

        var docs = registry.GetById("com.test.docs");
        Assert(docs is not null && docs.Enabled && docs.Kind == PluginKind.Url && docs.Action.UrlTemplate == "https://learn.microsoft.com/search/?terms={encodedText}",
            "XML Plist .popclipextz should install and convert {popclip text}");

        // Duplicate drop should cleanly overwrite without duplicating entries
        var dupBatch = PluginPackageInstaller.InstallFromPaths(new[] { extzYaml }, userPlugins, registry);
        Assert(dupBatch.SucceededCount == 1 && registry.GetAllPlugins().Count(p => p.Id == "com.test.shout") == 1,
            "Duplicate drop should overwrite cleanly without duplicating plugins");

        // Model real Instant-Translate .popclipextz (Config.plist with <key>module</key> + Config.js requiring ./langs.json & axios)
        var extzInstant = Path.Combine(installRoot, "Instant-Translate-08hhdb-397.popclipextz");
        using (var zip = System.IO.Compression.ZipFile.Open(extzInstant, System.IO.Compression.ZipArchiveMode.Create))
        {
            var plist = zip.CreateEntry("InstantTranslate.popclipext/Config.plist");
            using (var w = new StreamWriter(plist.Open()))
            {
                w.Write("""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <plist version="1.0"><dict>
                      <key>name</key><string>Instant Translate</string>
                      <key>identifier</key><string>com.popclip.extension.instant-translate</string>
                      <key>icon</key><string>itrans.png</string>
                      <key>module</key><string>Config.js</string>
                      <key>entitlements</key><array><string>network</string></array>
                      <key>after</key><string>preview-result</string>
                    </dict></plist>
                    """);
            }
            var langs = zip.CreateEntry("InstantTranslate.popclipext/langs.json");
            using (var w = new StreamWriter(langs.Open()))
            {
                w.Write("""{"languages":[{"code":"zh-Hans","name":"Chinese"}]}""");
            }
            var js = zip.CreateEntry("InstantTranslate.popclipext/Config.js");
            using (var w = new StreamWriter(js.Open()))
            {
                w.Write("""
                    const axios = require("axios");
                    const { languages } = require("./langs.json");
                    exports.options = [
                      { identifier: "destlang", label: { "zh-Hans": "翻译为", en: "Destination Language" }, defaultValue: "zh-Hans" },
                      { identifier: "deeplKey", label: "DeepL API Key", type: "secret" }
                    ];
                    exports.action = async (input, options) => {
                      const key = options.deeplKey.trim();
                      const hasAxios = typeof axios.post === "function";
                      return `[${options.destlang}|axios=${hasAxios}|langs=${languages.length}|key=${key}] ${input.text}`;
                    };
                    """);
            }
        }

        var instantBatch = PluginPackageInstaller.InstallFromPaths(new[] { extzInstant }, userPlugins, registry);
        Assert(instantBatch.SucceededCount == 1, $"Instant-Translate .popclipextz install failed: {instantBatch.Items[0].ErrorMessage}");
        var instant = registry.GetById("com.popclip.extension.instant-translate");
        Assert(instant is not null && instant.Enabled && instant.Icon == "symbol:translate" && instant.After == AfterActionType.ShowResult,
            $"Instant-Translate metadata mismatch: icon={instant?.Icon}, after={instant?.After}");
        Assert(instant!.Action.Entitlements.Contains("network"), "Instant-Translate should retain network entitlement");
        Assert(instant.Options.Any(o => o.Key == "destlang" && o.DefaultValue == "zh-Hans"), "Module options should be extracted");

        // End-to-end execution of the installed PopClip JS Module (verifies require("./langs.json"), axios shim, & option defaults)
        var modHost = new FakeHostEnvironment();
        var modEngine = new PluginExecutionEngine(new IPluginExecutor[] { new JavaScriptExecutor() });
        var modRes = await modEngine.ExecuteAsync(new PluginExecutionRequest
        {
            Plugin = instant,
            Context = new SelectionContext { Text = "Hello" },
            Options = registry.GetResolvedOptions(instant)
        }, modHost);
        Assert(modRes.Success, $"Instant-Translate JS module execution failed: {modRes.ErrorMessage}");
        Assert(modHost.LastToast == "[zh-Hans|axios=true|langs=1|key=] Hello", $"Unexpected module output: {modHost.LastToast}");

        await Task.CompletedTask;
    });

    await RunTest("PopClipExtensionCompiler recompiles modern Config.ts + stub Config.plist (.popclipextz) on import", async () =>
    {
        var tsRoot = Path.Combine(tempDir, "ts_recompiler_test");
        var userPlugins = Path.Combine(tsRoot, "user_plugins");
        Directory.CreateDirectory(userPlugins);

        var settingsPath = Path.Combine(tsRoot, "settings.json");
        var settingsService = new SettingsService(settingsPath);
        var registry = new PluginRegistry(settingsService, new IPluginProvider[]
        {
            new BuiltinPluginsCatalog(),
            new DirectoryJsonPluginProvider(userPlugins)
        });

        // Exact layout of official PopClip 4586+ packages like Instant-Translate-08hhdb-397.popclipextz:
        // server-generated stub Config.plist + raw TypeScript ESM Config.ts + sibling langs.json
        var extzTs = Path.Combine(tsRoot, "Instant-Translate-TS-RealLayout.popclipextz");
        using (var zip = System.IO.Compression.ZipFile.Open(extzTs, System.IO.Compression.ZipArchiveMode.Create))
        {
            var stubPlist = zip.CreateEntry("@08hhdb.com.popclip.extension.instant-translate.popclipext/Config.plist");
            using (var w = new StreamWriter(stubPlist.Open()))
            {
                w.Write("""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                    <plist version="1.0">
                    <dict>
                      <key>Comment</key><string>File auto-generated by server</string>
                      <key>Extension Identifier</key><string>com.popclip.extension.instant-translate</string>
                      <key>Extension Name</key><string>Instant Translate</string>
                      <key>Required Software Version</key><integer>4586</integer>
                    </dict>
                    </plist>
                    """);
            }
            var langs = zip.CreateEntry("@08hhdb.com.popclip.extension.instant-translate.popclipext/langs.json");
            using (var w = new StreamWriter(langs.Open()))
            {
                w.Write("""{"languages":[{"code":"en","deepl":"EN-US","name":"English"},{"code":"zh-Hans","deepl":"ZH-HANS","name":"Chinese (Simplified)"}]}""");
            }
            var ts = zip.CreateEntry("@08hhdb.com.popclip.extension.instant-translate.popclipext/Config.ts");
            using (var w = new StreamWriter(ts.Open()))
            {
                w.Write("""
                    // #popclip
                    // name: Instant Translate
                    // identifier: com.popclip.extension.instant-translate
                    // description: Translate the selected text instantly within PopClip.
                    // icon: itrans.png
                    // popclipVersion: 4586
                    // regex: (?s)^.{1,5000}$
                    // entitlements: [network]
                    // after: preview-result

                    import axios from "axios";
                    import { languages as languageList } from "./langs.json";

                    const languages = [...languageList].sort((a, b) => a.name.localeCompare(b.name));

                    export const options = [
                      {
                        identifier: "destlang",
                        label: { en: "Destination Language", "zh-Hans": "翻译为" },
                        type: "multiple",
                        valueLabels: languages.map((l) => l.name),
                        values: languages.map((l) => l.code),
                        defaultValue: "zh-Hans",
                      },
                      {
                        identifier: "deeplKey",
                        label: "DeepL API Key",
                        type: "secret",
                      },
                    ] as const;

                    type Options = InferOptions<typeof options>;
                    interface ErrorResponse { status: number; data?: { message?: string }; }

                    export const action: ActionFunction<Options> = async (input, options) => {
                      const key = options.deeplKey.trim();
                      const hasAxios = typeof axios.post === "function" && typeof axios.isAxiosError === "function";
                      return `[recompiled:${options.destlang}|axios=${hasAxios}|choices=${languages.length}|key=${key}] ${input.text}`;
                    };
                    """);
            }
        }

        var batch = PluginPackageInstaller.InstallFromPaths(new[] { extzTs }, userPlugins, registry);
        Assert(batch.SucceededCount == 1 && batch.Items[0].Recompiled,
            $"TS .popclipextz install/recompile failed: {batch.Items[0].ErrorMessage}");

        var installed = registry.GetById("com.popclip.extension.instant-translate");
        Assert(installed is not null && installed.Enabled && installed.Icon == "symbol:translate",
            $"Installed TS plugin metadata mismatch: id={installed?.Id}, icon={installed?.Icon}");
        Assert(installed!.Options.Count == 2, $"Expected 2 dynamic runtime options, got {installed.Options.Count}");
        var destOpt = installed.Options.First(o => o.Key == "destlang");
        Assert(destOpt.Label == "翻译为" && destOpt.Type == PluginOptionType.Choice && destOpt.Choices.Count == 2 && destOpt.ChoiceLabels.Contains("Chinese (Simplified)"),
            $"Dynamic Choice option mismatch: label={destOpt.Label}, choices={destOpt.Choices.Count}");

        // Verify compiled entry executes cleanly with Node.js
        var host = new FakeHostEnvironment();
        var engine = new PluginExecutionEngine(new IPluginExecutor[] { new JavaScriptExecutor() });
        var execRes = await engine.ExecuteAsync(new PluginExecutionRequest
        {
            Plugin = installed,
            Context = new SelectionContext { Text = "Hello TS" },
            Options = registry.GetResolvedOptions(installed)
        }, host);
        Assert(execRes.Success, $"Recompiled TS module execution failed: {execRes.ErrorMessage}");
        Assert(host.LastToast == "[recompiled:zh-Hans|axios=true|choices=2|key=] Hello TS",
            $"Unexpected recompiled TS output: {host.LastToast}");

        // Verify uninstalling installed external plugin deletes folder & updates registry
        var removed = registry.RemoveInstalledExternalPlugin(installed.Id, userPlugins);
        Assert(removed && registry.GetById(installed.Id) is null, "External plugin uninstall should remove plugin");
    });

    await RunTest("PopClip Select All extension (pressKey + util.constant.MODIFIER_COMMAND + setTimeout popclip.appear) runs end-to-end", async () =>
    {
        var extDir = Path.Combine(tempDir, "select-all.popclipext");
        Directory.CreateDirectory(extDir);
        File.WriteAllText(Path.Combine(extDir, "Config.json"), """
        {
          "popclipVersion": 3785,
          "identifier": "com.pilotmoon.popclip.extension.select-all",
          "name": "Select All",
          "icon": "select-all.svg",
          "requirements": [],
          "javascript": "popclip.pressKey('A', util.constant.MODIFIER_COMMAND); setTimeout(() => popclip.appear(), 250)",
          "description": "Select all the text in the field, then invoke PopClip again."
        }
        """);

        Assert(PopClipSnippetParser.TryParsePopClipExtFolder(extDir, out var manifest, out var err) && manifest is not null,
            $"Select All Config.json should parse cleanly: {err}");
        Assert(manifest!.Kind == PluginKind.JavaScript && manifest.Requirements.Count == 0,
            $"Expected JS plugin with explicit empty requirements, got kind={manifest.Kind}, reqs={manifest.Requirements.Count}");

        var host = new FakeHostEnvironment();
        var engine = new PluginExecutionEngine(new IPluginExecutor[] { new JavaScriptExecutor() });
        var execRes = await engine.ExecuteAsync(new PluginExecutionRequest
        {
            Plugin = manifest,
            Context = new SelectionContext { Text = "hello", IsEditable = true, AnchorPoint = new ScreenPoint(100, 200) },
            Options = new Dictionary<string, string>()
        }, host);

        Assert(execRes.Success, $"Select All execution failed: {execRes.ErrorMessage}");
        Assert(host.SentKeyCombos.Count == 1 && host.SentKeyCombos[0].Key == "A" && host.SentKeyCombos[0].Modifiers.Contains("command"),
            $"Expected Command+A key combo, got count={host.SentKeyCombos.Count}");
        Assert(host.ReappearCount == 1, $"Expected popclip.appear() to trigger ReappearToolbarAsync once, got {host.ReappearCount}");
    });

    await RunTest("Doubao-style DeferredCapture matches generic text actions cleanly and copies via single Ctrl+C", async () =>
    {
        var matcher = new PluginContextMatcher();
        var catalog = new BuiltinPluginsCatalog().LoadPlugins().ToList();

        // Read-only deferred selection (e.g. WeChat chat bubble): matches Copy, Search, WordCount — skips URL/Cut/Paste
        var deferredReadOnly = new SelectionContext
        {
            Text = string.Empty,
            ProcessName = "Weixin",
            IsEditable = false,
            IsDeferredCapture = true,
            Source = SelectionCaptureSource.DeferredCapture
        };
        var roMatches = matcher.MatchPlugins(catalog, deferredReadOnly);
        Assert(roMatches.Any(m => m.Plugin.Id == "builtin.copy"), "Deferred read-only should match builtin.copy");
        Assert(roMatches.Any(m => m.Plugin.Id == "builtin.web-search"), "Deferred read-only should match web-search");
        Assert(!roMatches.Any(m => m.Plugin.Id == "builtin.open-url"), "Deferred read-only must skip URL-only plugin until materialized");
        Assert(!roMatches.Any(m => m.Plugin.Id == "builtin.cut"), "Deferred read-only must skip cut");

        // Editable deferred selection (e.g. WeChat input box): also matches cut & paste
        var deferredEditable = new SelectionContext
        {
            Text = string.Empty,
            ProcessName = "Weixin",
            IsEditable = true,
            IsDeferredCapture = true,
            Source = SelectionCaptureSource.DeferredCapture
        };
        var edMatches = matcher.MatchPlugins(catalog, deferredEditable);
        Assert(edMatches.Any(m => m.Plugin.Id == "builtin.cut") && edMatches.Any(m => m.Plugin.Id == "builtin.paste"),
            "Deferred editable should match cut & paste");

        // Builtin copy on deferred context sends a single Ctrl+C shortcut without writing empty string to clipboard
        var host = new FakeHostEnvironment();
        var engine = new PluginExecutionEngine(new IPluginExecutor[] { new BuiltinExecutor() });
        var copyPlugin = catalog.First(p => p.Id == "builtin.copy");
        var res = await engine.ExecuteAsync(new PluginExecutionRequest
        {
            Plugin = copyPlugin,
            Context = deferredReadOnly,
            Options = new Dictionary<string, string>()
        }, host);
        Assert(res.Success && host.CopyShortcutCount == 1 && host.LastCopiedText is null,
            $"Deferred copy should fire single SendCopyShortcutAsync (count={host.CopyShortcutCount}, copied={host.LastCopiedText})");
    });

    await RunTest("ToolbarPopupSpeed presets, custom duration clamping, and SettingsService persistence work cleanly", async () =>
    {
        Assert(ThemeResolver.ResolvePopupDurationMs(ToolbarPopupSpeed.Instant, 150) == 0, "Instant should resolve to 0ms");
        Assert(ThemeResolver.ResolvePopupDurationMs(ToolbarPopupSpeed.Fast, 250) == 110, "Fast should resolve to 110ms");
        Assert(ThemeResolver.ResolvePopupDurationMs(ToolbarPopupSpeed.Standard, 50) == 190, "Standard should resolve to 190ms");
        Assert(ThemeResolver.ResolvePopupDurationMs(ToolbarPopupSpeed.Slow, 50) == 300, "Slow should resolve to 300ms");
        Assert(ThemeResolver.ResolvePopupDurationMs(ToolbarPopupSpeed.Custom, -20) == 0, "Custom below min should clamp to 0ms");
        Assert(ThemeResolver.ResolvePopupDurationMs(ToolbarPopupSpeed.Custom, 999) == 360, "Custom above max should clamp to 360ms");
        Assert(ThemeResolver.ResolvePopupDurationMs(ToolbarPopupSpeed.Custom, 140) == 140, "Custom valid duration should be preserved");

        Assert(ThemeResolver.ResolvePopupSpeedPreset(0) == ToolbarPopupSpeed.Instant, "0ms should map to Instant");
        Assert(ThemeResolver.ResolvePopupSpeedPreset(110) == ToolbarPopupSpeed.Fast, "110ms should map to Fast");
        Assert(ThemeResolver.ResolvePopupSpeedPreset(190) == ToolbarPopupSpeed.Standard, "190ms should map to Standard");
        Assert(ThemeResolver.ResolvePopupSpeedPreset(300) == ToolbarPopupSpeed.Slow, "300ms should map to Slow");
        Assert(ThemeResolver.ResolvePopupSpeedPreset(140) == ToolbarPopupSpeed.Custom, "140ms should map to Custom");

        var speedSettingsPath = Path.Combine(tempDir, "popup_speed_settings.json");
        var svc = new SettingsService(speedSettingsPath);
        Assert(svc.Settings.ToolbarPopupSpeed == ToolbarPopupSpeed.Fast && svc.Settings.ToolbarPopupDurationMs == 110,
            "Default popup speed should be Fast (110ms)");

        svc.Settings.ToolbarPopupSpeed = ToolbarPopupSpeed.Custom;
        svc.Settings.ToolbarPopupDurationMs = 150;
        svc.Save();

        var reloaded = new SettingsService(speedSettingsPath);
        Assert(reloaded.Settings.ToolbarPopupSpeed == ToolbarPopupSpeed.Custom && reloaded.Settings.ToolbarPopupDurationMs == 150,
            $"Persisted custom popup speed mismatch: {reloaded.Settings.ToolbarPopupSpeed}/{reloaded.Settings.ToolbarPopupDurationMs}");

        await Task.CompletedTask;
    });

    await RunTest("PopClip bundle image/SVG icons (Google/Baidu/SelectAll) resolve cleanly and support custom icon override & reset", async () =>
    {
        var iconTestRoot = Path.Combine(tempDir, "popclip_icon_test");
        var userPlugins = Path.Combine(iconTestRoot, "plugins");
        Directory.CreateDirectory(userPlugins);

        // 1. Model real Google & Baidu Config.plist bundles with g.png / Baidu.png
        var googleDir = Path.Combine(userPlugins, "google.popclipext");
        Directory.CreateDirectory(googleDir);
        File.WriteAllBytes(Path.Combine(googleDir, "g.png"), new byte[] { 137, 80, 78, 71 });
        File.WriteAllText(Path.Combine(googleDir, "Config.plist"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
              <key>Extension Identifier</key><string>com.test.google-search</string>
              <key>Extension Name</key><string>Google</string>
              <key>Actions</key><array><dict>
                <key>Image File</key><string>g.png</string>
                <key>Title</key><string>Google</string>
                <key>URL</key><string>https://www.google.com/search?q={popclip text}</string>
              </dict></array>
            </dict></plist>
            """);

        // 2. Model Select All Config.json with select-all.svg
        var saDir = Path.Combine(userPlugins, "select-all.popclipext");
        Directory.CreateDirectory(saDir);
        File.WriteAllText(Path.Combine(saDir, "select-all.svg"), "<svg viewBox=\"0 0 24 24\"><rect x=\"2\" y=\"2\" width=\"20\" height=\"20\"/></svg>");
        File.WriteAllText(Path.Combine(saDir, "Config.json"), """
            {
              "identifier": "com.test.select-all",
              "name": "Select All",
              "icon": "select-all.svg",
              "javascript": "popclip.pressKey('A', util.constant.MODIFIER_COMMAND)"
            }
            """);

        var settingsPath = Path.Combine(iconTestRoot, "settings.json");
        var svc = new SettingsService(settingsPath);
        var registry = new PluginRegistry(svc, new IPluginProvider[]
        {
            new BuiltinPluginsCatalog(),
            new DirectoryJsonPluginProvider(userPlugins)
        });

        var google = registry.GetById("com.test.google-search");
        var sa = registry.GetById("com.test.select-all");
        var expectedGoogleIcon = $"file:{Path.Combine(googleDir, "g.png")}";
        var expectedSaIcon = $"file:{Path.Combine(saDir, "select-all.svg")}";

        Assert(google is not null && google.Icon.Equals(expectedGoogleIcon, StringComparison.OrdinalIgnoreCase),
            $"Google should resolve bundled g.png (actual={google?.Icon})");
        Assert(sa is not null && sa.Icon.Equals(expectedSaIcon, StringComparison.OrdinalIgnoreCase),
            $"Select All should resolve bundled select-all.svg (actual={sa?.Icon})");

        // Custom icon override + ResetCustomIcon restores original bundled icon
        registry.SetCustomIcon("com.test.google-search", "file-color:C:\\custom\\logo.png");
        Assert(registry.GetById("com.test.google-search")?.Icon == "file-color:C:\\custom\\logo.png",
            "SetCustomIcon should persist file-color override");

        registry.ResetCustomIcon("com.test.google-search");
        Assert(registry.GetById("com.test.google-search")!.Icon.Equals(expectedGoogleIcon, StringComparison.OrdinalIgnoreCase),
            $"ResetCustomIcon should restore bundled g.png (actual={registry.GetById("com.test.google-search")?.Icon})");

        await Task.CompletedTask;
    });

    await RunTest("Per-plugin DisplayMode (IconOnly / TextOnly / IconAndText / FollowGlobal) persists cleanly", async () =>
    {
        var dmPath = Path.Combine(tempDir, "per_plugin_display_mode.json");
        var svc = new SettingsService(dmPath);
        var registry = new PluginRegistry(svc, new IPluginProvider[] { new BuiltinPluginsCatalog() });

        Assert(registry.GetById("builtin.copy")?.DisplayMode is null, "Default per-plugin display mode should be null (FollowGlobal)");

        registry.SetDisplayMode("builtin.copy", ToolbarDisplayMode.IconOnly);
        registry.SetDisplayMode("builtin.web-search", ToolbarDisplayMode.TextOnly);
        registry.SetDisplayMode("builtin.word-count", ToolbarDisplayMode.IconAndText);

        var reloadedSvc = new SettingsService(dmPath);
        var reloadedReg = new PluginRegistry(reloadedSvc, new IPluginProvider[] { new BuiltinPluginsCatalog() });

        Assert(reloadedReg.GetById("builtin.copy")?.DisplayMode == ToolbarDisplayMode.IconOnly, "IconOnly override should persist");
        Assert(reloadedReg.GetById("builtin.web-search")?.DisplayMode == ToolbarDisplayMode.TextOnly, "TextOnly override should persist");
        Assert(reloadedReg.GetById("builtin.word-count")?.DisplayMode == ToolbarDisplayMode.IconAndText, "IconAndText override should persist");

        reloadedReg.SetDisplayMode("builtin.web-search", null);
        Assert(reloadedReg.GetById("builtin.web-search")?.DisplayMode is null, "Resetting to FollowGlobal should clear override");

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
    public List<(string Key, IReadOnlyList<string> Modifiers)> SentKeyCombos { get; } = new();
    public int ReappearCount { get; set; }
    public int CopyShortcutCount { get; set; }

    public Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        LastCopiedText = text;
        return Task.CompletedTask;
    }

    public Task<string?> ReadClipboardTextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(LastCopiedText);

    public Task SendCopyShortcutAsync(CancellationToken cancellationToken = default)
    {
        CopyShortcutCount++;
        return Task.CompletedTask;
    }
    public Task SendCutShortcutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PasteTextAsync(string text, bool restorePreviousClipboard = true, CancellationToken cancellationToken = default)
    {
        LastPastedText = text;
        return Task.CompletedTask;
    }

    public Task SendPasteShortcutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SendKeyComboAsync(string key, IReadOnlyList<string> modifiers, CancellationToken cancellationToken = default)
    {
        SentKeyCombos.Add((key, modifiers));
        return Task.CompletedTask;
    }

    public Task ReappearToolbarAsync(ScreenPoint? anchor = null, CancellationToken cancellationToken = default)
    {
        ReappearCount++;
        return Task.CompletedTask;
    }

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
