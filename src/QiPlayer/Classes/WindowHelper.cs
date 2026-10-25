using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using static QiPlayer.Classes.NativeMethods;

namespace QiPlayer.Classes;

/// <summary>
/// 窗口级 Win32 工具：置顶、显隐、不抢焦点、按物理像素定位。
/// 移植自 FluentFlyout；Flyout 与任务栏挂件共用。
/// </summary>
public static class WindowHelper
{
    /// <summary>把窗口设为置顶（比 WPF Topmost 更可靠）。</summary>
    public static void SetTopmost(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>直接显示/隐藏窗口，绕过 WPF 的延迟。</summary>
    public static void SetVisibility(Window window, bool visible)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
            | (visible ? SWP_SHOWWINDOW : SWP_HIDEWINDOW);

        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, flags);
    }

    /// <summary>读取窗口的物理像素位置（不受 WPF DPI 缩放影响）。</summary>
    public static Rect GetPlacement(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out RECT rect))
            return Rect.Empty;

        return new Rect(
            rect.Left,
            rect.Top,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top);
    }

    /// <summary>按物理像素设置窗口位置。</summary>
    public static void SetPosition(Window window, double x, double y, bool async = false)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        uint flags = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
            | (async ? SWP_ASYNCWINDOWPOS : 0u);

        SetWindowPos(handle, IntPtr.Zero, (int)x, (int)y, 0, 0, flags);
    }

    /// <summary>让窗口不抢焦点（Flyout 的关键行为）。</summary>
    public static void SetNoActivate(Window window)
    {
        window.ShowActivated = false;

        void Apply()
        {
            var helper = new WindowInteropHelper(window);
            if (helper.Handle == IntPtr.Zero) return;

            int style = GetWindowLong(helper.Handle, GWL_EXSTYLE);
            SetWindowLong(helper.Handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE);
        }

        window.SourceInitialized += (_, _) => Apply();
        Apply();
    }

    /// <summary>光标是否位于窗口内（比 WPF IsMouseOver 更可靠）。</summary>
    public static bool IsMouseOverWindow(Window window)
    {
        if (!GetCursorPos(out POINT cursor)) return false;

        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out RECT rect)) return false;

        return cursor.X >= rect.Left && cursor.X <= rect.Right
            && cursor.Y >= rect.Top && cursor.Y <= rect.Bottom;
    }
}
