using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace QiPlayer.Classes;

/// <summary>
/// 界面强调色。
///
/// 全应用统一使用同一个品牌蓝，不再从专辑封面取色：
/// 标题、进度条、当前歌词行、列表高亮、开关与滑杆始终是同一抹蓝，
/// 换歌不会改变界面配色，视觉上更稳定、也更容易和系统其它窗口对上。
///
/// 这里仍按深浅主题各算一组：
///   · Accent      —— 主色，浅色主题下压暗、深色主题下提亮到可读
///   · AccentSoft  —— 同色低透明度，用于选中行底色与药丸标签
///   · OnAccent    —— 强调色上的文字色（白或近黑）
/// </summary>
public static class AccentPalette
{
    /// <summary>品牌蓝（与 DesignTokens.xaml 的默认 AppAccentBrush 一致）。</summary>
    private static readonly Color BrandBlue = Color.FromRgb(0x4F, 0x7F, 0xD9);

    /// <summary>按当前主题生成一套强调色画刷。</summary>
    public static AccentColors Create()
    {
        bool dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        return Build(dark ? Lighten(BrandBlue, 0.16) : BrandBlue);
    }

    private static AccentColors Build(Color dominant)
    {
        bool dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

        // 小字号与细线条需要与背景拉开明度差：
        // 深色主题把主色提亮到至少 0.72 亮度，浅色主题压到至多 0.42。
        var readable = AdjustLightness(dominant, dark ? 0.72 : 0.42, dark);

        var soft = Color.FromArgb(38, readable.R, readable.G, readable.B);
        var softStrong = Color.FromArgb(64, readable.R, readable.G, readable.B);

        // 强调色上的文字：按相对亮度选黑或白
        double luminance = RelativeLuminance(readable);
        var onAccent = luminance > 0.5 ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White;

        return new AccentColors(
            Freeze(new SolidColorBrush(readable)),
            Freeze(new SolidColorBrush(AdjustLightness(readable, dark ? 0.62 : 0.30, dark))),
            Freeze(new SolidColorBrush(soft)),
            Freeze(new SolidColorBrush(softStrong)),
            Freeze(new SolidColorBrush(onAccent)));
    }

    /// <summary>
    /// 把颜色沿 HSL 明度轴推到目标值，保留色相与饱和度。
    /// 只按明度缩放会改变饱和度，品牌蓝在深色主题下会变得发灰。
    /// </summary>
    private static Color AdjustLightness(Color color, double targetL, bool dark)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;
        double d = max - min;

        double s = d == 0 ? 0 : (l > 0.5 ? d / (2 - max - min) : d / (max + min));
        double h = 0;

        if (d != 0)
        {
            if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
            else if (max == g) h = ((b - r) / d + 2) / 6;
            else h = ((r - g) / d + 4) / 6;
        }

        // 目标明度是「底线/上限」：已经足够可读的颜色不要反被拉回来
        l = dark ? Math.Max(l, targetL) : Math.Min(l, targetL);

        // 深色主题下饱和度过高会刺眼，浅色主题下过低则显得脏
        s = Math.Clamp(dark ? Math.Min(s, 0.62) : Math.Max(s, 0.45), 0, 1);

        return FromHsl(h, s, Math.Clamp(l, 0.05, 0.95));
    }

    private static Color FromHsl(double h, double s, double l)
    {
        if (s == 0)
        {
            byte v = (byte)Math.Round(l * 255);
            return Color.FromRgb(v, v, v);
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;

        return Color.FromRgb(
            (byte)Math.Round(HueToRgb(p, q, h + 1.0 / 3) * 255),
            (byte)Math.Round(HueToRgb(p, q, h) * 255),
            (byte)Math.Round(HueToRgb(p, q, h - 1.0 / 3) * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Color Lighten(Color c, double amount)
        => Color.FromRgb(
            (byte)Math.Min(255, c.R + 255 * amount),
            (byte)Math.Min(255, c.G + 255 * amount),
            (byte)Math.Min(255, c.B + 255 * amount));
}

/// <summary>一组强调色画刷。全部已冻结，可跨线程使用。</summary>
public sealed record AccentColors(
    SolidColorBrush Accent,
    SolidColorBrush AccentStrong,
    SolidColorBrush AccentSoft,
    SolidColorBrush AccentSoftStrong,
    SolidColorBrush OnAccent);
