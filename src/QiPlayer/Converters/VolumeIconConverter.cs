using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace QiPlayer.Converters;

/// <summary>
/// 音量 + 静音状态 → 扬声器图标。
/// 取值顺序：[0] 音量（0.0–1.0）、[1] 是否静音。
/// 静音或音量为 0 显示静音图标，其余按音量大小分三档。
/// </summary>
public sealed class VolumeIconConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        double volume = values.Length > 0 && values[0] is double v ? v : 1.0;
        bool muted = values.Length > 1 && values[1] is true;

        if (muted || volume <= 0.001) return SymbolRegular.SpeakerMute24;
        if (volume < 0.34) return SymbolRegular.Speaker024;
        if (volume < 0.67) return SymbolRegular.Speaker124;
        return SymbolRegular.Speaker224;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
