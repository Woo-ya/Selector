using System.Text.RegularExpressions;
using WinPopClip.Core.Models;

namespace WinPopClip.Core.Plugins;

public sealed record MatchedPlugin(
    PluginManifest Plugin,
    string? RegexMatchValue = null,
    IReadOnlyList<string>? RegexGroups = null);

/// <summary>
/// Evaluates PopClip-style context requirements, regex patterns, length limits, and app filters.
/// </summary>
public sealed partial class PluginContextMatcher
{
    [GeneratedRegex(@"https?://[^\s<>""'()\[\]{}]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}$", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^[+\-]?\d+(?:[.,]\d+)?$", RegexOptions.Compiled)]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^(?:www\.)?[a-zA-Z0-9\-]+(?:\.[a-zA-Z]{2,})+(?:/[^\s]*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BareDomainRegex();

    // Matches pure length-cap regexes used by generic PopClip extensions (e.g. Instant Translate's "(?s)^.{1,5000}$").
    [GeneratedRegex(@"^(?:\(\?[a-z]+\))?\^?(?:\.|\[\\s\\S\])\{(?:\d+)?,\d*\}\$?$", RegexOptions.Compiled)]
    private static partial Regex GenericLengthCapRegex();

    public SelectionContext EnrichContext(SelectionContext context)
    {
        var urls = new List<string>();
        foreach (Match m in UrlRegex().Matches(context.Text))
        {
            urls.Add(m.Value.TrimEnd('.', ',', ';', ')', ']'));
        }

        if (urls.Count == 0 && BareDomainRegex().IsMatch(context.TrimmedText))
        {
            urls.Add("https://" + context.TrimmedText);
        }

        var emails = new List<string>();
        if (EmailRegex().IsMatch(context.TrimmedText))
        {
            emails.Add(context.TrimmedText);
        }

        return new SelectionContext
        {
            Text = context.Text,
            ProcessName = context.ProcessName,
            WindowTitle = context.WindowTitle,
            WindowHandle = context.WindowHandle,
            IsEditable = context.IsEditable,
            IsPasswordField = context.IsPasswordField,
            IsEmptyInputSummon = context.IsEmptyInputSummon,
            IsDeferredCapture = context.IsDeferredCapture,
            HasClipboardText = context.HasClipboardText,
            Source = context.Source,
            SelectionBounds = context.SelectionBounds,
            AnchorPoint = context.AnchorPoint,
            CapturedAt = context.CapturedAt,
            DetectedUrls = urls,
            DetectedEmails = emails
        };
    }

    public IReadOnlyList<MatchedPlugin> MatchPlugins(
        IEnumerable<PluginManifest> plugins,
        SelectionContext rawContext,
        int maxCount = 20)
    {
        if (rawContext.IsPasswordField)
        {
            return Array.Empty<MatchedPlugin>();
        }

        if (string.IsNullOrWhiteSpace(rawContext.Text) &&
            !rawContext.IsDeferredCapture &&
            (!rawContext.IsEmptyInputSummon || !rawContext.IsEditable))
        {
            return Array.Empty<MatchedPlugin>();
        }

        var context = EnrichContext(rawContext);
        var results = new List<MatchedPlugin>();

        foreach (var plugin in plugins.Where(p => p.Enabled).OrderBy(p => p.Order))
        {
            if (TryMatch(plugin, context, out var matched))
            {
                results.Add(matched!);
                if (results.Count >= maxCount)
                {
                    break;
                }
            }
        }

        return results;
    }

    public bool TryMatch(PluginManifest plugin, SelectionContext context, out MatchedPlugin? matched)
    {
        matched = null;

        if (!plugin.Enabled)
            return false;

        // App whitelist / blacklist
        if (plugin.AllowedApps.Count > 0 &&
            !plugin.AllowedApps.Any(a => a.Equals(context.ProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (plugin.BlockedApps.Count > 0 &&
            plugin.BlockedApps.Any(a => a.Equals(context.ProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Doubao-style deferred capture mode (text is materialized only when the user clicks an action,
        // avoiding Ctrl+C on selection): allow generic text/editable actions (including Web Search with MaxLength
        // and Translate extensions with pure length-cap regexes like "(?s)^.{1,5000}$") while skipping
        // content-specific regex/URL/email/number predicates.
        if (context.IsDeferredCapture && string.IsNullOrWhiteSpace(context.Text))
        {
            if (!string.IsNullOrWhiteSpace(plugin.Regex) && !GenericLengthCapRegex().IsMatch(plugin.Regex.Trim()))
            {
                return false;
            }

            foreach (var req in plugin.Requirements)
            {
                var normalized = req.Trim().ToLowerInvariant();
                var passed = normalized switch
                {
                    "" or "text" => true,
                    "editable" => context.IsEditable,
                    "paste" => context.IsEditable,
                    "url" or "email" or "number" or "multiline" or "singleline" => false,
                    _ => true
                };
                if (!passed) return false;
            }

            matched = new MatchedPlugin(plugin);
            return true;
        }

        // Length checks
        var len = context.TrimmedText.Length;
        if (plugin.MinLength.HasValue && len < plugin.MinLength.Value)
            return false;
        if (plugin.MaxLength.HasValue && len > plugin.MaxLength.Value)
            return false;

        // When summoned in an empty editable dialog/input box, only plugins explicitly declaring
        // editable/paste (without requiring text/url/email/number) may match.
        if (context.IsEmptyInputSummon && string.IsNullOrWhiteSpace(context.Text) && plugin.Requirements.Count == 0)
        {
            return false;
        }

        // Requirements checks
        foreach (var req in plugin.Requirements)
        {
            var normalized = req.Trim().ToLowerInvariant();
            var passed = normalized switch
            {
                "" or "text" => !string.IsNullOrWhiteSpace(context.Text),
                "url" => context.DetectedUrls.Count > 0,
                "email" => context.DetectedEmails.Count > 0,
                "number" => !string.IsNullOrWhiteSpace(context.Text) && NumberRegex().IsMatch(context.TrimmedText),
                "editable" => context.IsEditable,
                "paste" => context.IsEditable && (!context.IsEmptyInputSummon || context.HasClipboardText),
                "multiline" => !string.IsNullOrWhiteSpace(context.Text) && context.Text.Contains('\n'),
                "singleline" => !string.IsNullOrWhiteSpace(context.Text) && !context.Text.Contains('\n'),
                _ => true
            };

            if (!passed)
                return false;
        }

        // Optional regex check
        string? regexMatchValue = null;
        IReadOnlyList<string>? regexGroups = null;
        if (!string.IsNullOrWhiteSpace(plugin.Regex))
        {
            try
            {
                var regex = new Regex(plugin.Regex, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150));
                var match = regex.Match(context.Text);
                if (!match.Success)
                    return false;

                regexMatchValue = match.Value;
                regexGroups = match.Groups.Cast<Group>().Select(g => g.Value).ToList();
            }
            catch
            {
                return false;
            }
        }

        matched = new MatchedPlugin(plugin, regexMatchValue, regexGroups);
        return true;
    }
}
