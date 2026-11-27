using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinPopClip.App.Services;
using WinPopClip.Core.Models;
using WinPopClip.Core.Plugins;

namespace WinPopClip.App.Views;

public partial class PluginEditorDialog : Window
{
    public PluginManifest? ResultManifest { get; private set; }

    public PluginEditorDialog(PluginManifest? existing = null)
    {
        InitializeComponent();
        PopulateIconPresets();
        if (existing is not null)
        {
            LoadFromManifest(existing);
        }
        UpdateKindPanels();
    }

    private void PopulateIconPresets()
    {
        var fg = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;
        var invFg = TryFindResource("SurfaceBrush") as Brush ?? Brushes.White;

        foreach (var preset in VectorIconFactory.Presets)
        {
            var btn = new Button
            {
                Content = VectorIconFactory.CreateIcon(preset.Value, fg, invFg, size: 14),
                ToolTip = $"{preset.Label} ({preset.Value})",
                Width = 34,
                Height = 30,
                Padding = new Thickness(4),
                Margin = new Thickness(0, 0, 5, 5),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.Click += (_, _) => IconBox.Text = preset.Value;
            IconPresetsPanel.Children.Add(btn);
        }
    }

    private void OnBrowseIconClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择插件自定义浮窗图标",
            Filter = "图片与矢量图标 (*.png;*.svg;*.ico;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.svg;*.ico;*.jpg;*.jpeg;*.webp;*.bmp|所有文件 (*.*)|*.*",
            Multiselect = false
        };
        if (dlg.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dlg.FileName)) return;

        try
        {
            var idHint = string.IsNullOrWhiteSpace(IdBox.Text) ? NameBox.Text : IdBox.Text;
            var stored = VectorIconFactory.SaveCustomIconFile(dlg.FileName, string.IsNullOrWhiteSpace(idHint) ? "custom" : idHint.Trim(), WinPopClip.Core.Storage.AppPaths.GetCustomIconsDirectory());
            IconBox.Text = $"file:{stored}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导入图标失败：{ex.Message}", "Selector", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadFromManifest(PluginManifest m)
    {
        IdBox.Text = m.Id;
        IdBox.IsReadOnly = true;
        NameBox.Text = m.Name;
        IconBox.Text = m.Icon;
        DescriptionBox.Text = m.Description;

        SelectComboByTag(KindCombo, m.Kind.ToString());
        SelectComboByTag(AfterCombo, m.After.ToString());

        UrlTemplateBox.Text = m.Action.UrlTemplate ?? string.Empty;
        JsCodeBox.Text = m.Action.JavaScriptCode ?? string.Empty;
        JsNetworkCheck.IsChecked = m.Action.Entitlements.Any(e => e.Equals("network", StringComparison.OrdinalIgnoreCase));

        SelectComboByContent(TransformOpCombo, m.Action.TransformOperation ?? "uppercase");
        TransformPatternBox.Text = m.Action.TransformPattern ?? string.Empty;
        TransformReplacementBox.Text = m.Action.TransformReplacement ?? string.Empty;

        ShellExeBox.Text = m.Action.ShellExecutable ?? "powershell";
        ShellStdinCheck.IsChecked = m.Action.PassTextViaStdin;
        ShellScriptBox.Text = m.Action.ShellScript ?? m.Action.ShellArguments ?? string.Empty;

        SelectComboByContent(HttpMethodCombo, m.Action.HttpMethod ?? "POST");
        HttpUrlBox.Text = m.Action.HttpUrl ?? string.Empty;
        HttpBodyBox.Text = m.Action.HttpBodyTemplate ?? string.Empty;
        HttpJsonPathBox.Text = m.Action.HttpResponseJsonPath ?? string.Empty;

        RequirementsBox.Text = string.Join(", ", m.Requirements);
        RegexBox.Text = m.Regex ?? string.Empty;
        AllowedAppsBox.Text = string.Join(", ", m.AllowedApps);
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateKindPanels();
    }

    private void UpdateKindPanels()
    {
        if (UrlPanel is null) return;
        var kind = GetSelectedTag(KindCombo, "Url");
        UrlPanel.Visibility = kind == "Url" ? Visibility.Visible : Visibility.Collapsed;
        JsPanel.Visibility = kind == "JavaScript" ? Visibility.Visible : Visibility.Collapsed;
        TransformPanel.Visibility = kind == "TextTransform" ? Visibility.Visible : Visibility.Collapsed;
        ShellPanel.Visibility = kind == "Shell" ? Visibility.Visible : Visibility.Collapsed;
        HttpPanel.Visibility = kind == "Http" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "请填写插件名称。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var kindStr = GetSelectedTag(KindCombo, "Url");
        var afterStr = GetSelectedTag(AfterCombo, "None");
        Enum.TryParse<PluginKind>(kindStr, out var kind);
        Enum.TryParse<AfterActionType>(afterStr, out var after);

        var action = new PluginActionConfig();
        switch (kind)
        {
            case PluginKind.Url:
                if (string.IsNullOrWhiteSpace(UrlTemplateBox.Text))
                {
                    MessageBox.Show(this, "请填写 URL 模板。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                action.UrlTemplate = PopClipSnippetParser.ConvertPopClipUrlPlaceholders(UrlTemplateBox.Text.Trim());
                break;

            case PluginKind.JavaScript:
                if (string.IsNullOrWhiteSpace(JsCodeBox.Text))
                {
                    MessageBox.Show(this, "请填写 JavaScript 脚本代码。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                action.JavaScriptCode = JsCodeBox.Text;
                if (JsNetworkCheck.IsChecked == true)
                {
                    action.Entitlements.Add("network");
                }
                break;

            case PluginKind.TextTransform:
                action.TransformOperation = (TransformOpCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "uppercase";
                action.TransformPattern = string.IsNullOrWhiteSpace(TransformPatternBox.Text) ? null : TransformPatternBox.Text;
                action.TransformReplacement = TransformReplacementBox.Text;
                break;

            case PluginKind.Shell:
                action.ShellExecutable = string.IsNullOrWhiteSpace(ShellExeBox.Text) ? "powershell" : ShellExeBox.Text.Trim();
                action.PassTextViaStdin = ShellStdinCheck.IsChecked == true;
                action.ShellScript = ShellScriptBox.Text;
                break;

            case PluginKind.Http:
                if (string.IsNullOrWhiteSpace(HttpUrlBox.Text))
                {
                    MessageBox.Show(this, "请填写 HTTP URL。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                action.HttpMethod = (HttpMethodCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "POST";
                action.HttpUrl = HttpUrlBox.Text.Trim();
                action.HttpBodyTemplate = HttpBodyBox.Text;
                action.HttpResponseJsonPath = string.IsNullOrWhiteSpace(HttpJsonPathBox.Text) ? null : HttpJsonPathBox.Text.Trim();
                break;
        }

        var requirements = RequirementsBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (requirements.Count == 0)
        {
            requirements.Add("text");
        }

        var allowedApps = AllowedAppsBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        ResultManifest = new PluginManifest
        {
            Id = string.IsNullOrWhiteSpace(IdBox.Text) ? $"custom.{Guid.NewGuid():N}"[..15] : IdBox.Text.Trim(),
            Name = name,
            Icon = string.IsNullOrWhiteSpace(IconBox.Text) ? "⚡" : IconBox.Text.Trim(),
            Description = DescriptionBox.Text.Trim(),
            Kind = kind,
            After = after,
            Action = action,
            Requirements = requirements,
            Regex = string.IsNullOrWhiteSpace(RegexBox.Text) ? null : RegexBox.Text.Trim(),
            AllowedApps = allowedApps,
            Source = PluginSourceType.UserCustom,
            Enabled = true
        };

        DialogResult = true;
    }

    private static string GetSelectedTag(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private static void SelectComboByContent(ComboBox combo, string content)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }
}
