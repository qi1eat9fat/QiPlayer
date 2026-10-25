using Microsoft.Win32;

namespace QiPlayer.Classes;

/// <summary>
/// 开机启动注册（当前用户 Run 键）。
/// 移植自 FluentFlyout，键名改为 QiPlayer。
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QiPlayer";

    /// <summary>按设置写入或移除启动项。</summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (enabled)
            {
                string? executablePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(executablePath))
                    key.SetValue(ValueName, $"\"{executablePath}\"");
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 无注册表权限时静默忽略，不影响主程序
        }
    }
}
