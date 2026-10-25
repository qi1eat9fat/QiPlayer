using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using QiPlayer.Classes;
using QiPlayer.Models;
using QiPlayer.ViewModels;
using Wpf.Ui.Controls;

// Wpf.Ui.Controls 也定义了 MessageBox* 类型，与 System.Windows 冲突，此处显式消歧
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace QiPlayer.Windows;

/// <summary>
/// 右栏窗口：独立的播放列表面板。
///
/// 没有系统标题栏按钮，也不允许拖动/缩放——位置与高度由 PlayerWindow 统一摆放，
/// 显隐由中栏标题栏的列表按钮控制。
///
/// 列表的右键菜单、多选、排序、删除等交互都跟着面板搬到这里：
/// 它们依赖 PlaylistBox 这个元素，而它现在属于本窗口。
/// </summary>
public partial class PlaylistWindow : Window
{
    /// <summary>与中栏共用的 ViewModel（由 PlayerWindow 注入到 DataContext）。</summary>
    private PlayerViewModel _vm => (PlayerViewModel)DataContext;

    /// <summary>已订阅属性变更的 ViewModel（DataContext 可能被重新注入）。</summary>
    private PlayerViewModel? _observed;

    public PlaylistWindow()
    {
        InitializeComponent();

        // 切歌时把列表滚到正在播放的那一行
        DataContextChanged += (_, _) => AttachViewModel();
        Closed += (_, _) => DetachViewModel();

        // 面板重新显示时补一次定位：隐藏期间切歌不会触发滚动
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) ScrollToCurrent();
        };

        Loaded += (_, _) =>
        {
            AttachViewModel();
            ScrollToCurrent();
            ApplyChrome();
            // Wpf.Ui 会在加载流程末尾再套一次系统材质，排到队列末尾补一次。
            // 本窗口 ShowActivated=False、从不激活，靠 Activated 补是补不到的。
            Dispatcher.BeginInvoke(ApplyChrome, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            UpdateCardShape();
        };

        // 卡片与背景层按 10px 圆角裁剪（窗口其余位置不绘制 → 透出桌面）
        Card.SizeChanged += (_, _) => UpdateCardShape();

        // 框架变化会让卡片在窗口里挪一两像素而尺寸不变，布局完再对一次区域与裁剪
        LayoutUpdated += (_, _) => UpdateCardShape();

        // Wpf.Ui 会在激活时重设窗口边框/材质，排到队列末尾补一次
        Activated += (_, _) => Dispatcher.BeginInvoke(
            ApplyChrome, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void ApplyChrome() => CardWindowChrome.Apply(this);

    // ---------- 跟随正在播放的曲目 ----------

    /// <summary>订阅「正在播放的曲目」：切歌时把列表滚到对应行。</summary>
    private void AttachViewModel()
    {
        DetachViewModel();

        _observed = DataContext as PlayerViewModel;
        if (_observed is not null)
            _observed.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void DetachViewModel()
    {
        if (_observed is null) return;

        _observed.PropertyChanged -= OnViewModelPropertyChanged;
        _observed = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.CurrentSong))
        {
            ScrollToCurrent();
            return;
        }

        // 清空搜索框后列表恢复全量：顺手回到正在播放的位置，
        // 否则视图会停在顶部，用户还得自己找
        if (e.PropertyName == nameof(PlayerViewModel.SearchText)
            && string.IsNullOrWhiteSpace((sender as PlayerViewModel)?.SearchText))
        {
            ScrollToCurrent();
        }
    }

    /// <summary>
    /// 把正在播放的行滚到列表中央。
    ///
    /// 先 ScrollIntoView 让虚拟化列表把这一行生成出来，
    /// 再按容器相对滚动视口的位置校正偏移——只靠 ScrollIntoView
    /// 只能保证「露出一角」，切歌时行会停在列表边缘。
    /// </summary>
    private void ScrollToCurrent()
    {
        if (!IsVisible) return;
        if ((DataContext as PlayerViewModel)?.CurrentSong is not { } song) return;
        if (PlaylistBox.Items.IndexOf(song) < 0) return;   // 被搜索过滤掉了

        // 先确保这一行被生成，随后再按「项」单位把它挪到中间
        PlaylistBox.ScrollIntoView(song);

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (FindScrollViewer(PlaylistBox) is not { } viewer) return;

            int index = PlaylistBox.Items.IndexOf(song);
            if (index < 0) return;

            if (viewer.CanContentScroll)
            {
                // 虚拟化列表的逻辑滚动：偏移与视口的单位都是「项」，
                // 不能拿像素高度去算，否则会落到完全无关的位置。
                double half = Math.Max(0, (viewer.ViewportHeight - 1) / 2);
                viewer.ScrollToVerticalOffset(Math.Max(0, index - half));
            }
            else if (PlaylistBox.ItemContainerGenerator.ContainerFromItem(song) is ListBoxItem container)
            {
                double offset = container.TranslatePoint(new Point(0, 0), viewer).Y;
                double target = viewer.VerticalOffset + offset
                                - (viewer.ViewportHeight - container.ActualHeight) / 2;
                viewer.ScrollToVerticalOffset(Math.Max(0, target));
            }

        }));
    }

    /// <summary>找到 ListBox 模板里的滚动视口。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);

            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } found) return found;
        }

        return null;
    }

    /// <summary>
    /// 圆角裁剪 + 窗口区域：卡片内容裁成圆角，圆角外那一小块从窗口里挖掉。
    ///
    /// 背景层也要裁：它是一整块不透明底色，铺满窗口时会按矩形填进卡片圆角外，
    /// 四个角就成了直角。窗口区域对这种分层窗口不起作用（挖掉的像素仍是画的），
    /// 所以圆角只能靠这一层裁剪保证。
    /// </summary>
    private void UpdateCardShape()
    {
        CardWindowChrome.ClipToRounded(Card);
        CardWindowChrome.ClipToRounded(AmbientLayer);
        CardWindowChrome.ApplyRoundedRegion(this, Card);
    }

    /// <summary>
    /// 同步三栏共用的背景几何（由 PlayerWindow 算好后写入）：
    /// 背景层据此只画「右栏」那一段，与另外两栏拼成同一张模糊封面。
    /// </summary>
    internal void SetAmbientGroup(double groupWidth, double groupHeight, double offsetX)
    {
        AmbientLayer.GroupWidth = groupWidth;
        AmbientLayer.GroupHeight = groupHeight;
        AmbientLayer.OffsetX = offsetX;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // 区域按物理像素算，缩放变了要重设
        UpdateCardShape();
    }

    /// <summary>双击列表项播放。</summary>
    private void PlaylistBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PlaylistBox.SelectedItem is SongItem song)
            _vm.PlaySelectedCommand.Execute(song);
    }

    // ---------- 右键菜单 ----------

    private void MenuPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedSong is SongItem song)
            _vm.PlaySelectedCommand.Execute(song);
    }

    private void MenuRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedSong is SongItem song)
            _vm.RemoveFromListCommand.Execute(song);
    }

    /// <summary>
    /// 在资源管理器中定位选中曲目。
    /// 用 explorer /select, 让目标文件处于选中状态，而非只打开所在目录。
    /// </summary>
    private void MenuOpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var song = _vm.SelectedSong;
        if (song is null) return;

        if (!File.Exists(song.FilePath))
        {
            MessageBox.Show(this, "文件已不存在。", "打开文件位置",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            // /select 后的路径必须带引号，否则含空格的路径会被截断
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{song.FilePath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开文件位置：{ex.Message}", "打开文件位置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 把选中曲目移入回收站。
    /// 破坏性操作：先弹确认框，且默认走回收站而非永久删除。
    /// </summary>
    private async void MenuDeleteFromDisk_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetSelectedSongs();
        if (targets.Count == 0) return;

        if (!ConfirmDelete(targets)) return;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        var (count, error) = await _vm.DeleteFromDiskAsync(targets, hwnd);

        if (error is not null)
        {
            await ShowMessageAsync("删除失败", error);
            return;
        }

        await ShowMessageAsync("已移入回收站",
            $"已删除 {count} 个文件。\n文件已移入回收站，可随时还原。");
    }

    /// <summary>
    /// 取当前选中项。右键单击时 WPF 可能尚未选中该项，
    /// 因此在选中集为空时回退到右键所在项。
    /// </summary>
    private List<SongItem> GetSelectedSongs()
    {
        var selected = PlaylistBox.SelectedItems
            .OfType<SongItem>()
            .ToList();

        if (selected.Count == 0 && _vm.SelectedSong is SongItem s)
            selected.Add(s);

        return selected;
    }

    private bool ConfirmDelete(List<SongItem> targets)
    {
        string message = targets.Count == 1
            ? $"确定要删除以下文件吗？\n\n{targets[0].FileName}\n\n文件将被移入回收站，可随时还原。"
            : $"确定要删除选中的 {targets.Count} 个文件吗？\n\n文件将被移入回收站，可随时还原。";

        var result = System.Windows.MessageBox.Show(
            this,
            message,
            "删除文件",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);   // 默认按钮为「取消」，避免误按回车

        return result == MessageBoxResult.OK;
    }

    private Task ShowMessageAsync(string title, string message)
    {
        System.Windows.MessageBox.Show(this, message, title,
            MessageBoxButton.OK, MessageBoxImage.Information);
        return Task.CompletedTask;
    }

    private void PlaylistBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 供右键菜单判断操作目标数量
        _vm.SelectionCount = PlaylistBox.SelectedItems.Count;
    }
}
