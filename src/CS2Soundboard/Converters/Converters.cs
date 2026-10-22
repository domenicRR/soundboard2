using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CS2Soundboard.ViewModels;

namespace CS2Soundboard.Converters;

public sealed class StatusLevelToBrushConverter : IValueConverter
{
    public Brush Ok { get; set; } = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    public Brush Warning { get; set; } = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
    public Brush Error { get; set; } = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        StatusLevel.Ok => Ok,
        StatusLevel.Warning => Warning,
        _ => Error,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
