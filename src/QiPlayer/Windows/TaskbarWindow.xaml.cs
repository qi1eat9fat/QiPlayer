using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QiPlayer.Classes;
using QiPlayer.Classes.Settings;
using QiPlayer.Controls;
using static QiPlayer.Classes.NativeMethods;

namespace QiPlayer.Windows;

/// <summary>
/// 任务栏宿主窗口。
///
/// 移植自 FluentFlyout 的 TaskbarWindow：
///   · 把自身设为任务栏的 CHILD 窗口，从而贴合任务栏、随其显示/隐藏；
///   · 用 SetWindowRgn 只保留挂件与频谱的可见区域；
///   · 处理 DPI、横竖任务栏、小任务栏与显示器切换。
///
/// 与上游的差异：不再有 Premium 门控与外部播放器，频谱数据来自 BASS。
/// </summary>
public partial class TaskbarWindow : Window
{
    private const double SmallTaskbarThreshold = 40;
    private const double WidgetScale = 0.9;

    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _renderTimer;

    private IntPtr _lastTaskbarHandle;
    private bool _positionUpdateInProgress;
    private bool _isClosing;

    /// <summary>是否成功挂成了任务栏的子窗口；false 时用置顶窗口贴合任务栏。</summary>
    private bool _isChildHosted;

    /// <summary>挂载失败只记录一次，避免定位定时器刷日志。</summary>
    private bool _hostFailureLogged;

    private Audio.BassVisualizer? _visualizer;
    private int _barCount;
    private bool _isPlaying;

    public event EventHandler? WidgetCoverHovered;
    public event EventHandler? PlayPauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;

    /// <summary>鼠标是否停在挂件上（含控制键）。Flyout 据此在挂件悬停期间不自动隐藏。</summary>
    public bool IsWidgetHovered => Widget.IsHovered;

    /// <summary>挂件封面区左上角的屏幕坐标（物理像素），Flyout 用它对齐左边缘。</summary>
    public Point? CoverScreenPosition => Widget.GetCoverScreenPosition();

    public TaskbarWindow()
    {
        WindowHelper.SetNoActivate(this);

        InitializeComponent();

        Widget.CoverHovered += (_, _) => WidgetCoverHovered?.Invoke(this, EventArgs.Empty);
        Widget.PlayPauseRequested += (_, _) => PlayPauseRequested?.Invoke(this, EventArgs.Empty);
        Widget.NextRequested += (_, _) => NextRequested?.Invoke(this, EventArgs.Empty);
        Widget.PreviousRequested += (_, _) => PreviousRequested?.Invoke(this, EventArgs.Empty);

        // 定位刷新：显示器/任务栏尺寸变化不频繁，1.5 秒足够
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _positionTimer.Tick += (_, _) => UpdatePosition();

        // 频谱渲染：30fps，仅在可见且有流时运行
        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _renderTimer.Tick += (_, _) => RenderVisualizer();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PresentationSource.FromDependencyObject(this) is HwndSource source)
            source.AddHook(WindowProc);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WM_DPICHANGED or WM_DPICHANGED_AFTERPARENT)
        {
            Dispatcher.BeginInvoke(() =>
            {
                InvalidateMeasure();
                InvalidateArrange();
                InvalidateVisual();
                UpdateLayout();
                UpdatePosition();
            }, DispatcherPriority.Loaded);
        }

        // 阻止任务栏 UI 自动化/布局消息穿透到本窗口：
        // 某些任务栏增强工具会枚举所有子窗口，导致挂件与整个任务栏卡死。
        switch (msg)
        {
            case 0x003D: // WM_GETOBJECT
            case 0x0018: // WM_SHOWWINDOW
            case 0x0046: // WM_WINDOWPOSCHANGING
            case 0x0083: // WM_NCCALCSIZE
            case 0x0281: // WM_IME_SETCONTEXT
            case 0x0282: // WM_IME_NOTIFY
                handled = true;
                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SetupWindow();
        ApplyTheme();
        _positionTimer.Start();
        UpdateRenderTimer();

        // SetParent 会让窗口失去 WPF 的可见性管理，这里补一次原生显示
        WindowHelper.SetVisibility(this, true);
    }

    // ---------- 任务栏句柄 ----------

    /// <summary>找到选中显示器对应的任务栏窗口。</summary>
    private IntPtr GetSelectedTaskbarHandle(out bool isMainTaskbar)
    {
        var monitors = MonitorUtil.GetMonitors();
        if (monitors.Count == 0)
        {
            isMainTaskbar = true;
            return FindWindow("Shell_TrayWnd", null);
        }

        var selected = monitors[Math.Clamp(SettingsManager.Current.TaskbarWidgetSelectedMonitor, 0, monitors.Count - 1)];
        IntPtr mainHandle = FindWindow("Shell_TrayWnd", null);
        isMainTaskbar = true;

        if (MonitorUtil.GetMonitor(mainHandle).deviceId == selected.deviceId)
            return mainHandle;

        if (monitors.Count == 1)
            return mainHandle;

        // 副任务栏
        IntPtr found = IntPtr.Zero;
        var className = new StringBuilder(256);

        bool Check(IntPtr hwnd)
        {
            className.Clear();
            GetClassName(hwnd, className, className.Capacity);

            if (className.ToString() != "Shell_SecondaryTrayWnd") return false;
            if (MonitorUtil.GetMonitor(hwnd).deviceId != selected.deviceId) return false;

            found = hwnd;
            return true;
        }

        if (mainHandle != IntPtr.Zero)
        {
            uint threadId = GetWindowThreadProcessId(mainHandle, IntPtr.Zero);
            EnumThreadWindows(threadId, (hwnd, _) => !Check(hwnd), IntPtr.Zero);
        }

        if (found == IntPtr.Zero)
            EnumWindows((hwnd, _) => !Check(hwnd), IntPtr.Zero);

        if (found != IntPtr.Zero)
        {
            isMainTaskbar = false;
            return found;
        }

        return mainHandle;
    }

    // ---------- 窗口设置 ----------

    private void SetupWindow()
    {
        try
        {
            IntPtr self = new WindowInteropHelper(this).Handle;
            IntPtr taskbar = GetSelectedTaskbarHandle(out _);
            _lastTaskbarHandle = taskbar;

            TryHostInTaskbar(self, taskbar);
            CalculateAndSetPosition(taskbar, self);
        }
        catch (Exception ex)
        {
            App.Log("任务栏挂件初始化失败", ex);
        }
    }

    /// <summary>
    /// 尝试把挂件挂成任务栏的子窗口。
    /// 成功时窗口跟随任务栏显隐；失败时（部分系统会返回 ERROR_INVALID_PARAMETER）
    /// 必须把样式还原成顶层窗口，否则“WS_CHILD + 分层窗口”不会被绘制，挂件会整个消失，
    /// 之后退化为置顶窗口 + 屏幕坐标贴合任务栏。
    /// </summary>
    private void TryHostInTaskbar(IntPtr self, IntPtr taskbar)
    {
        if (taskbar == IntPtr.Zero) return;

        int originalStyle = GetWindowLong(self, GWL_STYLE);
        SetWindowLong(self, GWL_STYLE, (originalStyle & ~WS_POPUP) | WS_CHILD);

        IntPtr previous = SetParent(self, taskbar);
        int error = Marshal.GetLastWin32Error();

        // 用 GetAncestor 判断：挂件被挂成子窗口后，GetParent 也可能返回 0
        _isChildHosted = GetAncestor(self, GA_PARENT) == taskbar;

        if (_isChildHosted)
        {
            if (_hostFailureLogged)
            {
                App.Log("任务栏挂件已挂为任务栏子窗口");
                _hostFailureLogged = false;
            }
            return;
        }

        if (!_hostFailureLogged)
        {
            // 只在第一次失败时记录，之后每次定位都会再试一遍
            App.Log($"任务栏挂件未能挂为任务栏子窗口（err={error}，prev={previous}），"
                + "暂时改用置顶窗口并继续重试");
            _hostFailureLogged = true;
        }

        SetWindowLong(self, GWL_STYLE, originalStyle);
        SetWindowPos(self, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private void UpdatePosition()
    {
        if (_isClosing) return;
        if (!SettingsManager.Current.TaskbarWidgetEnabled
            && !SettingsManager.Current.TaskbarVisualizerEnabled)
        {
            Visibility = Visibility.Collapsed;
            _renderTimer.Stop();
            return;
        }

        try
        {
            IntPtr self = new WindowInteropHelper(this).Handle;
            if (self == IntPtr.Zero) return;

            IntPtr taskbar = GetSelectedTaskbarHandle(out _);

            if (taskbar != _lastTaskbarHandle)
            {
                _lastTaskbarHandle = taskbar;

                // 任务栏重建（Explorer 重启）后旧的父子关系失效，重新挂载一次
                _isChildHosted = false;
            }

            if (_isChildHosted)
            {
                // 任务栏重建（Explorer 重启）后父子关系会丢，补一次
                if (GetParent(self) != taskbar)
                    SetParent(self, taskbar);
            }
            else
            {
                // 挂载偶尔会失败（任务栏正忙时），每个定位周期重试一次
                TryHostInTaskbar(self, taskbar);
            }

            if (taskbar != IntPtr.Zero)
                CalculateAndSetPosition(taskbar, self);
        }
        catch (Exception ex)
        {
            App.Log("任务栏挂件定位失败", ex);
        }
    }

    private void CalculateAndSetPosition(IntPtr taskbar, IntPtr self)
    {
        if (_positionUpdateInProgress) return;
        _positionUpdateInProgress = true;

        try
        {
            double dpiScale = GetDpiForWindow(taskbar) / 96.0;
            if (dpiScale <= 0) return;

            GetWindowRect(taskbar, out RECT taskbarRect);

            int taskbarWidth = taskbarRect.Right - taskbarRect.Left;
            int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;

            bool isVertical = taskbarHeight > taskbarWidth;
            double crossSize = (isVertical ? taskbarWidth : taskbarHeight) / dpiScale;
            bool isSmallTaskbar = crossSize < SmallTaskbarThreshold;

            Widget.SetSmallTaskbarMode(isSmallTaskbar);
            Widget.SetVerticalMode(isVertical);

            bool isChild = _isChildHosted;

            // 独立窗口贴不住任务栏，任务栏自动隐藏或全屏应用运行时得自己让开
            if (!isChild
                && (!IsWindowVisible(taskbar) || FullscreenDetector.IsFullscreenApplicationRunning()))
            {
                if (Visibility != Visibility.Collapsed)
                {
                    Visibility = Visibility.Collapsed;
                    WindowHelper.SetVisibility(this, false);
                    UpdateRenderTimer();
                }
                return;
            }

            // 子窗口坐标相对父窗口，顶层窗口用屏幕坐标
            var origin = new POINT { X = taskbarRect.Left, Y = taskbarRect.Top };

            if (isChild)
                ScreenToClient(taskbar, ref origin);

            // 子窗口用 HWND_TOP（任务栏子窗口层级）；顶层模式必须置顶，否则会被任务栏盖住
            int insertAfter = isChild ? HWND_TOP : HWND_TOPMOST;
            SetWindowPos(self, insertAfter,
                origin.X, origin.Y,
                taskbarWidth, taskbarHeight,
                SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS | SWP_SHOWWINDOW);

            var widgetRect = PositionWidget(taskbarRect, dpiScale, isVertical);
            var visualizerRect = PositionVisualizer(taskbarRect, dpiScale, isVertical);

            UpdateWindowRegion(self, widgetRect, visualizerRect);
            PlaceVisualizerHitArea(visualizerRect, dpiScale);

            // 子窗口的显隐不能只靠 WPF Visibility：SetParent 之后必须用原生调用。
            // 同时把 WPF 的 Visibility 同步为 Visible，否则 UpdateRenderTimer 会一直认为窗口不可见。
            if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
            if (!IsVisible) Show();
            WindowHelper.SetVisibility(this, true);

            UpdateRenderTimer();
        }
        finally
        {
            _positionUpdateInProgress = false;
        }
    }

    // ---------- 悬停高亮 ----------

    /// <summary>
    /// 摆放频谱的命中层。分层窗口的完全透明像素不参与命中测试，
    /// 频谱静音时整块全透明，鼠标移上去收不到消息，所以铺一层 alpha=1 的底色。
    /// </summary>
    private void PlaceVisualizerHitArea(Rect visualizerRect, double dpiScale)
    {
        if (visualizerRect != Rect.Empty && visualizerRect.Width > 0 && dpiScale > 0)
        {
            VisualizerHitArea.Visibility = Visibility.Visible;
            Canvas.SetLeft(VisualizerHitArea, visualizerRect.Left / dpiScale);
            Canvas.SetTop(VisualizerHitArea, visualizerRect.Top / dpiScale);
            VisualizerHitArea.Width = visualizerRect.Width / dpiScale;
            VisualizerHitArea.Height = visualizerRect.Height / dpiScale;
        }
        else
        {
            VisualizerHitArea.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- 区域 ----------

    private void UpdateWindowRegion(IntPtr windowHandle, params Rect[] rects)
    {
        IntPtr region = CreateRectRgn(0, 0, 0, 0);

        foreach (var rect in rects)
        {
            if (rect == Rect.Empty) continue;

            IntPtr part = CreateRectRgn(
                (int)rect.Left, (int)rect.Top,
                (int)rect.Right, (int)rect.Bottom);

            if (part == IntPtr.Zero) continue;

            CombineRgn(region, region, part, 2 /* RGN_OR */);
            DeleteObject(part);
        }

        SetWindowRgn(windowHandle, region, true);
    }

    // ---------- 挂件定位 ----------

    private Rect PositionWidget(RECT taskbarRect, double dpiScale, bool isVertical)
    {
        if (!SettingsManager.Current.TaskbarWidgetEnabled)
            return Rect.Empty;

        var (logicalWidth, logicalHeight) = Widget.CalculateSize();

        // 向上取整：向下取整会让可见区域比内容窄不到 1 像素，右侧文字被裁掉半截
        int physicalWidth = (int)Math.Ceiling(logicalWidth * dpiScale * WidgetScale);
        int physicalHeight = (int)(logicalHeight * dpiScale);

        int taskbarWidth = taskbarRect.Right - taskbarRect.Left;
        int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;

        Widget.LayoutTransform = isVertical ? new RotateTransform(90) : null;
        Widget.RenderTransform = Transform.Identity;

        int crossSize = isVertical ? taskbarWidth : taskbarHeight;
        int crossPos = (crossSize - physicalHeight) / 2;

        // 挂件固定在任务栏靠左的位置，只保留“水平偏移”微调
        int primaryPos = 20;

        // 频谱固定在挂件左侧：把挂件右移，中间留出统一间距
        if (SettingsManager.Current.TaskbarVisualizerEnabled)
        {
            // 间距算法与 PositionVisualizer 保持一致
            primaryPos += (int)(TaskbarVisualizer.Width * dpiScale)
                + (int)(TaskbarMediaControl.WidgetGap * dpiScale);
        }

        primaryPos += SettingsManager.Current.TaskbarWidgetManualPadding;

        Canvas.SetLeft(Widget, (isVertical ? crossPos : primaryPos) / dpiScale);
        Canvas.SetTop(Widget, (isVertical ? primaryPos : crossPos) / dpiScale);

        // 布局宽度用未缩放的内容宽度：内容整体由 0.9 的 RenderTransform 缩小，
        // 若这里也乘 0.9，布局框会比内容窄一成，右侧（歌词）会被提前裁掉。
        // 可见区域仍由 physicalWidth（乘过 WidgetScale）决定，视觉上正好贴合。
        Widget.Width = logicalWidth;
        Widget.Height = physicalHeight / dpiScale;

        // 挂进任务栏之后，WPF 的布局循环不再把挂件自身的尺寸变化传下去：
        // 挂件会停在初始宽度（约等于封面宽度），歌名与歌词因此被整段裁掉。
        // 每次定位都显式测量并排列一次，保证内容始终跟挂件的实际宽度一致。
        Widget.Measure(new Size(logicalWidth, Widget.Height));
        Widget.Arrange(new Rect(
            Canvas.GetLeft(Widget), Canvas.GetTop(Widget),
            logicalWidth, Widget.Height));

        double rectWidth = isVertical ? physicalHeight : physicalWidth;
        double rectHeight = isVertical ? physicalWidth : physicalHeight;

        return new Rect(
            Canvas.GetLeft(Widget) * dpiScale,
            Canvas.GetTop(Widget) * dpiScale,
            rectWidth,
            rectHeight);
    }

    private Rect PositionVisualizer(RECT taskbarRect, double dpiScale, bool isVertical)
    {
        if (!SettingsManager.Current.TaskbarVisualizerEnabled)
        {
            TaskbarVisualizer.Visibility = Visibility.Collapsed;
            return Rect.Empty;
        }

        TaskbarVisualizer.Visibility = Visibility.Visible;
        TaskbarVisualizer.LayoutTransform = isVertical ? new RotateTransform(90) : null;

        int taskbarWidth = taskbarRect.Right - taskbarRect.Left;
        int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;

        int crossSize = isVertical ? taskbarWidth : taskbarHeight;
        int crossPos = (crossSize - (int)(TaskbarVisualizer.Height * dpiScale)) / 2
            + (isVertical ? 0 : -1);

        int primaryPos;

        if (SettingsManager.Current.TaskbarWidgetEnabled)
        {
            // 贴着挂件：位置 0 在挂件前，位置 1 在挂件后
            double widgetPrimaryStart = isVertical ? Canvas.GetTop(Widget) : Canvas.GetLeft(Widget);
            int widgetStartPx = (int)(widgetPrimaryStart * dpiScale);

            // 与挂件之间留出和“歌名区↔歌词区”一致的间距。
            // 这里不乘 WidgetScale：歌词区一侧还会算上文字本身的两侧留白，
            // 用未缩放的间距正好和它在视觉上对齐。
            int gapPx = (int)(TaskbarMediaControl.WidgetGap * dpiScale);

            // 频谱固定在挂件左侧
            primaryPos = widgetStartPx - (int)(TaskbarVisualizer.Width * dpiScale) - gapPx;
        }
        else
        {
            // 挂件关闭时频谱独立定位，与挂件同样固定在任务栏靠左
            primaryPos = 20;
        }

        Canvas.SetLeft(TaskbarVisualizer, (isVertical ? crossPos : primaryPos) / dpiScale);
        Canvas.SetTop(TaskbarVisualizer, (isVertical ? primaryPos : crossPos) / dpiScale);

        double rectWidth = isVertical
            ? TaskbarVisualizer.Height * dpiScale
            : TaskbarVisualizer.Width * dpiScale;
        double rectHeight = isVertical
            ? TaskbarVisualizer.Width * dpiScale
            : TaskbarVisualizer.Height * dpiScale;

        return new Rect(
            Canvas.GetLeft(TaskbarVisualizer) * dpiScale,
            Canvas.GetTop(TaskbarVisualizer) * dpiScale,
            rectWidth,
            rectHeight);
    }

    // ---------- 内容 ----------

    public void UpdateContent(NowPlayingState state)
    {
        var settings = SettingsManager.Current;

        // 恢复播放时重新启动频谱绘制（暂停时已停绘省 CPU）
        if (state.IsPlaying && !_isPlaying) UpdateRenderTimer();
        _isPlaying = state.IsPlaying;

        bool showWidget = settings.TaskbarWidgetEnabled;
        Widget.Visibility = showWidget ? Visibility.Visible : Visibility.Collapsed;

        if (showWidget)
        {
            // 先给出行数据（逐字时间表 + 位置），UpdateContent 再据此决定是否走卡拉OK 渲染
            Widget.UpdateLyricLine(state.LyricLine, state.PositionSeconds, state.IsPlaying);
            Widget.UpdateContent(state.Title, state.Artist, state.CoverBitmap, state.IsPlaying,
                state.LyricText);
        }

        // 无曲目且要求完全隐藏
        if (settings.TaskbarWidgetHideCompletely && !state.HasTrack)
            Visibility = Visibility.Collapsed;

        Dispatcher.BeginInvoke(UpdatePosition, DispatcherPriority.Background);
    }

    /// <summary>
    /// 推进任务栏歌词的卡拉OK 进度。
    /// 播放器每 100ms 通知一次，这里只改两个依赖属性，不做重建。
    /// </summary>
    public void UpdateLyricProgress(double positionSeconds, bool isPlaying)
        => Widget.UpdateLyricProgress(positionSeconds, isPlaying);

    public void ApplyTheme()
    {
        Widget.ApplyWindowsTheme();
    }

    public void RefreshSettings()
    {
        Widget.SetControlsVisible(
            SettingsManager.Current.TaskbarWidgetControlsEnabled && Widget.IsHovered);

        _barCount = SettingsManager.Current.TaskbarVisualizerBarCount;

        // 宽度变化需要按新分辨率重建位图，所以这里统一重建
        double visualizerWidth = SettingsManager.Current.TaskbarVisualizerWidth;
        TaskbarVisualizer.Width = visualizerWidth;

        _visualizer?.Dispose();
        _visualizer = null;

        if (SettingsManager.Current.TaskbarVisualizerEnabled)
        {
            _visualizer = new Audio.BassVisualizer(_barCount, visualizerWidth);
            TaskbarVisualizer.Source = _visualizer.Bitmap;
        }

        // 滚动开关/速度变化时，尺寸可能不变，需要显式重排滚动动画
        Widget.UpdateMarquees();

        UpdateRenderTimer();
        Dispatcher.BeginInvoke(UpdatePosition, DispatcherPriority.Background);
    }

    // ---------- 频谱 ----------

    private void UpdateRenderTimer()
    {
        bool shouldRun = SettingsManager.Current.TaskbarVisualizerEnabled
            && Visibility == Visibility.Visible;

        if (shouldRun && !_renderTimer.IsEnabled) _renderTimer.Start();
        else if (!shouldRun && _renderTimer.IsEnabled) _renderTimer.Stop();
    }

    private void RenderVisualizer()
    {
        if (_visualizer is null) return;

        _visualizer.SetStream(StreamProvider?.Invoke() ?? 0);
        _visualizer.Update();

        bool hasContent = !_visualizer.IsSilent;
        SettingsManager.Current.TaskbarVisualizerHasContent = hasContent;

        // 暂停且已完全归零时停绘，避免空转
        if (!hasContent && !_isPlaying) _renderTimer.Stop();
    }

    /// <summary>由外部注入"当前 BASS 流句柄"的读取方式。</summary>
    public Func<int>? StreamProvider { get; set; }

    protected override void OnClosed(EventArgs e)
    {
        _isClosing = true;
        _positionTimer.Stop();
        _renderTimer.Stop();
        _visualizer?.Dispose();
        base.OnClosed(e);
    }
}
