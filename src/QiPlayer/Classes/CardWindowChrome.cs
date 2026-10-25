using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace QiPlayer.Classes;

/// <summary>
/// 三个面板窗口共用的窗口样式处理。
///
/// 面板都是「透明窗口 + 一张圆角卡片」：
///   · 关掉 DWM 的背景材质（Mica），否则未绘制的区域会被 Mica 填成浅灰，桌面透不出来；
///   · 关掉 DWM 的 1px 边框，否则窗口边缘会横跨卡片圆角外侧，露出一圈灰线；
///   · 收回窗口框架，否则 DWM 会画系统投影，投影糊到 2px 外就成了缝隙里的灰边。
///
/// 注意顺序：Wpf.Ui 会在自己的加载/激活流程里重新设置这些属性，
/// 所以调用方应在加载完成后、以及激活时排到 Dispatcher 队列末尾再补一次。
/// </summary>
internal static class CardWindowChrome
{
    /// <summary>去掉窗口的背景材质与边框，让未绘制的区域真正透明。</summary>
    public static void Apply(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int backdrop = NativeMethods.DWMSBT_NONE;
        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

        int border = NativeMethods.DWMWA_COLOR_NONE;
        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref border, sizeof(int));

        // 投影会糊到 2px 外的隔壁面板上，让缝隙看起来是一条灰边。
        // DWM 只在窗口有「框架」时画投影：WindowChrome 的玻璃边框、
        // 以及 Mica 背景材质把客户区扩展到框架，都会让框架存在，这里一并收回。
        if (System.Windows.Shell.WindowChrome.GetWindowChrome(window) is { } chrome)
        {
            if (chrome.GlassFrameThickness != new Thickness(0))
                chrome.GlassFrameThickness = new Thickness(0);

            if (chrome.NonClientFrameEdges != System.Windows.Shell.NonClientFrameEdges.None)
                chrome.NonClientFrameEdges = System.Windows.Shell.NonClientFrameEdges.None;

            if (chrome.UseAeroCaptionButtons)
                chrome.UseAeroCaptionButtons = false;
        }

        NativeMethods.MARGINS margins = default;
        NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

        // 框架变了要重新计算一次，投影才会立刻消失
        NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
            | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE
            | NativeMethods.SWP_FRAMECHANGED);
    }

    /// <summary>
    /// 用窗口区域把圆角外侧那一小块从窗口里「挖掉」。
    ///
    /// 区域决定窗口真正占用的像素：挖掉之后那片就是桌面，圆角才真正「漏」出来。
    /// 窗口里不透明的背景层同样要按圆角裁剪（见 <see cref="ClipToRounded"/>），
    /// 否则区域挖掉的只是可点击/可见范围，背景层照样把方角画出来。
    ///
    /// 区域边缘没有抗锯齿，所以半径按物理像素取整、紧贴卡片外缘；
    /// 宽度为 0 或区域与上次相同都不重复设置，避免拖动缩放时闪烁。
    ///
    /// 但外部（Wpf.Ui 在加载/激活流程里）可能把区域换成整窗矩形，
    /// 这时缓存会让后续调用误以为已经设好而跳过，所以这种情况要用 force 重设。
    /// </summary>
    public static void ApplyRoundedRegion(
        Window window, FrameworkElement card, double radius = 10, bool force = false)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || card.ActualWidth <= 0 || card.ActualHeight <= 0) return;

        DpiScale dpi = VisualTreeHelper.GetDpi(window);

        // 卡片在窗口内的位置：最大化时中栏要左右让出两栏，卡片不再贴着窗口边
        Point origin = card.TranslatePoint(default, window);

        int x = (int)Math.Round(origin.X * dpi.DpiScaleX);
        int y = (int)Math.Round(origin.Y * dpi.DpiScaleY);
        int w = (int)Math.Round(card.ActualWidth * dpi.DpiScaleX);
        int h = (int)Math.Round(card.ActualHeight * dpi.DpiScaleY);
        int r = (int)Math.Round(radius * dpi.DpiScaleX);

        var key = (x, y, w, h, r);
        if (!force && _appliedRegions.TryGetValue(window, out var last) && last == key) return;

        // CreateRoundRectRgn 的椭圆参数是直径，右下角坐标是开区间
        IntPtr region = NativeMethods.CreateRoundRectRgn(x, y, x + w, y + h, r * 2, r * 2);
        if (region == IntPtr.Zero) return;

        int setResult = NativeMethods.SetWindowRgn(hwnd, region, true);
        if (setResult == 0)
        {
            NativeMethods.DeleteObject(region);
            return;
        }

        // 设置成功后区域归系统所有，不能再删除
        _appliedRegions[window] = key;
    }

    /// <summary>上次设置的区域，尺寸/位置没变时跳过重复设置。</summary>
    private static readonly Dictionary<Window, (int X, int Y, int W, int H, int R)> _appliedRegions = new();

    /// <summary>
    /// 把元素的内容裁成 10px 圆角。每次布局都会调用，尺寸没变就跳过，
    /// 免得每帧重建几何体。
    /// </summary>
    public static void ClipToRounded(FrameworkElement card, double radius = 10)
    {
        double width = card.ActualWidth;
        double height = card.ActualHeight;
        if (width <= 0 || height <= 0) return;

        if (card.Clip is RectangleGeometry current
            && current.Rect.Width == width
            && current.Rect.Height == height
            && current.RadiusX == radius)
        {
            return;
        }

        card.Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
    }
}
