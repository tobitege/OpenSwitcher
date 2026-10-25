using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using OpenSwitcher.Core;

namespace OpenSwitcher;

public sealed partial class McpServerDialog : ContentDialog
{
    private static readonly string[] LineSeparators = ["\r\n", "\r", "\n"];
    private readonly WindowId _windowId;
    private readonly Func<McpServerDefinition, Task> _saveAsync;

    public McpServerDialog(
        WindowId windowId,
        McpServerDefinition? server,
        Func<McpServerDefinition, Task> saveAsync)
    {
        InitializeComponent();
        _windowId = windowId;
        _saveAsync = saveAsync;
        Title = server is null ? "Add MCP server" : $"Edit MCP server '{server.Name}'";
        TypeRadioButtons.SelectedIndex = server?.Type == McpServerType.Remote ? 1 : 0;
        if (server is null)
        {
            return;
        }

        NameTextBox.Text = server.Name;
        if (server.Type == McpServerType.Local)
        {
            ProgramTextBox.Text = server.Command.FirstOrDefault() ?? string.Empty;
            ArgumentsTextBox.Text = string.Join("\r", server.Command.Skip(1));
            EnvironmentTextBox.Text = string.Join("\r", server.Environment.Select(item => $"{item.Key}={item.Value}"));
        }
        else
        {
            UrlTextBox.Text = server.Url ?? string.Empty;
            HeadersTextBox.Text = string.Join("\r", server.Headers.Select(item => $"{item.Key}: {item.Value}"));
        }

        TimeoutNumberBox.Value = server.Timeout ?? double.NaN;
        EnabledCheckBox.IsChecked = server.Enabled;
    }

    private void TypeRadioButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var isRemote = TypeRadioButtons.SelectedIndex == 1;
        LocalPanel.Visibility = isRemote ? Visibility.Collapsed : Visibility.Visible;
        RemotePanel.Visibility = isRemote ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BrowseProgramButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(_windowId);
        picker.FileTypeFilter.Add(".exe");
        var result = await picker.PickSingleFileAsync();
        if (result is not null)
        {
            ProgramTextBox.Text = result.Path;
        }
    }

    private async void McpServerDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            ErrorInfoBar.IsOpen = false;
            await _saveAsync(ReadServer());
        }
        catch (Exception exception)
        {
            args.Cancel = true;
            ErrorInfoBar.Message = $"Configuration not changed: {exception.Message}";
            ErrorInfoBar.IsOpen = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private McpServerDefinition ReadServer()
    {
        var name = NameTextBox.Text.Trim();
        var enabled = EnabledCheckBox.IsChecked is true;
        int? timeout = double.IsNaN(TimeoutNumberBox.Value)
            ? null
            : checked((int)Math.Round(TimeoutNumberBox.Value));

        if (TypeRadioButtons.SelectedIndex == 1)
        {
            return new McpServerDefinition(
                name,
                McpServerType.Remote,
                [],
                new Dictionary<string, string>(StringComparer.Ordinal),
                UrlTextBox.Text.Trim(),
                ParseMap(HeadersTextBox.Text, ':', "Headers", "Name: value"),
                enabled,
                timeout);
        }

        var command = new List<string> { ProgramTextBox.Text.Trim() };
        command.AddRange(SplitLines(ArgumentsTextBox.Text)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0));

        return new McpServerDefinition(
            name,
            McpServerType.Local,
            command,
            ParseMap(EnvironmentTextBox.Text, '=', "Environment", "NAME=value"),
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            enabled,
            timeout);
    }

    private static string[] SplitLines(string text) =>
        text.Split(LineSeparators, StringSplitOptions.None);

    private static Dictionary<string, string> ParseMap(string text, char separator, string fieldName, string expectedFormat)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = SplitLines(text);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var separatorIndex = line.IndexOf(separator);
            var key = separatorIndex > 0 ? line[..separatorIndex].Trim() : string.Empty;
            if (key.Length == 0)
            {
                throw new FormatException($"{fieldName}, line {index + 1}: expected {expectedFormat}.");
            }

            if (!map.TryAdd(key, line[(separatorIndex + 1)..].Trim()))
            {
                throw new FormatException($"{fieldName}, line {index + 1}: '{key}' is defined more than once.");
            }
        }

        return map;
    }
}
