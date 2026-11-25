using System.Windows;
using System.Windows.Controls;
using WinPopClip.Core.Models;
using WinPopClip.Core.Plugins;

namespace WinPopClip.App.Views;

public partial class PopClipImportDialog : Window
{
    public PluginManifest? ParsedManifest { get; private set; }

    public PopClipImportDialog()
    {
        InitializeComponent();
        LoadUrlSample();
    }

    private void OnSnippetTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (PopClipSnippetParser.TryParseSnippet(SnippetBox.Text, out var manifest, out var err) && manifest is not null)
        {
            ParsedManifest = manifest;
            PreviewText.SetResourceReference(TextBlock.ForegroundProperty, "StatusOkBrush");
            PreviewText.Text = $"✔ 解析成功：{manifest.Name} | 图标: {manifest.Icon} | 类型: {manifest.Kind} | 执行后: {manifest.After} | ID: {manifest.Id}";
            ImportButton.IsEnabled = true;
        }
        else
        {
            ParsedManifest = null;
            PreviewText.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrBrush");
            PreviewText.Text = $"✘ 无法解析：{err}";
            ImportButton.IsEnabled = false;
        }
    }

    private void OnLoadUrlSampleClick(object sender, RoutedEventArgs e) => LoadUrlSample();

    private void OnLoadJsSampleClick(object sender, RoutedEventArgs e)
    {
        SnippetBox.Text =
            "// #popclip\n" +
            "// name: Markdown 代码块\n" +
            "// icon: square filled MD\n" +
            "// after: paste-result\n" +
            "return '```\\n' + popclip.input.text + '\\n```';";
    }

    private void LoadUrlSample()
    {
        SnippetBox.Text =
            "#popclip\n" +
            "name: DuckDuckGo 搜索\n" +
            "icon: search\n" +
            "url: https://duckduckgo.com/?q=***";
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        UpdatePreview();
        if (ParsedManifest is not null)
        {
            DialogResult = true;
        }
    }
}
