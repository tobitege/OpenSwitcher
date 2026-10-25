using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenSwitcher.Core;

namespace OpenSwitcher;

public sealed class SelectableLocalModel(LocalModel model, bool isChecked) : INotifyPropertyChanged
{
    private bool _isChecked = isChecked;

    public LocalModel Model { get; } = model;
    public string Id => Model.Id;
    public string DisplayText => Model.DisplayText;
    public bool IsLoaded => Model.IsLoaded;
    public string LoadedLabel => Model.LoadedLabel;
    public string CapabilityLabel => Model.CapabilityLabel;

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
