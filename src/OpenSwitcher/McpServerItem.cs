using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using OpenSwitcher.Core;

namespace OpenSwitcher;

public sealed class McpServerItem
{
    public McpServerItem(McpServerDefinition definition)
    {
        Definition = definition;
        var status = McpProgramLocator.GetStatus(definition);
        StatusDescription = status.Description;
        StatusBrush = (Brush)Application.Current.Resources[
            status.IsReady ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"];
    }

    public McpServerDefinition Definition { get; }
    public string Name => Definition.Name;
    public string TypeLabel => Definition.TypeLabel;
    public string Target => Definition.Target;
    public string EnabledLabel => Definition.EnabledLabel;
    public string StatusDescription { get; }
    public Brush StatusBrush { get; }
}
