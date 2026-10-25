using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using QiPlayer.Classes.Settings;
using Wpf.Ui.Appearance;
using static QiPlayer.Classes.NativeMethods;

namespace QiPlayer.Classes;

/// <summary>
/// 亚克力（Acrylic）背景支持。
/// 移植自 FluentFlyout，去掉 Premium 门控与已删除窗口的判定。
/// </summary>
public static class WindowBlurHelper
{
    public static void EnableBlur(Window window, uint blurOpacity = 175)
    {
        blurOpacity = Math.Clamp(
            SettingsManager.Current.AcrylicBlurOpacity == 0 ? blurOpacity : SettingsManager.Current.AcrylicBlurOpacity,
            0u, 255u);

        Apply(window, AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND, blurOpacity);
    }

    public static void DisableBlur(Window window)
        => Apply(window, AccentState.ACCENT_DISABLED, 0);

    /// <summary>调整所有已启用亚克力的窗口的不透明度。</summary>
    public static void AdjustBlurOpacityForAllWindows(uint newBlurOpacity)
    {
        newBlurOpacity = Math.Clamp(newBlurOpacity, 0u, 255u);

        foreach (Window window in Application.Current.Windows)
        {
            if (window == null) continue;
            if (new WindowInteropHelper(window).Handle == IntPtr.Zero) continue;
            if (!ShouldHaveAcrylicBlur(window)) continue;

            Apply(window, AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND, newBlurOpacity);
        }
    }

    // 阶段 0 只有主窗口与歌词设置窗口；副窗口接入后在此补充。
    private static bool ShouldHaveAcrylicBlur(Window window) => window.GetType().Name switch
    {
        nameof(PlayerWindow) => false,           // 主窗口用 Mica，不用亚克力
        "MediaFlyoutWindow" => SettingsManager.Current.MediaFlyoutAcrylicWindowEnabled,
        _ => false,
    };

    private static void Apply(Window window, AccentState state, uint opacity)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        uint backgroundColor = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light
            ? 0xF3F3F3u
            : 0x202020u;

        var accent = new AccentPolicy
        {
            AccentState = state,
            GradientColor = (opacity << 24) | (backgroundColor & 0xFFFFFF),
        };

        int size = Marshal.SizeOf(accent);
        IntPtr ptr = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(accent, ptr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                SizeOfData = size,
                Data = ptr,
            };

            SetWindowCompositionAttribute(handle, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
