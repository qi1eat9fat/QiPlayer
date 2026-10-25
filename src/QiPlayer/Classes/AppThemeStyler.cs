using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace QiPlayer.Classes;

/// <summary>
/// 把深浅主题对应的中性色写入应用资源。
///
/// 为什么不用 XAML 里的 ThemeDictionary：
/// 本项目的样式（Styles/*.xaml）需要读写一组自定义语义键
/// （CardBackgroundBrush / AppHoverBrush …），而 WPF-UI 的主题字典
/// 只覆盖它自己的键。用一个最小的代码层做桥接，比给两套主题各写一份
/// 资源字典、再靠合并顺序覆盖要不容易出错。
/// </summary>
internal static class AppThemeStyler
{
    /// <summary>按当前生效的主题刷新令牌。ThemeManager 切换主题后调用。</summary>
    public static void Apply()
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        bool dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

        if (dark)
        {
            Set(resources, "CardBackgroundBrush", Color.FromArgb(0x94, 0x2B, 0x2B, 0x2B));
            Set(resources, "CardBackgroundSubtleBrush", Color.FromArgb(0x70, 0x32, 0x32, 0x32));
            Set(resources, "CardStrokeBrush", Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            Set(resources, "CardStrokeStrongBrush", Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            Set(resources, "AppHoverBrush", Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
            Set(resources, "AppPressedBrush", Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
            Set(resources, "AppTextSecondaryBrush", Color.FromArgb(0xC8, 0xF5, 0xF5, 0xF5));
            Set(resources, "AppTextTertiaryBrush", Color.FromArgb(0x8F, 0xF5, 0xF5, 0xF5));
            Set(resources, "AppTrackBrush", Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
            Set(resources, "TitleBarButtonHoverBrush", Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            Set(resources, "TitleBarButtonPressedBrush", Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            Set(resources, "SettingsStageBrush", Color.FromArgb(0xFF, 0x17, 0x17, 0x17));
        }
        else
        {
            Set(resources, "CardBackgroundBrush", Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF));
            Set(resources, "CardBackgroundSubtleBrush", Color.FromArgb(0x75, 0xFF, 0xFF, 0xFF));
            Set(resources, "CardStrokeBrush", Color.FromArgb(0x14, 0x00, 0x00, 0x00));
            Set(resources, "CardStrokeStrongBrush", Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
            Set(resources, "AppHoverBrush", Color.FromArgb(0x0C, 0x00, 0x00, 0x00));
            Set(resources, "AppPressedBrush", Color.FromArgb(0x15, 0x00, 0x00, 0x00));
            Set(resources, "AppTextSecondaryBrush", Color.FromArgb(0x8C, 0x00, 0x00, 0x00));
            Set(resources, "AppTextTertiaryBrush", Color.FromArgb(0x66, 0x00, 0x00, 0x00));
            Set(resources, "AppTrackBrush", Color.FromArgb(0x24, 0x00, 0x00, 0x00));
            Set(resources, "TitleBarButtonHoverBrush", Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
            Set(resources, "TitleBarButtonPressedBrush", Color.FromArgb(0x2E, 0x00, 0x00, 0x00));
            Set(resources, "SettingsStageBrush", Color.FromArgb(0xFF, 0xE4, 0xE4, 0xE4));
        }

        // 强调色固定为品牌蓝，但深浅主题的明度规则不同，切主题时需要重算。
        var accent = AccentPalette.Create();
        SetBrush(resources, "AppAccentBrush", accent.Accent);
        SetBrush(resources, "AppAccentStrongBrush", accent.AccentStrong);
        SetBrush(resources, "AppAccentSoftBrush", accent.AccentSoft);
        SetBrush(resources, "AppAccentSoftStrongBrush", accent.AccentSoftStrong);
        SetBrush(resources, "AppAccentOnAccentBrush", accent.OnAccent);

        // 设置页开关跟随同一强调色（见 PlayerViewModel.ApplyAccent）
        SetBrush(resources, "ToggleSwitchFillOn", accent.Accent);
        SetBrush(resources, "ToggleSwitchFillOnPointerOver", accent.AccentStrong);
        SetBrush(resources, "ToggleSwitchFillOnPressed", accent.AccentStrong);
        SetBrush(resources, "ToggleSwitchStrokeOn", accent.Accent);
    }

    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    private static void SetBrush(ResourceDictionary resources, string key, Brush brush)
        => resources[key] = brush;
}
