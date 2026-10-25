using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QiPlayer.Classes.Settings;

namespace QiPlayer.Controls;

/// <summary>
/// 三栏共用的封面光晕背景。
///
/// 三个面板窗口各自持有一个实例，但画的是同一张图：
/// 调用方给出整组的宽高（GroupWidth / GroupHeight）和本栏在组里的横向偏移（OffsetX），
/// 这里把封面铺到「整组矩形」上，再用 Viewport 让本栏只显示自己那一块。
/// 三栏拼起来就是一张连续的模糊封面，而不是每栏各贴一份。
/// </summary>
public partial class AmbientBackground : UserControl
{
    /// <summary>三栏合计的宽度（DIP，含栏间缝隙）。</summary>
    public static readonly DependencyProperty GroupWidthProperty = DependencyProperty.Register(
        nameof(GroupWidth), typeof(double), typeof(AmbientBackground),
        new PropertyMetadata(0.0, OnGroupGeometryChanged));

    /// <summary>三栏的高度（DIP）。</summary>
    public static readonly DependencyProperty GroupHeightProperty = DependencyProperty.Register(
        nameof(GroupHeight), typeof(double), typeof(AmbientBackground),
        new PropertyMetadata(0.0, OnGroupGeometryChanged));

    /// <summary>本栏左边缘在整组里的横向偏移（DIP）。</summary>
    public static readonly DependencyProperty OffsetXProperty = DependencyProperty.Register(
        nameof(OffsetX), typeof(double), typeof(AmbientBackground),
        new PropertyMetadata(0.0, OnGroupGeometryChanged));

    public double GroupWidth
    {
        get => (double)GetValue(GroupWidthProperty);
        set => SetValue(GroupWidthProperty, value);
    }

    public double GroupHeight
    {
        get => (double)GetValue(GroupHeightProperty);
        set => SetValue(GroupHeightProperty, value);
    }

    public double OffsetX
    {
        get => (double)GetValue(OffsetXProperty);
        set => SetValue(OffsetXProperty, value);
    }

    public AmbientBackground()
    {
        InitializeComponent();

        SizeChanged += (_, _) => UpdateViewport();

        ApplyBlurSettings();
        SettingsManager.Current.PropertyChanged += OnSettingsChanged;
        Unloaded += (_, _) => SettingsManager.Current.PropertyChanged -= OnSettingsChanged;
    }

    private static void OnGroupGeometryChanged(
        DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((AmbientBackground)d).UpdateViewport();

    /// <summary>
    /// 把画刷的视口挪到「整组矩形」上：Viewport 的原点是本栏左上角，
    /// 所以横向平移 -OffsetX 之后，本栏看到的正好是整组里属于它的那一段。
    ///
    /// 组信息还没给全时（窗口刚构造）退回铺满自身，避免一帧全白/全黑。
    /// </summary>
    private void UpdateViewport()
    {
        double width = GroupWidth > 0 ? GroupWidth : ActualWidth;
        double height = GroupHeight > 0 ? GroupHeight : ActualHeight;
        if (width <= 0 || height <= 0) return;

        var viewport = new Rect(-OffsetX, 0, width, height);
        if (CoverBrush.Viewport != viewport)
            CoverBrush.Viewport = viewport;
    }

    /// <summary>
    /// 「背景高斯模糊」关闭时半径归零（BlurEffect 半径为 0 即不模糊），
    /// 开启时用「模糊半径」的设置值。拖动滑块时同样立即生效。
    /// </summary>
    private void ApplyBlurSettings()
    {
        var settings = SettingsManager.Current;

        double radius = settings.AlbumArtBackgroundBlur
            ? Math.Max(0, settings.AlbumArtBackgroundBlurRadius)
            : 0;

        if (Math.Abs(CoverBlur.Radius - radius) > 0.01)
            CoverBlur.Radius = radius;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UserSettings.AlbumArtBackgroundBlur)
            or nameof(UserSettings.AlbumArtBackgroundBlurRadius))
        {
            ApplyBlurSettings();
        }
    }
}
