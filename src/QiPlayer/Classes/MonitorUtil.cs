using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using static QiPlayer.Classes.NativeMethods;

namespace QiPlayer.Classes;

/// <summary>
/// 多显示器与 DPI 查询。
/// 移植自 FluentFlyout；Flyout 定位与任务栏挂件共用。
/// </summary>
public static class MonitorUtil
{
    public struct MonitorInfo
    {
        public Rect monitorArea;
        public Rect workArea;
        public bool isPrimary;
        public uint dpiX;
        public uint dpiY;
        public string deviceId;
        public string deviceName;
    }

    /// <summary>按设置中的索引取显示器；索引越界时回落到最近的显示器。</summary>
    public static MonitorInfo GetSelectedMonitor(int index = 0)
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0) return default;

        return monitors[Math.Clamp(index, 0, monitors.Count - 1)];
    }

    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        List<MonitorInfo> result = [];

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (hMonitor, _, ref _, _) =>
            {
                result.Add(GetMonitorInfoInternal(hMonitor));
                return true;
            },
            IntPtr.Zero);

        return result
            .OrderByDescending(m => m.isPrimary)
            .ThenBy(m => m.monitorArea.Left)
            .ToList();
    }

    public static MonitorInfo GetMonitor(Window window, MonitorFromWindowFlags flag = MonitorFromWindowFlags.DEFAULTTONEAREST)
        => GetMonitor(new WindowInteropHelper(window).Handle, flag);

    /// <summary>按窗口句柄取显示器（任务栏句柄等）。</summary>
    public static MonitorInfo GetMonitor(IntPtr handle, MonitorFromWindowFlags flag = MonitorFromWindowFlags.DEFAULTTONEAREST)
    {
        if (handle == IntPtr.Zero) return GetSelectedMonitor(0);

        return GetMonitorInfoInternal(MonitorFromWindow(handle, (int)flag));
    }

    private static MonitorInfo GetMonitorInfoInternal(IntPtr hMonitor)
    {
        var info = new MONITORINFOEX
        {
            cbSize = Marshal.SizeOf<MONITORINFOEX>(),
        };

        if (!GetMonitorInfo(hMonitor, ref info))
            return default;

        var result = new MonitorInfo
        {
            monitorArea = new Rect(
                info.rcMonitor.Left, info.rcMonitor.Top,
                info.rcMonitor.Right - info.rcMonitor.Left,
                info.rcMonitor.Bottom - info.rcMonitor.Top),
            workArea = new Rect(
                info.rcWork.Left, info.rcWork.Top,
                info.rcWork.Right - info.rcWork.Left,
                info.rcWork.Bottom - info.rcWork.Top),
            isPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
            deviceId = info.szDevice ?? string.Empty,
            deviceName = GetMonitorFriendlyName(info.szDevice),
        };

        if (GetDpiForMonitor(hMonitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY) == S_OK)
        {
            result.dpiX = dpiX;
            result.dpiY = dpiY;
        }
        else
        {
            result.dpiX = 96;
            result.dpiY = 96;
        }

        return result;
    }

    private static string GetMonitorFriendlyName(string? deviceId)
    {
        var device = new DISPLAY_DEVICE
        {
            cb = Marshal.SizeOf<DISPLAY_DEVICE>(),
        };

        if (EnumDisplayDevices(deviceId, 0, ref device, 0)
            && !string.IsNullOrWhiteSpace(device.DeviceString))
        {
            return device.DeviceString.Trim();
        }

        return "Unknown Monitor";
    }
}
