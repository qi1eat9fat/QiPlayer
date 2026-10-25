using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace QiPlayer.Converters;

/// <summary>
/// 行间距（像素）→ 上下各半的 Margin，使相邻行的间距视觉均匀。
/// </summary>
public sealed class SpacingToMarginConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double spacing = value is double d ? d : 14;
        double half = Math.Max(0, spacing / 2);
        return new Thickness(0, half, 0, half);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
