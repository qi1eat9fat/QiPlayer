using System.ComponentModel;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using QiPlayer.Classes;
using QiPlayer.Classes.Settings;
using QiPlayer.ViewModels;
using Wpf.Ui.Controls;

namespace QiPlayer.Windows;

/// <summary>
/// 左栏窗口：独立的歌词面板。
///
/// 没有系统标题栏按钮，也不允许拖动/缩放——位置与高度由 PlayerWindow 统一摆放，
/// 显隐由中栏标题栏的歌词按钮控制。
///
/// 歌词滚动逻辑跟着面板一起搬到这里：原来的实现依赖窗口里的 LyricScroll/LyricItems，
/// 现在它们属于本窗口，居中计算必须在本窗口的坐标系里做。
/// </summary>
public partial class LyricsWindow : Window
{
    private PlayerViewModel? _vm;

    private int _pendingCenterIndex = -1;
    private bool _lyricCenterQueued;

    public LyricsWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => AttachViewModel();

        Loaded += (_, _) =>
        {
            ApplyChrome();
            // Wpf.Ui 会在加载流程末尾再套一次系统材质，排到队列末尾补一次
            Dispatcher.BeginInvoke(ApplyChrome, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            UpdateCardShape();
            UpdateLyricPadding();
            ApplyLyricFont();
            if (_vm is { CurrentLyricIndex: >= 0 }) RequestLyricCenter();
        };

        // 设置里改歌词字体后立即生效，不必重开窗口
        SettingsManager.Current.PropertyChanged += OnSettingsChanged;
        Closed += (_, _) => SettingsManager.Current.PropertyChanged -= OnSettingsChanged;

        // 卡片与背景层按 10px 圆角裁剪（窗口其余位置不绘制 → 透出桌面）
        Card.SizeChanged += (_, _) => UpdateCardShape();

        // 框架变化会让卡片在窗口里挪一两像素而尺寸不变，布局完再对一次区域与裁剪
        LayoutUpdated += (_, _) => UpdateCardShape();

        // 高度跟随中栏变化：视口变了要重算上下留白并重新居中
        SizeChanged += (_, _) =>
        {
            UpdateLyricPadding();
            if (_vm is { CurrentLyricIndex: >= 0 }) RequestLyricCenter();
        };

        // 显示/隐藏后视口尺寸才有效，重新居中一次
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            UpdateLyricPadding();
            if (_vm is { CurrentLyricIndex: >= 0 }) RequestLyricCenter();
        };

        // Wpf.Ui 会在激活时重设窗口边框/材质，排到队列末尾补一次
        Activated += (_, _) => Dispatcher.BeginInvoke(
            ApplyChrome, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void ApplyChrome() => CardWindowChrome.Apply(this);

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UserSettings.LyricFontFamily)) return;

        ApplyLyricFont();

        // 换字体后行高会变，当前行要重新滚回视口中央
        RequestLyricCenter();
    }

    /// <summary>
    /// 应用歌词字体；空字符串表示沿用应用默认字体。
    ///
    /// 设在 ItemsControl 上由所有歌词行继承：KaraokeTextBlock 是两层文本叠放的 Grid，
    /// 字体走 TextElement.FontFamily 一并传给内部两层，译文行也一起跟随。
    /// </summary>
    private void ApplyLyricFont()
    {
        string family = SettingsManager.Current.LyricFontFamily?.Trim() ?? string.Empty;

        if (family.Length == 0)
        {
            LyricItems.ClearValue(TextElement.FontFamilyProperty);
            return;
        }

        var font = new FontFamily(family);
        if (Equals(LyricItems.GetValue(TextElement.FontFamilyProperty), font)) return;

        LyricItems.SetValue(TextElement.FontFamilyProperty, font);
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
    /// 背景层据此只画「左栏」那一段，与另外两栏拼成同一张模糊封面。
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

    /// <summary>DataContext 由 PlayerWindow 注入，这里接上歌词换行与排版设置变化。</summary>
    private void AttachViewModel()
    {
        if (_vm is not null)
        {
            _vm.LyricLineChanged -= OnLyricLineChanged;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _vm = DataContext as PlayerViewModel;

        if (_vm is not null)
        {
            _vm.LyricLineChanged += OnLyricLineChanged;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    /// <summary>
    /// 字号或行距一变，每一行的高度都跟着变，视口中央就不再是当前行所在的位置——
    /// 歌词会看上去整体上移或下移。这里重新请求居中，让正在播放的那一行留在中央。
    ///
    /// 居中要等布局跑完才能算（见 <see cref="RequestLyricCenter"/>），
    /// 拖动滑块时连续触发也没关系，排队中的请求只会保留一个。
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.LyricFontSize)
            or nameof(PlayerViewModel.LyricLineSpacing))
        {
            RequestLyricCenter();
        }
    }

    /// <summary>
    /// 当前歌词行变化：滚动到该行并垂直居中。
    /// 延后到布局完成后再算，否则容器的实际高度还不是最新值。
    /// </summary>
    private void OnLyricLineChanged(object? sender, int index)
    {
        if (index < 0) return;

        // 记下待居中的行；布局完成后统一校正
        _pendingCenterIndex = index;
        RequestLyricCenter();
    }

    /// <summary>
    /// 请求把歌词滚动到居中位置。
    ///
    /// 不在切换行时直接滚动，而是等到布局完成 ——
    /// 因为高亮切换会改变字号（14→18），行高随之变化，
    /// 此时用旧的 ActualHeight 计算偏移会偏掉。
    /// </summary>
    private void RequestLyricCenter()
    {
        if (_vm is null) return;

        if (_lyricCenterQueued) return;
        _lyricCenterQueued = true;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                _lyricCenterQueued = false;

                // 优先用「当前正在播放的行」；没有时回退到待处理索引。
                // 窗口尺寸变化时 _pendingCenterIndex 可能已过期，
                // 直接用它会让歌词停在错误位置。
                int index = _vm.CurrentLyricIndex >= 0
                    ? _vm.CurrentLyricIndex
                    : _pendingCenterIndex;

                if (index >= 0) CenterLyricLine(index);
            }));
    }

    /// <summary>把指定歌词行滚动到视口垂直中央。</summary>
    private void CenterLyricLine(int index)
    {
        // 首末行要能居中，内容上下需各留半屏空白；此时视口尺寸才有效
        UpdateLyricPadding();

        // ItemsControl 的子项容器
        if (LyricItems.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
            return;

        var sv = LyricScroll;
        // 窗口隐藏时子项不在可视树里，TransformToAncestor 会抛 InvalidOperationException
        if (!sv.IsVisible || !container.IsDescendantOf(sv)) return;

        var transform = container.TransformToAncestor(sv);
        var viewportPos = transform.Transform(new Point(0, 0));

        // 容器在视口中的位置 → 内容坐标，再上移半个视口即为居中
        double contentTop = sv.VerticalOffset + viewportPos.Y;
        double target = contentTop + container.ActualHeight / 2 - sv.ViewportHeight / 2;

        sv.ScrollToVerticalOffset(Math.Max(0, target));
    }

    /// <summary>给歌词内容上下各留半屏空白，使首行与末行也能居中。</summary>
    private void UpdateLyricPadding()
    {
        double half = Math.Max(0, LyricScroll.ViewportHeight / 2);
        var target = new Thickness(0, half, 0, half);

        if (LyricItems.Margin.Equals(target)) return;

        LyricItems.Margin = target;

        // 留白改变内容高度，之前的偏移失效 → 重新居中
        RequestLyricCenter();
    }
}
