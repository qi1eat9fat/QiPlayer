using QiPlayer.Classes.Settings;
using static QiPlayer.Classes.NativeMethods;

namespace QiPlayer.Classes;

/// <summary>
/// 全屏应用检测（DirectX 独占全屏）。
/// 移植自 FluentFlyout；用于在游戏/全屏视频时抑制 Flyout。
/// </summary>
internal static class FullscreenDetector
{
    public static bool IsFullscreenApplicationRunning()
    {
        if (!SettingsManager.Current.DisableIfFullscreen) return false;

        try
        {
            int result = SHQueryUserNotificationState(out var state);
            if (result != 0) return false;

            return state == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN;
        }
        catch
        {
            return false;
        }
    }
}
