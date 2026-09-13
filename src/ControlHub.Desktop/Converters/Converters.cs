using Avalonia.Data.Converters;
using Avalonia.Media;
using ControlHub.Desktop.ViewModels;

namespace ControlHub.Desktop.Converters;

/// <summary>把星期几（1=周一 … 6=周六，0=周日）映射为网格列索引（0-6）。</summary>
public sealed class DayToColumnConverter : IValueConverter
{
    public static readonly DayToColumnConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is int day)
        {
            return day == 0 ? 6 : day - 1;
        }

        return 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => null;
}

/// <summary>把「是否选中」映射为单元格背景画刷（选中高亮，否则透明）。</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public static readonly BoolToBrushConverter Instance = new();

    private static readonly IBrush Selected = new SolidColorBrush(Color.FromArgb(0x30, 0x1E, 0x90, 0xFF));
    private static readonly IBrush Normal = Brushes.Transparent;

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is true ? Selected : Normal;

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => null;
}
