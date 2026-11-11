using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace StartupManager.Gui;

/// <summary>Enabled -> "已启用" / "已禁用"</summary>
public class BoolTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? "已启用" : "已禁用";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Enabled -> green / red brush for the status badge.</summary>
public class BoolBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x34, 0xA8, 0x53));
    private static readonly SolidColorBrush Red = new(Color.FromRgb(0xEA, 0x43, 0x35));
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Green : Red;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Negates a bool (used to disable a button while a queue is running).</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not bool b || !b;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not bool b || !b;
}

/// <summary>bool -> Visible / Collapsed (used for the queue error banner and the ⚠ marker).</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Non-empty string -> Visible / Collapsed (shows the error panel only when there is text).</summary>
public class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string)
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
