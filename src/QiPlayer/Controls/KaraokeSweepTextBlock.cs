using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using QiPlayer.ViewModels;

namespace QiPlayer.Controls;

/// <summary>
/// 整行扫过的卡拉OK 文本（参考 MusicPlayer2 的歌词效果）。
///
/// 做法是两层同样排版的文本叠在一起：
///   · 底层为未唱色（深绿）的整行；
///   · 上层为已唱色（橙）的同一行，用一个矩形裁剪，只露出左端到边界的一截。
/// 边界随播放进度从左到右推进，视觉上就是整行被“擦”过去。
///
/// 为什么不用渐变笔刷：WPF 给文本应用 RelativeToBoundingBox 渐变时，
/// 是**按每个字形 run 各映射一次**的。中英混排时（如「…只你共我 oh yeah」）
/// 拉丁字母单独成 run，渐变会在那里重新从橙色开始，整行出现橙绿交替的怪现象。
/// 裁剪是几何操作，没有这个问题。
///
/// 播放期间用 CompositionTarget.Rendering 做帧级插值：播放器位置 100ms 才更新一次，
/// 直接拿它设边界会明显一顿一顿。
/// </summary>
public sealed class KaraokeSweepTextBlock : Grid
{
    // 配色取自 MusicPlayer2 的歌词配色，但去掉了两端渐变：
    // 任务栏是浅色背景，浅绿一端会发虚，行尾几乎看不清，因此未唱统一用深绿。
    // 颜色定义在 DesignTokens.xaml，左栏歌词窗口引用同一组资源。
    private static readonly Brush IdleBrush =
        ResolvePalette("LyricKaraokeIdleBrush", Color.FromRgb(0x3E, 0x8E, 0x2E));
    private static readonly Brush FillBrush =
        ResolvePalette("LyricKaraokeFillBrush", Color.FromRgb(0xF2, 0x73, 0x0D));

    /// <summary>取设计令牌里的配色；资源尚未就绪时退回内置颜色。</summary>
    private static Brush ResolvePalette(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush) return brush;

        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }

    // 两层文本必须字体、字号、排版完全一致，否则边界处会错位
    private readonly TextBlock _idleText;
    private readonly TextBlock _fillText;
    private readonly RectangleGeometry _clip = new(new Rect(0, 0, 0, 0));

    // 歌词过长时整行随扫过进度左移（见 ViewportWidth）
    private readonly TranslateTransform _scroll = new();
    private double _lastScroll = double.NaN;

    // ---------- 依赖属性 ----------

    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
        nameof(Line), typeof(LyricLineItem), typeof(KaraokeSweepTextBlock),
        new PropertyMetadata(null, OnLineChanged));

    /// <summary>该控件对应的歌词行（用于取逐字时间表）。</summary>
    public LyricLineItem? Line
    {
        get => (LyricLineItem?)GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(
        nameof(Active), typeof(bool), typeof(KaraokeSweepTextBlock),
        new PropertyMetadata(false, OnActiveChanged));

    /// <summary>是否启用扫过效果。关闭时退化为单色文本（沿用继承的前景）。</summary>
    public bool Active
    {
        get => (bool)GetValue(ActiveProperty);
        set => SetValue(ActiveProperty, value);
    }

    public static readonly DependencyProperty PositionSecondsProperty = DependencyProperty.Register(
        nameof(PositionSeconds), typeof(double), typeof(KaraokeSweepTextBlock),
        new PropertyMetadata(0d, OnPositionChanged));

    /// <summary>播放位置（秒），由播放器每 100ms 推送一次，用于对齐基准。</summary>
    public double PositionSeconds
    {
        get => (double)GetValue(PositionSecondsProperty);
        set => SetValue(PositionSecondsProperty, value);
    }

    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying), typeof(bool), typeof(KaraokeSweepTextBlock),
        new PropertyMetadata(false, OnIsPlayingChanged));

    /// <summary>是否正在播放。暂停时停止帧回调，只按已知位置静态绘制。</summary>
    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public static readonly DependencyProperty ViewportWidthProperty = DependencyProperty.Register(
        nameof(ViewportWidth), typeof(double), typeof(KaraokeSweepTextBlock),
        new PropertyMetadata(0d, OnViewportChanged));

    /// <summary>
    /// 可见区宽度（DIP）。控件本身保持完整排版宽度，超出视口的部分由宿主容器裁剪。
    /// 设置后整行随扫过进度等比例左移，行尾时恰好露出整句；
    /// 取 0（默认）表示不滚动，只按视口裁掉多余部分。
    /// </summary>
    public double ViewportWidth
    {
        get => (double)GetValue(ViewportWidthProperty);
        set => SetValue(ViewportWidthProperty, value);
    }

    // ---------- 对外属性（与原先 TextBlock 版本的调用方保持相容） ----------

    /// <summary>歌词文本。两层同步设置。</summary>
    public string Text
    {
        get => _idleText.Text;
        set
        {
            if (string.Equals(_idleText.Text, value, StringComparison.Ordinal)) return;
            _idleText.Text = value;
            _fillText.Text = value;
            UpdateVisual();
        }
    }

    /// <summary>字号：写在控件上，由两层文本继承。</summary>
    public double FontSize
    {
        get => (double)GetValue(TextElement.FontSizeProperty);
        set => SetValue(TextElement.FontSizeProperty, value);
    }

    /// <summary>字体：写在控件上，由两层文本继承；清除后沿用上层控件（应用字体）。</summary>
    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(TextElement.FontFamilyProperty);
        set => SetValue(TextElement.FontFamilyProperty, value);
    }

    // ---------- 状态 ----------

    private double _basePosition;   // 最近一次收到位置时的播放位置
    private long _baseTimestamp;    // 收到位置时的 Stopwatch 时间戳
    private bool _renderHooked;
    private double _lastClip = -1;
    private double _lastClipHeight = -1;

    // 字符边界横向位置缓存（DIP，含 0 与行尾，共 chars.Count + 1 项）
    private readonly List<double> _bounds = new();
    private string _metricsText = string.Empty;
    private double _metricsSize = -1;
    private FontFamily? _metricsFamily;
    private FontWeight _metricsWeight;
    private FontStyle _metricsStyle;
    private FontStretch _metricsStretch;

    public KaraokeSweepTextBlock()
    {
        _idleText = CreateText();
        _idleText.Foreground = IdleBrush;

        _fillText = CreateText();
        _fillText.Foreground = FillBrush;
        _fillText.Clip = _clip;

        Children.Add(_idleText);
        Children.Add(_fillText);

        // 平移只影响绘制，不参与布局：控件宽度始终是整句的宽度
        RenderTransform = _scroll;

        Loaded += (_, _) =>
        {
            Rebuild();
            UpdateRenderHook();
        };

        Unloaded += (_, _) => UpdateRenderHook(forceOff: true);
        IsVisibleChanged += (_, _) => UpdateRenderHook();
        SizeChanged += (_, _) => UpdateVisual();
    }

    /// <summary>两层文本的公共排版设置。</summary>
    private static TextBlock CreateText() => new()
    {
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextAlignment = TextAlignment.Left,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ---------- 依赖属性回调 ----------

    private static void OnLineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((KaraokeSweepTextBlock)d).Rebuild();

    private static void OnActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (KaraokeSweepTextBlock)d;
        control.ApplyActive();
        control.Rebuild();
    }

    private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (KaraokeSweepTextBlock)d;
        control._basePosition = (double)e.NewValue;
        control._baseTimestamp = Stopwatch.GetTimestamp();
        control.UpdateVisual();
    }

    private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (KaraokeSweepTextBlock)d;
        // 暂停时位置不再推进；恢复播放时以最近一次推送的位置为准，等待下一帧插值
        control._basePosition = control.PositionSeconds;
        control._baseTimestamp = Stopwatch.GetTimestamp();
        control.UpdateRenderHook();
        control.UpdateVisual();
    }

    private static void OnViewportChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((KaraokeSweepTextBlock)d).UpdateVisual();

    // ---------- 构建 / 绘制 ----------

    /// <summary>同步文本内容。扫过模式下整行是同一段文本，不需要拆 Run。</summary>
    private void Rebuild()
    {
        string text = Line?.Text ?? string.Empty;
        if (!string.Equals(Text, text, StringComparison.Ordinal))
            Text = text;

        UpdateVisual();
        UpdateRenderHook();
    }

    /// <summary>把填充边界推进到当前播放位置对应的横向位置，并同步整行平移。</summary>
    private void UpdateVisual()
    {
        double width = ActualWidth;

        if (!Active)
        {
            SetClip(0, width);
            UpdateScroll(0);
            return;
        }

        var chars = Line?.Chars;
        if (chars is null || chars.Count == 0 || width <= 0 || string.IsNullOrEmpty(Text))
        {
            SetClip(0, width);
            UpdateScroll(0);
            return;
        }

        if (!EnsureMetrics(chars))
        {
            SetClip(0, width);
            UpdateScroll(0);
            return;
        }

        double position = EffectivePosition();

        // 已完全唱完的字数
        int filled = 0;
        while (filled < chars.Count && position >= chars[filled].EndSeconds)
            filled++;

        double total = _bounds[^1];

        // 前沿字在文本中的横向位置按字内进度插值 → 边界连续推进
        double boundary = total;
        if (filled < chars.Count)
        {
            var current = chars[filled];
            double span = current.EndSeconds - current.StartSeconds;
            double local = span > 1e-6
                ? Math.Clamp((position - current.StartSeconds) / span, 0, 1)
                : 0;

            double start = _bounds[filled];
            boundary = start + local * (_bounds[filled + 1] - start);
        }

        // 平移量按“整句进度”等比例推进：行尾时平移满，整句正好露出来。
        // 不能用“让边界贴住右边缘”的算法——那样扫过前沿会一直停在右边缘，
        // 橙色已唱区占满视口，扫过效果就看不见了。
        UpdateScroll(total > 0 ? boundary / total : 0);

        // 控件被窄容器约束（没走 ViewportWidth 平移）时，按排版宽度等比压缩边界
        double clipX = filled >= chars.Count ? width : boundary;
        if (filled < chars.Count && total > 0 && total > width)
            clipX = clipX / total * width;

        SetClip(clipX, width);
    }

    /// <summary>
    /// 按进度平移整行。视口宽度为 0 或不比排版宽度小时不平移。
    /// 每帧直接赋值即可：帧回调本身就在渲染前执行，无需再做动画。
    /// </summary>
    private void UpdateScroll(double ratio)
    {
        double width = ActualWidth;
        double viewport = ViewportWidth;
        double range = width - viewport;

        double x = range > 0.5
            ? -Math.Clamp(ratio, 0, 1) * range
            : 0;

        if (Math.Abs(x - _lastScroll) < 0.01) return;
        _lastScroll = x;
        _scroll.X = x;
    }

    /// <summary>设置上层（已唱色）文本的裁剪宽度。</summary>
    private void SetClip(double clipWidth, double width)
    {
        double w = width > 0 ? Math.Clamp(clipWidth, 0, width) : 0;
        if (w <= 0)
        {
            if (_lastClip == 0) return;
            _lastClip = 0;
            _fillText.Visibility = Visibility.Collapsed;
            return;
        }

        double h = Math.Max(0, ActualHeight);
        if (Math.Abs(w - _lastClip) < 0.01
            && Math.Abs(h - _lastClipHeight) < 0.01
            && _fillText.Visibility == Visibility.Visible)
            return;

        _lastClip = w;
        _lastClipHeight = h;
        _fillText.Visibility = Visibility.Visible;
        _clip.Rect = new Rect(0, 0, w, h);
    }

    /// <summary>
    /// 计算每个字符边界处的横向位置（DIP），结果缓存在 <see cref="_bounds"/>。
    ///
    /// 不能用 TextPointer.GetCharacterRect：TextBlock 里它只给出零宽的插入点矩形，
    /// 拿不到字形宽度，边界会退化成逐字跳动。这里改用 FormattedText 逐段排版测量。
    /// </summary>
    private bool EnsureMetrics(IReadOnlyList<KaraokeChar> chars)
    {
        string text = Text;
        if (string.IsNullOrEmpty(text)) return false;

        // 文本、字体、字号任一变化才重新测量（每次换行才做一次）
        if (_metricsText == text
            && _metricsSize == FontSize
            && Equals(_metricsFamily, FontFamily)
            && _metricsWeight == TextElement.GetFontWeight(this)
            && _metricsStyle == TextElement.GetFontStyle(this)
            && _metricsStretch == TextElement.GetFontStretch(this)
            && _bounds.Count == chars.Count + 1)
            return true;

        _metricsText = text;
        _metricsSize = FontSize;
        _metricsFamily = FontFamily;
        _metricsWeight = TextElement.GetFontWeight(this);
        _metricsStyle = TextElement.GetFontStyle(this);
        _metricsStretch = TextElement.GetFontStretch(this);

        var typeface = new Typeface(FontFamily, _metricsStyle, _metricsWeight, _metricsStretch);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        _bounds.Clear();
        _bounds.Add(0);

        int offset = 0;
        foreach (var c in chars)
        {
            offset = Math.Min(offset + c.Text.Length, text.Length);

            var formatted = new FormattedText(
                text[..offset],
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize,
                Brushes.Black,
                pixelsPerDip);

            _bounds.Add(formatted.WidthIncludingTrailingWhitespace);
        }

        return true;
    }

    /// <summary>扫过模式使用两层配色，普通模式恢复继承的前景。</summary>
    private void ApplyActive()
    {
        if (Active)
        {
            _idleText.Foreground = IdleBrush;
            _fillText.Foreground = FillBrush;
        }
        else
        {
            _idleText.ClearValue(TextElement.ForegroundProperty);
            _fillText.ClearValue(TextElement.ForegroundProperty);
        }
    }

    // ---------- 帧回调 ----------

    /// <summary>
    /// 播放位置每 100ms 才更新一次，直接用会一格一格跳。
    /// 播放期间按真实时间做线性插值，得到连续的边界位置。
    /// </summary>
    private double EffectivePosition()
    {
        if (!IsPlaying) return _basePosition;

        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - _baseTimestamp) / (double)Stopwatch.Frequency;
        return _basePosition + elapsed;
    }

    private void OnRendering(object? sender, EventArgs e) => UpdateVisual();

    private void UpdateRenderHook(bool forceOff = false)
    {
        bool need = !forceOff
            && IsLoaded
            && IsVisible
            && Active
            && IsPlaying
            && Line is not null
            && !string.IsNullOrEmpty(Text);

        if (need == _renderHooked) return;

        _renderHooked = need;
        if (need)
            CompositionTarget.Rendering += OnRendering;
        else
            CompositionTarget.Rendering -= OnRendering;
    }
}
