using System.Globalization;
using System.Windows.Data;
using QiPlayer.ViewModels;
using Wpf.Ui.Controls;

namespace QiPlayer.Converters;

/// <summary>
/// 播放模式 → 图标。
/// 列表循环用「重复全部」，单曲循环用「单曲重复」，无序播放用洗牌图标。
/// </summary>
public sealed class RepeatModeIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is RepeatMode mode
            ? mode switch
            {
                RepeatMode.LoopTrack => SymbolRegular.ArrowRepeat124,
                RepeatMode.Shuffle => SymbolRegular.ArrowShuffle24,
                _ => SymbolRegular.ArrowRepeatAll24,
            }
            : SymbolRegular.ArrowRepeatAll24;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}