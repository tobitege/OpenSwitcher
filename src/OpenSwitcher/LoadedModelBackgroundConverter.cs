using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace OpenSwitcher;

public sealed class LoadedModelBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush LoadedBrush = new(ColorHelper.FromArgb(48, 0, 120, 212));
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? LoadedBrush : TransparentBrush;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
