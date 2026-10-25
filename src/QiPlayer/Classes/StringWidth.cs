using System.Globalization;
using System.Windows;
using System.Windows.Media;
using QiPlayer.Classes.Settings;

namespace QiPlayer.Classes;

/// <summary>
/// 文本宽度测量（任务栏挂件计算尺寸与滚动距离用）。
/// 移植自 FluentFlyout，字体取自设置。
/// </summary>
internal static class StringWidth
{
    private static FontFamily? _fontFamily;
    private static string _cachedFontFamily = string.Empty;
    private static readonly Dictionary<string, Typeface> CachedTypefaces = [];

    /// <summary>
    /// 测量文本宽度（DIP）。
    /// </summary>
    /// <param name="text">要测量的文本。</param>
    /// <param name="fontWeight">字重，默认 500（Medium）。</param>
    /// <param name="fontSize">字号，默认 14。</param>
    public static double GetStringWidth(string? text, int fontWeight = 500, int fontSize = 14)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            GetTypeface(fontWeight),
            fontSize,
            Brushes.Black,
            null,
            1);

        return formattedText.Width;
    }

    private static Typeface GetTypeface(int fontWeight)
    {
        string currentFontFamily = string.IsNullOrWhiteSpace(SettingsManager.Current.FontFamily)
            ? "Segoe UI Variable, Microsoft YaHei UI"
            : SettingsManager.Current.FontFamily;

        if (_fontFamily is null || _cachedFontFamily != currentFontFamily)
        {
            _fontFamily = new FontFamily(currentFontFamily);
            _cachedFontFamily = currentFontFamily;
        }

        string key = currentFontFamily + fontWeight;

        if (CachedTypefaces.TryGetValue(key, out var cached))
            return cached;

        var typeface = new Typeface(
            _fontFamily,
            new FontStyle(),
            fontWeight == 400 ? FontWeights.Normal : FontWeights.Medium,
            FontStretches.Normal);

        CachedTypefaces[key] = typeface;
        return typeface;
    }
}
