using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CoDDedupe;

/// <summary>Visible for true, a non-zero number or a non-empty string; Invert flips it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool truthy = value switch { bool b => b, int i => i != 0, string s => s.Length > 0, null => false, _ => true };
        return truthy != Invert ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
}
