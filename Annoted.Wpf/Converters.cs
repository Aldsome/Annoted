using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Annoted.Wpf;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

public sealed class BoolToGlyphConverter : IValueConverter
{
    public string TrueValue  { get; set; } = string.Empty;
    public string FalseValue { get; set; } = string.Empty;
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is bool b && b ? TrueValue : FalseValue;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

public sealed class BoolToStringConverter : IValueConverter
{
    public string TrueValue  { get; set; } = string.Empty;
    public string FalseValue { get; set; } = string.Empty;
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is bool b && b ? TrueValue : FalseValue;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

public sealed class ZoomToFontSizeConverter : IValueConverter
{
    private const double BaseFontSize = 14.0;
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is float f ? BaseFontSize * f : BaseFontSize;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}
