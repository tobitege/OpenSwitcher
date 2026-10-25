using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenSwitcher.Core;
using Windows.Graphics;
using WinRT.Interop;

namespace OpenSwitcher;

public sealed partial class MainWindow : Window
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly LocalModelCatalog _catalog;
    private readonly OpenCodeConfigService _configService = new();
    private readonly ObservableCollection<LocalProviderSection> _localProviders =
        new(LocalProviderDescriptor.All.Select(descriptor => new LocalProviderSection(descriptor)));
    private readonly ObservableCollection<ConfiguredProvider> _providers = [];
    private readonly ObservableCollection<McpServerItem> _mcpServers = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public MainWindow()
    {
        InitializeComponent();
        Title = "OpenSwitcher";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "OpenSwitcher.ico"));
        _catalog = new LocalModelCatalog(_httpClient);
        LocalProvidersList.ItemsSource = _localProviders;
        ProvidersList.ItemsSource = _providers;
        McpServersList.ItemsSource = _mcpServers;
        ShowConfigPath(_configService.FindGlobalConfigPath());

        ResizeWindow();
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        await RefreshAllAsync();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _httpClient.Dispose();
        _writeLock.Dispose();
    }

    private void ResizeWindow()
    {
        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        AppWindow.GetFromWindowId(windowId).Resize(new SizeInt32(1120, 960));
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshAllAsync();

    private async Task RefreshAllAsync()
    {
        StatusInfoBar.IsOpen = false;
        var config = await LoadConfigAsync();
        await Task.WhenAll(_localProviders
            .Select(section => RefreshLocalProviderAsync(section, config))
            .Append(LoadProvidersAsync(config)));
        ShowMcpServers(config);
    }

    private async Task<ConfigLoadResult> LoadConfigAsync()
    {
        try
        {
            return new ConfigLoadResult(await _configService.LoadSnapshotAsync(), null);
        }
        catch (Exception exception) when (IsExpectedError(exception))
        {
            return new ConfigLoadResult(null, $"The OpenCode configuration could not be read: {FriendlyError(exception)}");
        }
    }

    private async Task RefreshLocalProviderAsync(LocalProviderSection section, ConfigLoadResult config)
    {
        section.BeginLoading();
        var descriptor = section.Descriptor;
        IReadOnlySet<string> configuredModelIds = new HashSet<string>(StringComparer.Ordinal);
        var baseUrl = descriptor.DefaultBaseUrl;
        var configError = config.Error;
        if (config.Snapshot is not null)
        {
            try
            {
                configuredModelIds = config.Snapshot.GetConfiguredModelIds(descriptor.Id);
                baseUrl = config.Snapshot.GetProviderBaseUrl(descriptor.Id) ?? baseUrl;
            }
            catch (InvalidDataException exception)
            {
                configError = $"The OpenCode selection could not be read: {exception.Message}";
            }
        }

        try
        {
            var models = await _catalog.GetModelsAsync(descriptor, baseUrl);
            section.ShowModels(models, configuredModelIds, configError is null ? null : $"{descriptor.DisplayName}: {configError}");
        }
        catch (Exception exception) when (IsExpectedError(exception))
        {
            section.ShowUnavailable($"{descriptor.DisplayName} ({LocalModelCatalog.GetServerRoot(baseUrl)}): {FriendlyError(exception)}");
        }
    }

    private async void LocalProviderRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LocalProviderSection section)
        {
            await RefreshLocalProviderAsync(section, await LoadConfigAsync());
        }
    }

    private async void ProvidersRefreshButton_Click(object sender, RoutedEventArgs e) =>
        await LoadProvidersAsync(await LoadConfigAsync());

    private async void McpRefreshButton_Click(object sender, RoutedEventArgs e) =>
        await ReloadMcpServersAsync();

    private static void SetSectionAvailability(bool isAvailable, Button refreshButton, params UIElement[] content)
    {
        refreshButton.Visibility = isAvailable ? Visibility.Collapsed : Visibility.Visible;
        foreach (var element in content)
        {
            element.Visibility = isAvailable ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void ApplyLocalProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LocalProviderSection section)
        {
            await SynchronizeProviderAsync(section);
        }
    }

    private async Task SynchronizeProviderAsync(LocalProviderSection section)
    {
        var selectedModels = section.SelectedModels;
        if (selectedModels.Count == 0)
        {
            return;
        }

        var providerName = section.DisplayName;
        foreach (var localProvider in _localProviders)
        {
            localProvider.IsSynchronizing = true;
        }

        try
        {
            ShowStatus(InfoBarSeverity.Informational, $"{providerName}: synchronizing {selectedModels.Count} models with OpenCode …");
            var result = await WriteConfigAsync(() =>
                _configService.SynchronizeProviderModelsAsync(section.Descriptor, selectedModels));
            ShowConfigPath(result.ConfigPath);
            ShowStatus(
                InfoBarSeverity.Success,
                result.DefaultModelChanged
                    ? $"{providerName}: applied {result.ModelCount} models. New default model: {result.DefaultModel}."
                    : $"{providerName}: applied {result.ModelCount} models to OpenCode.");
            await LoadProvidersAsync(await LoadConfigAsync());
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, $"Configuration not changed: {FriendlyError(exception)}");
        }
        finally
        {
            foreach (var localProvider in _localProviders)
            {
                localProvider.IsSynchronizing = false;
            }
        }
    }

    private async Task<T> WriteConfigAsync<T>(Func<Task<T>> write)
    {
        await _writeLock.WaitAsync();
        try
        {
            return await write();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task LoadProvidersAsync(ConfigLoadResult config)
    {
        _providers.Clear();
        ProvidersCountText.Text = "loading …";
        SetSectionWarning(ProvidersWarningIcon, null);
        if (config.Snapshot is null)
        {
            ShowProvidersUnavailable(config.Error!);
            return;
        }

        try
        {
            var providers = await OpenCodeProviderInspector.GetConfiguredProvidersAsync(config.Snapshot);
            foreach (var provider in providers)
            {
                _providers.Add(provider);
            }

            ProvidersCountText.Text = $"{providers.Count} configured";
            SetSectionAvailability(true, ProvidersRefreshButton, ProvidersHeader, ProvidersList);
        }
        catch (Exception exception) when (IsExpectedError(exception))
        {
            ShowProvidersUnavailable($"The OpenCode providers could not be read: {FriendlyError(exception)}");
        }
    }

    private void ShowProvidersUnavailable(string message)
    {
        ProvidersCountText.Text = "unavailable";
        SetSectionWarning(ProvidersWarningIcon, message);
        SetSectionAvailability(false, ProvidersRefreshButton, ProvidersHeader, ProvidersList);
    }

    private async Task ReloadMcpServersAsync() =>
        ShowMcpServers(await LoadConfigAsync());

    private void ShowMcpServers(ConfigLoadResult config)
    {
        _mcpServers.Clear();
        SetSectionWarning(McpWarningIcon, null);
        string? error = config.Error;
        if (config.Snapshot is not null)
        {
            try
            {
                foreach (var server in config.Snapshot.GetMcpServers())
                {
                    _mcpServers.Add(new McpServerItem(server));
                }
            }
            catch (InvalidDataException exception)
            {
                _mcpServers.Clear();
                error = $"The MCP servers could not be read: {exception.Message}";
            }
        }

        McpCountText.Text = error is null ? $"{_mcpServers.Count} configured" : "unavailable";
        SetSectionWarning(McpWarningIcon, error);
        SetSectionAvailability(error is null, McpRefreshButton, McpServersList, McpButtonsPanel);
        UpdateMcpButtonStates();
    }

    private void McpServersList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateMcpButtonStates();

    private void UpdateMcpButtonStates()
    {
        var hasSelection = McpServersList.SelectedItem is McpServerItem;
        EditMcpServerButton.IsEnabled = hasSelection;
        DeleteMcpServerButton.IsEnabled = hasSelection;
    }

    private async void AddMcpServerButton_Click(object sender, RoutedEventArgs e) =>
        await ShowMcpServerDialogAsync(null);

    private async void EditMcpServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (McpServersList.SelectedItem is McpServerItem item)
        {
            await ShowMcpServerDialogAsync(item.Definition);
        }
    }

    private async Task ShowMcpServerDialogAsync(McpServerDefinition? server)
    {
        var savedName = string.Empty;
        var dialog = new McpServerDialog(AppWindow.Id, server, async definition =>
        {
            var result = await WriteConfigAsync(() => _configService.SaveMcpServerAsync(server?.Name, definition));
            ShowConfigPath(result.ConfigPath);
            savedName = definition.Name;
        })
        {
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ShowStatus(InfoBarSeverity.Success, $"MCP server '{savedName}' saved to OpenCode.");
        await ReloadMcpServersAsync();
        McpServersList.SelectedItem = _mcpServers.FirstOrDefault(item => item.Name == savedName);
    }

    private async void DeleteMcpServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (McpServersList.SelectedItem is not McpServerItem server)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = "Delete MCP server",
            Content = $"Remove the MCP server '{server.Name}' from the OpenCode configuration?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var result = await WriteConfigAsync(() => _configService.DeleteMcpServerAsync(server.Name));
            ShowConfigPath(result.ConfigPath);
            ShowStatus(InfoBarSeverity.Success, $"MCP server '{server.Name}' removed from OpenCode.");
        }
        catch (Exception exception)
        {
            ShowStatus(InfoBarSeverity.Error, $"Configuration not changed: {FriendlyError(exception)}");
        }

        await ReloadMcpServersAsync();
    }

    private async void ImportMcpServersButton_Click(object sender, RoutedEventArgs e)
    {
        var existingNames = _mcpServers.Select(server => server.Name).ToHashSet(StringComparer.Ordinal);
        var importedCount = 0;
        var dialog = new McpImportDialog(existingNames, async servers =>
        {
            var result = await WriteConfigAsync(() => _configService.ImportMcpServersAsync(servers));
            ShowConfigPath(result.ConfigPath);
            importedCount = servers.Count;
        })
        {
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ShowStatus(InfoBarSeverity.Success, $"Imported {importedCount} MCP servers into OpenCode.");
        await ReloadMcpServersAsync();
    }

    private void ShowStatus(InfoBarSeverity severity, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }

    private void ShowConfigPath(string configPath)
    {
        var additionalConfigPath = OpenCodeConfigLocator.FindAdditionalConfigPath();
        ConfigPathText.Text = additionalConfigPath is null
            ? $"OpenCode configuration: {configPath}"
            : $"OpenCode configuration: {configPath}{Environment.NewLine}" +
              $"OPENCODE_CONFIG: {additionalConfigPath} (merged on top by OpenCode, not shown or edited here)";
    }

    private static void SetSectionWarning(TextBlock warningIcon, string? message)
    {
        warningIcon.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
        ToolTipService.SetToolTip(warningIcon, message);
    }

    private static bool IsExpectedError(Exception exception) =>
        exception is HttpRequestException
            or TaskCanceledException
            or JsonException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException;

    private static string FriendlyError(Exception exception) => exception switch
    {
        TaskCanceledException => "The local server timed out.",
        HttpRequestException => "Server unavailable or HTTP error.",
        _ => exception.Message
    };

    private sealed record ConfigLoadResult(OpenCodeConfigSnapshot? Snapshot, string? Error);
}
