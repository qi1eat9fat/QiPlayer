using System.Windows;
using MicaWPF.Core.Enums;
using MicaWPF.Core.Services;
using QiPlayer.Classes.Settings;
using Wpf.Ui.Appearance;

namespace QiPlayer.Classes;

/// <summary>
/// 主题统一入口。
///
/// 主窗口（WPF-UI FluentWindow）与副窗口（MicaWPF MicaWindow）共用同一个设置值，
/// 避免两套主题各自响应系统深浅色而出现不一致。
/// </summary>
internal static class ThemeManager
{
    /// <summary>
    /// 启动时应用主题。
    ///
    /// 界面只保留浅色一套外观：主题选项已从设置页移除，
    /// 旧配置里即便写着「跟随系统」或「深色」，也一律按浅色应用。
    /// </summary>
    public static void ApplySavedTheme() => ApplyLightTheme();

    /// <summary>应用并保存主题。设置里的值只作记录，实际外观固定为浅色。</summary>
    public static void ApplyAndSaveTheme(int theme)
    {
        ApplyLightTheme();

        if (SettingsManager.Current.AppTheme != theme)
            SettingsManager.Current.AppTheme = theme;

        SettingsManager.SaveSettings();
    }

    private static void ApplyLightTheme()
    {
        Window? mainWindow = Application.Current?.MainWindow;

        // 不再跟随系统深浅色变化
        if (mainWindow is { IsLoaded: true })
            SystemThemeWatcher.UnWatch(mainWindow);

        ApplicationThemeManager.Apply(ApplicationTheme.Light);
        MicaWPFServiceUtility.ThemeService.ChangeTheme(WindowsTheme.Light);

        MicaWPFServiceUtility.AccentColorService.RefreshAccentsColors();

        // 自定义语义色（卡片底、描边、正文层次）与强调色都随主题重算，
        // 否则切换主题后卡片仍是旧主题的半透明黑/白。
        AppThemeStyler.Apply();

        // ApplicationThemeManager 会重新给主窗口套上 Mica 与边框色，这里再关一次，
        // 否则切主题后栏间缝隙又会被背景材质填平、顶部又冒出灰边。
        // 排到队列末尾，避免被它随后的主题流程覆盖。
        if (mainWindow is PlayerWindow playerWindow)
        {
            playerWindow.DisableSystemBackdrop();
            mainWindow.Dispatcher.BeginInvoke(
                playerWindow.DisableSystemBackdrop,
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }
}
