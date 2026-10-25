using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenSwitcher.Core;

namespace OpenSwitcher;

public sealed class SelectableMcpImport(ImportedMcpServer import, bool replacesExisting) : INotifyPropertyChanged
{
    private bool _isChecked;

    public McpServerDefinition Server { get; } = import.Server;
    public string Name => Server.Name;
    public string Origin { get; } = import.Origin;
    public string TypeLabel => Server.TypeLabel;
    public string Target => Server.Target;
    public string ConflictLabel => replacesExisting ? "replaces existing" : string.Empty;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
