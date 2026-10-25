using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenSwitcher.Core;

namespace OpenSwitcher;

public sealed class LocalProviderSection(LocalProviderDescriptor descriptor) : INotifyPropertyChanged
{
    private bool _isAvailable;
    private bool _isUnavailable;
    private bool _isSynchronizing;
    private string _countText = "loading …";
    private string? _warning;

    public LocalProviderDescriptor Descriptor { get; } = descriptor;
    public string DisplayName => Descriptor.DisplayName;
    public string ApplyText => $"Apply {Descriptor.DisplayName} selection";
    public string RefreshToolTip => $"Refresh {Descriptor.DisplayName}";
    public ObservableCollection<SelectableLocalModel> Models { get; } = [];

    public bool IsAvailable
    {
        get => _isAvailable;
        private set => SetField(ref _isAvailable, value, nameof(IsAvailable), nameof(IsApplyEnabled));
    }

    public bool IsUnavailable
    {
        get => _isUnavailable;
        private set => SetField(ref _isUnavailable, value, nameof(IsUnavailable));
    }

    public bool IsSynchronizing
    {
        get => _isSynchronizing;
        set => SetField(ref _isSynchronizing, value, nameof(IsSynchronizing), nameof(IsApplyEnabled));
    }

    public string CountText
    {
        get => _countText;
        private set => SetField(ref _countText, value, nameof(CountText));
    }

    public string? Warning
    {
        get => _warning;
        private set => SetField(ref _warning, value, nameof(Warning), nameof(HasWarning));
    }

    public bool HasWarning => !string.IsNullOrWhiteSpace(Warning);

    public bool IsApplyEnabled => IsAvailable && !IsSynchronizing && Models.Any(model => model.IsChecked);

    public IReadOnlyList<LocalModel> SelectedModels =>
        Models.Where(model => model.IsChecked).Select(model => model.Model).ToArray();

    public event PropertyChangedEventHandler? PropertyChanged;

    public void BeginLoading()
    {
        foreach (var model in Models)
        {
            model.PropertyChanged -= Model_PropertyChanged;
        }

        Models.Clear();
        CountText = "loading …";
        Warning = null;
        OnPropertyChanged(nameof(IsApplyEnabled));
    }

    public void ShowModels(IEnumerable<LocalModel> models, IReadOnlySet<string> configuredModelIds, string? warning)
    {
        foreach (var model in models)
        {
            var item = new SelectableLocalModel(model, configuredModelIds.Contains(model.Id));
            item.PropertyChanged += Model_PropertyChanged;
            Models.Add(item);
        }

        IsAvailable = true;
        IsUnavailable = false;
        Warning = warning;
        UpdateCountText();
        OnPropertyChanged(nameof(IsApplyEnabled));
    }

    public void ShowUnavailable(string error)
    {
        IsAvailable = false;
        IsUnavailable = true;
        CountText = "unavailable";
        Warning = error;
    }

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SelectableLocalModel.IsChecked))
        {
            return;
        }

        UpdateCountText();
        OnPropertyChanged(nameof(IsApplyEnabled));
    }

    private void UpdateCountText()
    {
        var loadedCount = Models.Count(model => model.IsLoaded);
        var selectedCount = Models.Count(model => model.IsChecked);
        var parts = new List<string> { $"{Models.Count} local" };
        if (loadedCount > 0)
        {
            parts.Add($"{loadedCount} loaded");
        }

        parts.Add($"{selectedCount} selected");
        CountText = string.Join(" · ", parts);
    }

    private void SetField<T>(ref T field, T value, params string[] propertyNames)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        foreach (var propertyName in propertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
