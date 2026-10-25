using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenSwitcher.Core;

namespace OpenSwitcher;

public sealed partial class McpImportDialog : ContentDialog
{
    private static readonly McpImportSource[] Sources =
    [
        McpImportSource.Codex,
        McpImportSource.ClaudeCode,
        McpImportSource.Cursor
    ];

    private readonly ObservableCollection<SelectableMcpImport> _servers = [];
    private readonly IReadOnlySet<string> _existingNames;
    private readonly Func<IReadOnlyCollection<McpServerDefinition>, Task> _importAsync;

    public McpImportDialog(
        IReadOnlySet<string> existingNames,
        Func<IReadOnlyCollection<McpServerDefinition>, Task> importAsync)
    {
        InitializeComponent();
        _existingNames = existingNames;
        _importAsync = importAsync;
        ServersList.ItemsSource = _servers;
        SourceComboBox.SelectedIndex = 0;
    }

    private async void SourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceComboBox.SelectedIndex < 0)
        {
            return;
        }

        var source = Sources[SourceComboBox.SelectedIndex];
        var configPath = McpConfigImporter.GetConfigPath(source);
        SourcePathText.Text = configPath;
        StatusInfoBar.IsOpen = false;
        _servers.Clear();
        UpdatePrimaryButton();

        try
        {
            var imports = await McpConfigImporter.LoadAsync(source, configPath);
            if (SourceComboBox.SelectedIndex < 0 || Sources[SourceComboBox.SelectedIndex] != source)
            {
                return;
            }

            foreach (var import in imports)
            {
                _servers.Add(new SelectableMcpImport(import, _existingNames.Contains(import.Server.Name)));
            }

            if (_servers.Count == 0)
            {
                StatusInfoBar.Severity = InfoBarSeverity.Informational;
                StatusInfoBar.Message = File.Exists(configPath)
                    ? "No MCP servers found in this configuration."
                    : "This configuration file does not exist.";
                StatusInfoBar.IsOpen = true;
            }
        }
        catch (Exception exception)
        {
            if (SourceComboBox.SelectedIndex < 0 || Sources[SourceComboBox.SelectedIndex] != source)
            {
                return;
            }

            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Message = $"The configuration could not be read: {exception.Message}";
            StatusInfoBar.IsOpen = true;
        }
    }

    private void ServerCheckBox_Changed(object sender, RoutedEventArgs e) =>
        UpdatePrimaryButton();

    private void UpdatePrimaryButton() =>
        IsPrimaryButtonEnabled = _servers.Any(server => server.IsChecked);

    private async void McpImportDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            StatusInfoBar.IsOpen = false;
            var selectedServers = _servers.Where(server => server.IsChecked).Select(server => server.Server).ToArray();
            await _importAsync(selectedServers);
        }
        catch (Exception exception)
        {
            args.Cancel = true;
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Message = $"Configuration not changed: {exception.Message}";
            StatusInfoBar.IsOpen = true;
        }
        finally
        {
            deferral.Complete();
        }
    }
}
