using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using QiPlayer.Classes;
using QiPlayer.Classes.Settings;
using QiPlayer.Models;
using QiPlayer.Services;
using QiPlayer.ViewModels;
using QiPlayer.Windows;
using Wpf.Ui.Controls;

// Wpf.Ui.Controls 也定义了 MessageBox* 类型，与 System.Windows 冲突，此处显式消歧
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace QiPlayer;

public partial class PlayerWindow : FluentWindow
{
    private readonly PlayerViewModel _vm = new();

    // 副窗口调度与全局媒体键
    private readonly FlyoutService _flyoutService;
    private KeyboardHookService? _keyboardHook;

    // Explorer 重启（任务栏重建）广播消息
    private uint _wmTaskbarCreated;

    // 主窗口句柄（Explorer 重启后重建任务栏用）
    private IntPtr _hwnd;

    // 是否真的要退出（区分「关闭窗口」与「退出程序」）
    private bool _reallyExit;

    // 最小化之前的状态（普通/最大化）：从托盘还原时据此回到原样，
    // 否则「最大化 → 最小化 → 托盘打开」会被压回普通大小。
    private WindowState _stateBeforeMinimize = WindowState.Normal;

    public PlayerWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _flyoutService = new FlyoutService(_vm);

        // Slider 自己会在预览阶段把「点击轨道」标记为已处理（IsMoveToPointEnabled），
        // 普通事件绑定收不到，必须用 handledEventsToo 注册才能拿到这次点击。
        ProgressSlider.AddHandler(
            PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(ProgressSlider_PreviewMouseLeftButtonDown),
            handledEventsToo: true);

        SourceInitialized += OnSourceInitialized;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;

        // 设置面板的可见性由 ViewModel 控制；打开时同步左侧分类选中项
        _vm.PropertyChanged += (_, args) =>
        {
            // 左右两栏的显隐：窗口跟着显示/隐藏（按钮在中栏标题栏上）
            if (args.PropertyName is nameof(PlayerViewModel.ShowLyrics)
                or nameof(PlayerViewModel.IsPlaylistVisible))
            {
                // 刚打开/收起两栏：必要时把整组挪进工作区
                UpdateSideWindows(ensureGroupFits: true);
                return;
            }

            if (args.PropertyName != nameof(PlayerViewModel.IsSettingsVisible)) return;
            if (!_vm.IsSettingsVisible) return;

            // 单页布局：所有分区同屏，显示器列表在打开面板时一次填好
            if (FlyoutMonitorComboBox is not null && FlyoutMonitorComboBox.Items.Count == 0)
            {
                PopulateMonitorComboBox(FlyoutMonitorComboBox, _vm.Settings.FlyoutSelectedMonitor);
            }

            if (TaskbarMonitorComboBox is not null && TaskbarMonitorComboBox.Items.Count == 0)
            {
                PopulateMonitorComboBox(TaskbarMonitorComboBox, _vm.Settings.TaskbarWidgetSelectedMonitor);
            }
        };

        // 卡片内容按圆角裁剪（见 ClipCardToRoundedCorners）
        // 只有中栏卡片在本窗口里，左右两栏各自在自己的窗口中裁剪
        CoverCard.SizeChanged += ClipCardToRoundedCorners;

        // 窗口框架变化（激活、最大化）会让卡片在窗口里整体挪一两个像素，
        // 尺寸却不一定变，所以每次布局完都对一次窗口区域（内部会跳过没变的）
        LayoutUpdated += (_, _) => CardWindowChrome.ApplyRoundedRegion(this, CoverCard);

        // 窗口第一次激活后 DWM 才会理会边框色，激活时补一次。
        // 必须排到队列末尾：Wpf.Ui 会在自己的加载/激活流程里重设窗口边框色，
        // 直接调用会被它覆盖，窗口顶部就会留一条灰边。
        // 它同时会把窗口区域换回整窗矩形，圆角外侧又会被背景层填成直角，
        // 所以区域也要强制重设（force：缓存里的记录已经不可信）。
        Activated += (_, _) => Dispatcher.BeginInvoke(
            ApplyWindowChrome, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // 窗口尺寸/位置/状态变化：重算中栏响应式布局，并让两侧窗口跟随
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        LocationChanged += (_, _) => UpdateSideWindows();

        // 隐藏到托盘时两栏一起收起来，回到前台再按需显示（否则它们会留在桌面上）
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) UpdateSideWindows();
            else HideSideWindows();
        };
        StateChanged += (_, _) =>
        {
            // 记下最小化之前的样子；最小化本身不覆盖它
            if (WindowState != WindowState.Minimized)
                _stateBeforeMinimize = WindowState;

            UpdateResponsiveLayout();
            UpdateSideWindows();

            // 最大化会改 RootLayout 的边距，卡片在窗口里的位置随之变化，
            // 窗口区域要等布局跑完再重算
            Dispatcher.BeginInvoke(
                () => CardWindowChrome.ApplyRoundedRegion(this, CoverCard),
                System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    // ---------- 三窗口布局 ----------
    //
    // 三个面板是三个独立的顶层窗口：
    //   中栏（本窗口）带标题栏与系统按钮；
    //   左右两栏没有系统按钮，位置/高度全部由这里算好，
    //   显隐跟随 ViewModel 的 ShowLyrics / IsPlaylistVisible（中栏按钮切换）。

    /// <summary>面板之间透出桌面的缝隙（DIP）。</summary>
    private const double CardGap = 2;

    /// <summary>左栏歌词窗口的固定宽度（DIP）。</summary>
    private const double LyricsWindowWidth = 480;

    /// <summary>右栏播放列表窗口的固定宽度（DIP）。</summary>
    private const double PlaylistWindowWidth = 360;

    private LyricsWindow? _lyricsWindow;
    private PlaylistWindow? _playlistWindow;

    /// <summary>
    /// 音量行宽度已收短到约 290 DIP（歌词按钮 + 静音 + 短滑条 + 百分比 + 列表按钮），
    /// 最窄窗口（520）也放得下，因此不再按窗口宽度隐藏滑块。
    /// </summary>
    private void UpdateResponsiveLayout()
    {
        // ActualWidth 为设备无关像素（DIP），此处直接用
        double w = ActualWidth;
        if (double.IsNaN(w) || w <= 0) return;

        // 时长列属于播放列表窗口，宽度固定，始终放得下
        _vm.ShowDurationColumn = true;

        UpdateSideWindows();

        // 封面的可用宽高要等这一轮布局跑完才准（ScrollViewer 的实际宽度、去掉标题栏后的窗口高）
        Dispatcher.BeginInvoke(UpdateCoverSide, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ---------- 封面尺寸 ----------

    /// <summary>封面的边长（DIP），绑定到 CoverHost 的 MaxWidth。</summary>
    public static readonly DependencyProperty CoverSideProperty = DependencyProperty.Register(
        nameof(CoverSide), typeof(double), typeof(PlayerWindow), new PropertyMetadata(500.0));

    public double CoverSide
    {
        get => (double)GetValue(CoverSideProperty);
        set => SetValue(CoverSideProperty, value);
    }

    /// <summary>
    /// 普通窗口保持设计稿的 500；最大化时把封面放大，让它吃掉多出来的空间。
    ///
    /// 可用宽取 ScrollViewer 的实际宽度（最大化时左右两栏的位置已经在 RootLayout.Margin 里让出来了），
    /// 可用高 = 窗口高 − 标题栏 32 − 内容上下留白 40 − 封面以外的几块（信息 / 进度 / 控制 / 音量）约 348，
    /// 再留 8 的余量，免得算大了挤出竖直滚动条。
    ///
    /// 封面放大的同时，信息 / 进度 / 控制都按封面宽度铺开并整体居中（宽度绑定 CoverHost.ActualWidth）。
    /// </summary>
    private void UpdateCoverSide()
    {
        if (WindowState != WindowState.Maximized)
        {
            CoverSide = 500;
            return;
        }

        double width = NowPlayingScroll.ActualWidth - 48;    // PanePadding 左右各 24
        double height = ActualHeight - 32 - 40 - 348 - 8;

        CoverSide = Math.Clamp(Math.Min(width, height), 320, 1200);
    }

    /// <summary>
    /// 按中栏的位置与高度摆放左右两个窗口，并按需显示/隐藏。
    ///
    /// 两栏紧贴中栏、留 2px 缝隙；窗口本身不绘制的地方透出桌面，缝就是桌面。
    /// 最大化时中栏内容左右让出两栏的位置（见 RootLayout.Margin），
    /// 两栏窗口落到工作区两侧，高度与中栏一致。
    /// </summary>
    private void UpdateSideWindows(bool ensureGroupFits = false)
    {
        if (_lyricsWindow is null || _playlistWindow is null) return;
        if (double.IsNaN(Top) || double.IsNaN(Left)) return;

        // 最小化后中栏不可见，两栏也不该浮在桌面上
        if (WindowState == WindowState.Minimized)
        {
            HideSideWindows();
            return;
        }

        bool maximized = WindowState == WindowState.Maximized;
        bool showLyrics = _vm.ShowLyrics;
        bool showPlaylist = _vm.IsPlaylistVisible;
        var work = SystemParameters.WorkArea;

        // 最大化时把两栏占的宽度从内容里让出来，窗口本身仍是最大化
        RootLayout.Margin = maximized
            ? new Thickness(
                showLyrics ? LyricsWindowWidth + CardGap : 0,
                0,
                showPlaylist ? PlaylistWindowWidth + CardGap : 0,
                0)
            : new Thickness(0);

        // 打开两栏时整组会变宽，靠近屏幕边缘就把中栏挪进来（拖动时不再抢位置）
        if (ensureGroupFits && !maximized)
            EnsureGroupFits(showLyrics, showPlaylist, work);

        double top = maximized ? work.Top : Top;
        double height = maximized ? work.Height : ActualHeight;

        // 最大化窗口会被 DWM 当作不透明窗口合成，透明的地方是黑的，
        // 留 2px 缝隙会变成一条黑线。此时让两栏各宽出 2px、与中栏卡片直接相接。
        double lyricsWidth = maximized ? LyricsWindowWidth + CardGap : LyricsWindowWidth;
        double playlistWidth = maximized ? PlaylistWindowWidth + CardGap : PlaylistWindowWidth;

        if (showLyrics)
        {
            double left = maximized ? work.Left : Left - CardGap - LyricsWindowWidth;
            PlaceSideWindow(_lyricsWindow, left, top, height, lyricsWidth);
        }
        else if (_lyricsWindow.IsVisible)
        {
            _lyricsWindow.Hide();
        }

        if (showPlaylist)
        {
            double left = maximized ? work.Right - playlistWidth : Left + ActualWidth + CardGap;
            PlaceSideWindow(_playlistWindow, left, top, height, playlistWidth);
        }
        else if (_playlistWindow.IsVisible)
        {
            _playlistWindow.Hide();
        }

        UpdateAmbientGroup(maximized, showLyrics, showPlaylist, height);
    }

    /// <summary>
    /// 把三栏拼成一个矩形的坐标交给背景层：三栏各画自己那一段，
    /// 拼起来才是同一张模糊封面，而不是每栏各贴一份。
    ///
    /// 组宽 = 左栏（含缝隙）+ 中栏卡片 + 右栏（含缝隙）。
    /// 最大化时中栏窗口铺满屏幕，但卡片已经由 RootLayout.Margin 让出了两栏的位置，
    /// 所以卡片宽要扣掉左右两侧的让位。
    /// </summary>
    private void UpdateAmbientGroup(bool maximized, bool showLyrics, bool showPlaylist, double height)
    {
        double lyricsInset = showLyrics ? LyricsWindowWidth + CardGap : 0;
        double playlistInset = showPlaylist ? PlaylistWindowWidth + CardGap : 0;

        double cardWidth = ActualWidth - (maximized ? lyricsInset + playlistInset : 0);
        if (cardWidth <= 0 || height <= 0) return;

        double groupWidth = lyricsInset + cardWidth + playlistInset;

        AmbientLayer.GroupWidth = groupWidth;
        AmbientLayer.GroupHeight = height;
        AmbientLayer.OffsetX = lyricsInset;

        if (showLyrics)
            _lyricsWindow?.SetAmbientGroup(groupWidth, height, 0);

        if (showPlaylist)
        {
            _playlistWindow?.SetAmbientGroup(
                groupWidth, height, lyricsInset + cardWidth + CardGap);
        }
    }

    private void HideSideWindows()
    {
        if (_lyricsWindow is { IsVisible: true }) _lyricsWindow.Hide();
        if (_playlistWindow is { IsVisible: true }) _playlistWindow.Hide();
    }

    /// <summary>
    /// 三栏拼成一组后整体比单个窗口宽；中栏太靠边时两栏会超出屏幕，
    /// 这里把中栏整体挪进来，保证两栏完整可见。屏幕放不下整组时保持原位。
    /// </summary>
    private void EnsureGroupFits(bool showLyrics, bool showPlaylist, Rect work)
    {
        if (ActualWidth <= 0) return;

        double leftInset = showLyrics ? LyricsWindowWidth + CardGap : 0;
        double rightInset = showPlaylist ? PlaylistWindowWidth + CardGap : 0;
        if (leftInset + ActualWidth + rightInset > work.Width) return;

        double left = Left;
        if (left < work.Left + leftInset) left = work.Left + leftInset;
        if (left + ActualWidth > work.Right - rightInset) left = work.Right - rightInset - ActualWidth;

        // 赋值会触发 LocationChanged → UpdateSideWindows，二次摆放就是最终位置
        if (Math.Abs(left - Left) > 0.5) Left = left;
    }

    private static void PlaceSideWindow(Window window, double left, double top, double height, double width)
    {
        window.Width = width;
        window.Height = Math.Max(window.MinHeight, height);
        window.Left = left;
        window.Top = top;

        if (!window.IsVisible) window.Show();
    }

    /// <summary>创建左右两个窗口并挂到本窗口下（同属一个应用，一起关闭）。</summary>
    private void CreateSideWindows()
    {
        _lyricsWindow = new LyricsWindow { DataContext = _vm, Owner = this };
        _playlistWindow = new PlaylistWindow { DataContext = _vm, Owner = this };

        // 首次显示时同样校正一次，避免用户上次把窗口停在屏幕边缘
        UpdateSideWindows(ensureGroupFits: true);
    }


    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 引擎必须在窗口可见后初始化：Bass.Init 需要输出设备可用
        _vm.Initialize();

        // 托盘图标：必须先置为 Visible 再 Register，
        // 顺序反了（或保持 Collapsed）图标不会出现在托盘区。
        try
        {
            TrayIcon.Visibility = SettingsManager.Current.TrayIconHide
                ? Visibility.Collapsed
                : Visibility.Visible;

            if (!SettingsManager.Current.TrayIconHide)
            {
                TrayIcon.Register();
                App.Log("TrayIcon 注册成功");
            }
        }
        catch (Exception ex)
        {
            App.Log("TrayIcon 注册失败", ex);
        }

        RestoreWindowPlacement();

        // 左右两栏是独立窗口：位置依赖中栏的最终位置，恢复完窗口位置再建
        CreateSideWindows();

        // 关掉 Mica、DWM 边框与窗口投影（见 CardWindowChrome）：
        // 卡片圆角外侧与栏间缝隙要真正透出桌面。
        // 加载阶段先设一次，等 Wpf.Ui 的加载流程跑完再补一次（它会重设这些属性，
        // 窗口区域也一起换掉，所以补的时候连区域一并强制重设）。
        DisableSystemBackdrop();
        DisableResizeBorder();
        _ = Dispatcher.BeginInvoke(
            ApplyWindowChrome, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // 全局媒体键：直接驱动本地播放并弹出 Flyout
        // 回调来自钩子线程：用 BeginInvoke 异步转回 UI 线程，
        // 绝不能在钩子回调里同步等待 UI 线程（否则会阻塞系统按键投递）
        _keyboardHook = new KeyboardHookService(
            onPlayPause: () => Dispatcher.BeginInvoke(() => { _vm.PlayPauseCommand.Execute(null); _flyoutService.Show(); }),
            onNext: () => Dispatcher.BeginInvoke(() => { _vm.PlayNextCommand.Execute(null); _flyoutService.Show(); }),
            onPrevious: () => Dispatcher.BeginInvoke(() => { _vm.PlayPreviousCommand.Execute(null); _flyoutService.Show(); }),
            onStop: () => Dispatcher.BeginInvoke(() => { _vm.StopCommand.Execute(null); _flyoutService.Show(); }));

        // 支持从命令行直接指定音乐目录：QiPlayer.exe "D:\Music"
        // 便于快速验证，也方便日后关联「用 QiPlayer 打开文件夹」。
        var args = Environment.GetCommandLineArgs();
        string? folder = args.Length > 1 && Directory.Exists(args[1]) ? args[1] : null;

        if (folder is null)
            folder = SettingsManager.Current.LastFolder;

        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            await _vm.LoadFolderAsync(folder);
            SettingsManager.Current.LastFolder = folder;
        }
    }

    /// <summary>恢复上次的窗口位置与大小；越界时回落到默认位置。</summary>
    private void RestoreWindowPlacement()
    {
        var s = SettingsManager.Current;

        if (s.PlayerWindowWidth >= MinWidth && s.PlayerWindowHeight >= MinHeight)
        {
            Width = s.PlayerWindowWidth;
            Height = s.PlayerWindowHeight;
        }

        if (!double.IsNaN(s.PlayerWindowLeft) && !double.IsNaN(s.PlayerWindowTop))
        {
            double left = s.PlayerWindowLeft;
            double top = s.PlayerWindowTop;

            // 屏幕配置可能已变化：确认标题栏仍在可见工作区内
            bool visible = SystemParameters.VirtualScreenLeft <= left
                && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80
                && SystemParameters.VirtualScreenTop <= top
                && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80;

            if (visible)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }

        if (s.PlayerWindowState == WindowState.Maximized)
            WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// 注册 Explorer 重启广播。
    /// Explorer 重启会销毁任务栏及其子窗口，必须重建任务栏宿主。
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _wmTaskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        _hwnd = new WindowInteropHelper(this).Handle;

        if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
            source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_wmTaskbarCreated != 0 && (uint)msg == _wmTaskbarCreated)
        {
            App.Log("检测到 Explorer 重启，准备重建任务栏挂件");
            _ = RecoverTaskbarAfterExplorerRestartAsync();
        }

        return IntPtr.Zero;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // 换了显示器/缩放后工作区与窗口像素尺寸都变了，两侧窗口要重新摆放
        UpdateSideWindows();

        // 窗口区域按物理像素算，缩放变了要重设
        CardWindowChrome.ApplyRoundedRegion(this, CoverCard);
    }

    /// <summary>
    /// 把中栏卡片的内容裁成与卡片一致的 10px 圆角。
    ///
    /// Border 只把自身背景画成圆角，不会裁剪子元素：
    /// 卡片顶部的标题栏是一块矩形白底，会盖住卡片圆角，四角看上去就是直角。
    ///
    /// 背景层不裁：窗口里没绘制的像素会被合成成黑色，裁出来的圆角边缘
    /// 就成了一圈黑边。让背景层铺满整窗，圆角外的形状交给窗口区域去挖，
    /// 卡片自身的抗锯齿边缘也就落在背景层的实色上，不会发黑。
    /// </summary>
    private void ClipCardToRoundedCorners(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement card) return;

        double width = card.ActualWidth;
        double height = card.ActualHeight;
        if (width <= 0 || height <= 0) return;

        card.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 10, 10);

        // 卡片圆角外的那一小块从窗口区域里挖掉，露出桌面
        CardWindowChrome.ApplyRoundedRegion(this, card);
    }

    /// <summary>
    /// 关掉 DWM 的背景材质与窗口边框，让未绘制的区域真正透明。
    ///
    /// Mica 由 DWM 按窗口矩形整块绘制，会把卡片圆角外侧填成浅灰；
    /// 1 DIP 的窗口边框则会绕出一圈灰线。两者都由 CardWindowChrome 统一去掉。
    /// </summary>
    public void DisableSystemBackdrop() => CardWindowChrome.Apply(this);

    /// <summary>
    /// 平台样式收尾：去掉系统背景，并按卡片强制重设圆角窗口区域。
    ///
    /// 加载/激活时 Wpf.Ui 会重设窗口框架，顺带把区域覆盖成整窗矩形；
    /// 我们的缓存里还记着「已经设过」，光靠布局回调不会补回来，
    /// 圆角外就会一直是背景层的直角。所以这两步必须排到队列末尾成对执行。
    /// </summary>
    private void ApplyWindowChrome()
    {
        DisableSystemBackdrop();
        DisableResizeBorder();
        CardWindowChrome.ApplyRoundedRegion(this, CoverCard, force: true);
    }

    /// <summary>
    /// 禁止拖动窗口边缘改大小。
    ///
    /// 不能改用 ResizeMode.NoResize —— 那会把标题栏的最大化按钮一起置灰，
    /// 而最大化正是这个窗口唯一的另一种形态。这里只把 WindowChrome 的缩放热区收成 0，
    /// 按钮与双击标题栏的最大化/还原照常工作。
    ///
    /// WindowChrome 可能已被 Wpf.Ui 冻结，冻结时先复制一份再改（复制保留它原有的其它配置）。
    /// </summary>
    private void DisableResizeBorder()
    {
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (chrome is null || chrome.ResizeBorderThickness.Equals(new Thickness(0))) return;

        if (chrome.IsFrozen)
        {
            chrome = (System.Windows.Shell.WindowChrome)chrome.Clone();
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, chrome);
        }

        chrome.ResizeBorderThickness = new Thickness(0);
    }

    /// <summary>
    /// 等待任务栏重建完成后再重建挂件。
    /// 立即重建会挂到一个还不存在的任务栏上。
    /// </summary>
    private async Task RecoverTaskbarAfterExplorerRestartAsync()
    {
        for (int attempt = 0; attempt < 150; attempt++)   // 最多约 30 秒
        {
            await Task.Delay(200);

            IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero
                && NativeMethods.GetWindowRect(taskbar, out NativeMethods.RECT rect)
                && rect.Right > rect.Left
                && rect.Bottom > rect.Top)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _flyoutService.RecreateTaskbar();
                    App.Log("任务栏挂件已重建");
                });
                return;
            }
        }

        App.Log("Explorer 重启后任务栏未在超时内就绪，挂件保持关闭");
    }

    /// <summary>记录窗口位置、大小与状态。</summary>
    private void SaveWindowPlacement()
    {
        var s = SettingsManager.Current;
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;

        if (bounds.Width >= MinWidth && bounds.Height >= MinHeight)
        {
            s.PlayerWindowWidth = bounds.Width;
            s.PlayerWindowHeight = bounds.Height;
            s.PlayerWindowLeft = bounds.Left;
            s.PlayerWindowTop = bounds.Top;
        }

        s.PlayerWindowState = WindowState == WindowState.Maximized
            ? WindowState.Maximized
            : WindowState.Normal;
    }

    /// <summary>
    /// 关闭窗口时隐藏到托盘而非退出程序。
    /// 真正的退出由托盘菜单的「退出」触发（会先置 _reallyExit）。
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyExit) return;

        SaveWindowPlacement();
        e.Cancel = true;    // 阻止真正关闭，隐藏到托盘
        Hide();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SaveWindowPlacement();
        SettingsManager.SaveSettings();

        _keyboardHook?.Dispose();
        _flyoutService.Dispose();

        TrayIcon.Unregister();
        _vm.Dispose();
    }

    // ---------- 托盘菜单 ----------

    /// <summary>
    /// 左键单击托盘图标。行为由设置决定：
    /// 0 = 弹媒体 Flyout（阶段 2 接入），1 = 显示主窗口，2 = 播放/暂停。
    /// 未接入 Flyout 前，0 与 1 等价。
    /// </summary>
    private void TrayIcon_LeftClick(Wpf.Ui.Tray.Controls.NotifyIcon sender, RoutedEventArgs e)
    {
        switch (SettingsManager.Current.TrayLeftClick)
        {
            case 2:
                _vm.PlayPauseCommand.Execute(null);
                break;

            case 0:
                _flyoutService.Show(toggleMode: true);
                break;

            case 1:
            default:
                RestoreWindow();
                break;
        }
    }

    private void TrayShow_Click(object sender, RoutedEventArgs e)
        => RestoreWindow();

    private void TrayPlayPause_Click(object sender, RoutedEventArgs e)
        => _vm.PlayPauseCommand.Execute(null);

    private void TrayPrevious_Click(object sender, RoutedEventArgs e)
        => _vm.PlayPreviousCommand.Execute(null);

    private void TrayNext_Click(object sender, RoutedEventArgs e)
        => _vm.PlayNextCommand.Execute(null);

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        _reallyExit = true;

        // 先落盘再退出：设置采用防抖保存，进程结束可能来不及写
        SettingsManager.SaveSettings();

        TrayIcon.Unregister();
        Close();
        Application.Current.Shutdown();
    }

    /// <summary>从托盘（或第二实例的激活请求）恢复窗口并置于前台。</summary>
    public void RestoreFromTray() => RestoreWindow();

    private void RestoreWindow()
    {
        Show();

        // 只处理「最小化」这一种：隐藏到托盘时窗口状态本来就保留着，
        // 无条件设 Normal 会让最大化窗口一点托盘就缩回普通大小。
        if (WindowState == WindowState.Minimized)
            WindowState = _stateBeforeMinimize;

        Activate();
        Topmost = true;
        Topmost = false;   // 置顶一次再取消，确保窗口跑到前台
    }

    /// <summary>
    /// 选择文件夹并载入播放列表（含子目录）。
    /// </summary>
    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择音乐文件夹",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true) return;

        await _vm.LoadFolderAsync(dialog.FolderName);
        SettingsManager.Current.LastFolder = dialog.FolderName;
    }

    // ---------- 设置面板 ----------

    /// <summary>点击面板外的遮罩关闭设置。</summary>
    private void SettingsBackdrop_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _vm.CloseSettingsCommand.Execute(null);

    private void ResetLyricSettings_Click(object sender, RoutedEventArgs e)
    {
        _vm.Settings.LyricFontSize = 18;
        _vm.Settings.LyricLineSpacing = 14;
        _vm.Settings.LyricFontFamily = string.Empty;
    }

    /// <summary>显示器列表在展开时刷新（支持热插拔）。</summary>
    private void FlyoutMonitorComboBox_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is ComboBox comboBox)
            PopulateMonitorComboBox(comboBox, _vm.Settings.FlyoutSelectedMonitor);
    }

    private void TaskbarMonitorComboBox_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is ComboBox comboBox)
            PopulateMonitorComboBox(comboBox, _vm.Settings.TaskbarWidgetSelectedMonitor);
    }

    private void TaskbarLyricsFontComboBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // 设置页首次显示时才枚举系统字体，避免拖慢启动
        if (sender is ComboBox { IsVisible: true } comboBox)
            PopulateFontComboBox(comboBox, SettingsManager.Current.TaskbarLyricsFontFamily);
    }

    private void TaskbarLyricsFontComboBox_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is ComboBox comboBox)
            PopulateFontComboBox(comboBox, SettingsManager.Current.TaskbarLyricsFontFamily);
    }

    private void LyricFontComboBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is ComboBox { IsVisible: true } comboBox)
            PopulateFontComboBox(comboBox, SettingsManager.Current.LyricFontFamily);
    }

    private void LyricFontComboBox_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is ComboBox comboBox)
            PopulateFontComboBox(comboBox, SettingsManager.Current.LyricFontFamily);
    }

    /// <summary>
    /// 填充字体下拉框（只枚举一次）。任务栏歌词与左栏歌词各用一个实例。
    /// Tag 保存字体名，空字符串表示跟随应用字体。
    /// </summary>
    private static void PopulateFontComboBox(ComboBox comboBox, string? currentFamily)
    {
        if (comboBox.Items.Count > 0) return;

        comboBox.Items.Add(new ComboBoxItem { Content = "跟随应用字体", Tag = string.Empty });

        var names = Fonts.SystemFontFamilies
            .Select(family => family.Source)
            .Where(name => !string.IsNullOrWhiteSpace(name) && !name.StartsWith('@'))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCulture);

        foreach (string name in names)
            comboBox.Items.Add(new ComboBoxItem { Content = name, Tag = name });

        // 列表是后填的，按已保存的字体名恢复选中项
        string current = currentFamily ?? string.Empty;
        int index = 0;
        for (int i = 1; i < comboBox.Items.Count; i++)
        {
            if (comboBox.Items[i] is ComboBoxItem { Tag: string tag } && tag == current)
            {
                index = i;
                break;
            }
        }

        comboBox.SelectedIndex = index;
    }

    /// <summary>
    /// 填充显示器下拉框。
    /// 打开设置页时就要填好，否则用户看到的是空白框，无法判断当前选择。
    /// </summary>
    /// <param name="selected">当前选中的显示器索引（Flyout 与任务栏各用各的设置）。</param>
    private void PopulateMonitorComboBox(ComboBox comboBox, int selected)
    {
        comboBox.Items.Clear();

        var monitors = MonitorUtil.GetMonitors();
        for (int i = 0; i < monitors.Count; i++)
        {
            string name = string.IsNullOrWhiteSpace(monitors[i].deviceName)
                ? $"显示器 {i + 1}"
                : $"{i + 1} · {monitors[i].deviceName}";

            comboBox.Items.Add(new ComboBoxItem
            {
                Content = monitors[i].isPrimary ? $"{name}（主）" : name,
            });
        }

        if (comboBox.Items.Count == 0)
            comboBox.Items.Add(new ComboBoxItem { Content = "未检测到显示器" });

        comboBox.SelectedIndex = Math.Clamp(selected, 0, comboBox.Items.Count - 1);
    }

    /// <summary>导出当前设置为指定 XML 文件。</summary>
    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出设置",
            Filter = "QiPlayer 设置 (*.xml)|*.xml",
            FileName = "QiPlayer.settings.xml",
        };

        if (dialog.ShowDialog(this) != true) return;

        SettingsManager.SaveSettings(dialog.FileName);
    }

    /// <summary>从 XML 文件导入设置并立即生效。</summary>
    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入设置",
            Filter = "QiPlayer 设置 (*.xml)|*.xml",
        };

        if (dialog.ShowDialog(this) != true) return;

        var result = MessageBox.Show(
            this,
            "导入会覆盖当前设置，是否继续？",
            "导入设置",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);

        if (result != MessageBoxResult.OK) return;

        SettingsManager.RestoreSettings(dialog.FileName);
        SettingsManager.SaveSettings();

        MessageBox.Show(this, "设置已导入。部分选项将在重启后生效。", "导入设置",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ProgressSlider_DragStarted(object sender, DragStartedEventArgs e)
    {
        _vm.IsUserSeeking = true;
    }

    private void ProgressSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _vm.IsUserSeeking = false;

        if (sender is Slider slider)
            _vm.SeekTo(slider.Value);
    }

    /// <summary>
    /// 点击轨道（非拇指）时寻址。
    ///
    /// Slider 的 IsMoveToPointEnabled 已经在原地把 Value 改成了点击位置，
    /// 但 Value 是 OneWay 绑定、只用于显示，引擎并不知道这次跳转；
    /// 所以这里补一次真正的 seek。事件用 handledEventsToo 注册，
    /// 因为 Slider 会把这次按下标记为已处理。
    /// </summary>
    private void ProgressSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || IsThumbUnderMouse(slider)) return;

        _vm.SeekTo(slider.Value);
    }

    /// <summary>鼠标是否落在拇指上：落在拇指上交给 DragStarted / DragCompleted 处理。</summary>
    private static bool IsThumbUnderMouse(Slider slider)
        => slider.Template.FindName("PART_Track", slider) is Track track
           && track.Thumb?.IsMouseOver == true;

}
