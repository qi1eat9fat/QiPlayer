using System.IO;
using System.Threading;
using System.Windows;
using QiPlayer.Classes;
using QiPlayer.Classes.Settings;

namespace QiPlayer;

public partial class App : Application
{
    /// <summary>崩溃日志路径：程序目录下 crash.log（便携模式）。</summary>
    public static string CrashLogPath =>
        Path.Combine(AppContext.BaseDirectory, "crash.log");

    // 单实例：与 FluentFlyout 一致，第二个实例唤起已运行实例的主窗口
    private static readonly Mutex SingletonMutex = new(true, "QiPlayer_SingleInstance");
    private const string ActivateEventName = "QiPlayer_ActivateMainWindow";

    /// <summary>
    /// 当前进程是否为主实例。
    /// 第二个实例没有加载设置，若在退出时落盘会把默认值覆盖到真实配置上。
    /// </summary>
    private static bool _isPrimaryInstance;

    /// <summary>主窗口实例。副窗口（Flyout / 任务栏）通过它取服务。</summary>
    public static PlayerWindow? MainPlayerWindow => Current.MainWindow as PlayerWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 未处理异常统一落盘，避免窗口静默失败无从排查
        DispatcherUnhandledException += (_, args) =>
        {
            Log("DispatcherUnhandledException", args.Exception);
            args.Handled = false;   // 继续抛出，便于调试时中断
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("AppDomain.UnhandledException", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        // 第二个实例：通知已运行实例显示主窗口，然后退出
        if (!SingletonMutex.WaitOne(TimeSpan.Zero, true))
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        _isPrimaryInstance = true;

        base.OnStartup(e);

        // 设置必须在任何窗口创建前恢复（窗口会读取主题、窗口状态等）
        SettingsManager.RestoreSettings();

        // 记录关键开关的载入值：曾出现挂件被关掉却查不到来源的情况
        Log($"[设置] 载入完成：任务栏挂件={(SettingsManager.Current.TaskbarWidgetEnabled ? "开" : "关")}");

        var window = new PlayerWindow();
        MainWindow = window;
        window.Show();

        ThemeManager.ApplySavedTheme();
        StartupRegistration.Apply(SettingsManager.Current.Startup);
        StartActivationListener();
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var activateEvent = new EventWaitHandle(
                false, EventResetMode.AutoReset, ActivateEventName);
            activateEvent.Set();
        }
        catch
        {
            // 信号失败不应阻断退出
        }
    }

    /// <summary>监听第二个实例的激活请求。</summary>
    private void StartActivationListener()
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var activateEvent = new EventWaitHandle(
                    false, EventResetMode.AutoReset, ActivateEventName);

                while (true)
                {
                    activateEvent.WaitOne();
                    Dispatcher.Invoke(() => MainPlayerWindow?.RestoreFromTray());
                }
            }
            catch
            {
                // 监听线程异常时静默结束，不影响主程序
            }
        })
        {
            IsBackground = true,
            Name = "QiPlayer.ActivationListener",
        };

        thread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 只有主实例才落盘：第二实例没有加载设置，写回会覆盖真实配置
        if (_isPrimaryInstance)
        {
            // 退出前确保设置落盘（属性变更采用 500ms 防抖）
            SettingsManager.SaveSettings();

            try { SingletonMutex.ReleaseMutex(); } catch { /* 未持有 */ }
        }

        SingletonMutex.Dispose();

        base.OnExit(e);
    }

    /// <summary>写一条诊断日志（也用于非异常事件）。</summary>
    public static void Log(string message, Exception? ex = null)
    {
        try
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            if (ex is not null) line += $"\n{ex}";
            File.AppendAllText(CrashLogPath, line + "\n\n");
        }
        catch
        {
            // 日志失败不应再抛
        }
    }
}
