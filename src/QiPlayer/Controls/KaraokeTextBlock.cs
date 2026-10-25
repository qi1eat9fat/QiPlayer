using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using QiPlayer.ViewModels;

namespace QiPlayer.Controls;

/// <summary>
/// 左侧歌词窗口的歌词行，卡拉OK 填充形式与任务栏歌词
/// （<see cref="KaraokeSweepTextBlock"/>）保持一致：整行连续扫过。
///
/// 做法是两层同样排版的文本叠在一起：
///   · 底层为未唱色的整行；
///   · 上层为已唱色的同一行，用一个矩形裁剪，只露出左端到边界的一截。
/// 边界随播放进度从左到右连续推进，视觉上就是整行被「擦」过去。
///
/// 为什么不再用「逐字换 Foreground + 字内渐变」：
/// WPF 给文本应用 RelativeToBoundingBox 渐变时是**按每个字形 run 各映射一次**的，
/// 中英混排时拉丁字母单独成 run，渐变会在那里重新从底色开始，整行出现橙绿交替；
/// 而且逐个字换色是跳变的，不如裁剪出来的边界平滑。
///
/// 与任务栏版本的区别：这里允许换行、居中，所以裁剪几何按「当前视觉行」构造——
/// 边界线以上各行整体填充，边界所在行只填到边界 x 处；
/// 每个字的行位置与左右边界由文本指针的字符矩形得到（换行由排版自己决定）。
///
/// 非当前行、关闭卡拉OK、或该行没有逐字时间表时，只显示一层普通文本，外观与改动前一致。
/// </summary>
public sealed class KaraokeTextBlock : Grid
{
    // 主题画刷不是纯色时的兜底颜色
    private static readonly Color FallbackBaseColor = Color.FromArgb(0x73, 0x9E, 0x9E, 0x9E);
    private static readonly Color FallbackFillColor = Color.FromArgb(0xFF, 0x60, 0x9A, 0xFF);
    private static readonly SolidColorBrush FallbackBaseBrush;
    private static readonly SolidColorBrush FallbackFillBrush;

    static KaraokeTextBlock()
    {
        FallbackBaseBrush = new SolidColorBrush(FallbackBaseColor);
        FallbackBaseBrush.Freeze();
        FallbackFillBrush = new SolidColorBrush(FallbackFillColor);
        FallbackFillBrush.Freeze();
    }

    // ---------- 依赖属性 ----------

    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
        nameof(Line), typeof(LyricLineItem), typeof(KaraokeTextBlock),
        new PropertyMetadata(null, OnLineChanged));

    /// <summary>该控件对应的歌词行（整行数据，而非纯文本）。</summary>
    public LyricLineItem? Line
    {
        get => (LyricLineItem?)GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    public static readonly DependencyProperty UseTranslationProperty = DependencyProperty.Register(
        nameof(UseTranslation), typeof(bool), typeof(KaraokeTextBlock),
        new PropertyMetadata(false, OnLineChanged));

    /// <summary>
    /// 渲染该行的翻译而不是原文。
    /// 翻译没有独立的逐字时间轴，按整行起止时间均分，
    /// 因此两行能一起被同一进度擦过。
    /// </summary>
    public bool UseTranslation
    {
        get => (bool)GetValue(UseTranslationProperty);
        set => SetValue(UseTranslationProperty, value);
    }

    public static readonly DependencyProperty IsCurrentProperty = DependencyProperty.Register(
        nameof(IsCurrent), typeof(bool), typeof(KaraokeTextBlock),
        new PropertyMetadata(false, OnKaraokeStateChanged));

    /// <summary>是否为当前演唱行。只有当前行才启用扫过效果。</summary>
    public bool IsCurrent
    {
        get => (bool)GetValue(IsCurrentProperty);
        set => SetValue(IsCurrentProperty, value);
    }

    public static readonly DependencyProperty KaraokeEnabledProperty = DependencyProperty.Register(
        nameof(KaraokeEnabled), typeof(bool), typeof(KaraokeTextBlock),
        new PropertyMetadata(true, OnKaraokeStateChanged));

    /// <summary>用户设置中的总开关。</summary>
    public bool KaraokeEnabled
    {
        get => (bool)GetValue(KaraokeEnabledProperty);
        set => SetValue(KaraokeEnabledProperty, value);
    }

    public static readonly DependencyProperty PositionSecondsProperty = DependencyProperty.Register(
        nameof(PositionSeconds), typeof(double), typeof(KaraokeTextBlock),
        new PropertyMetadata(0d, OnPositionChanged));

    /// <summary>播放位置（秒），由播放器每 100ms 推送一次，用于对齐基准。</summary>
    public double PositionSeconds
    {
        get => (double)GetValue(PositionSecondsProperty);
        set => SetValue(PositionSecondsProperty, value);
    }

    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying), typeof(bool), typeof(KaraokeTextBlock),
        new PropertyMetadata(false, OnIsPlayingChanged));

    /// <summary>是否正在播放。暂停时停止帧回调，只按已知位置静态绘制。</summary>
    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public static readonly DependencyProperty BaseBrushProperty = DependencyProperty.Register(
        nameof(BaseBrush), typeof(Brush), typeof(KaraokeTextBlock),
        new PropertyMetadata(null, OnBrushChanged));

    /// <summary>未唱部分的颜色。</summary>
    public Brush? BaseBrush
    {
        get => (Brush?)GetValue(BaseBrushProperty);
        set => SetValue(BaseBrushProperty, value);
    }

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(KaraokeTextBlock),
        new PropertyMetadata(null, OnBrushChanged));

    /// <summary>已唱部分的颜色（通常为强调色）。</summary>
    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    // Grid 本身没有文本排版属性，这里补上两个转发给内部文本的依赖属性，
    // XAML 里仍然可以照旧写 TextAlignment="Center" TextWrapping="Wrap"。

    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
        nameof(TextAlignment), typeof(TextAlignment), typeof(KaraokeTextBlock),
        new PropertyMetadata(TextAlignment.Left, OnTextLayoutChanged));

    /// <summary>文本对齐方式。</summary>
    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
        nameof(TextWrapping), typeof(TextWrapping), typeof(KaraokeTextBlock),
        new PropertyMetadata(TextWrapping.NoWrap, OnTextLayoutChanged));

    /// <summary>换行方式。</summary>
    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    // ---------- 状态 ----------

    // 两层文本必须字体、字号、排版完全一致，否则边界处会错位
    private readonly TextBlock _idleText;
    private readonly TextBlock _fillText;

    // 上层文本的裁剪几何：边界线以上整体填充 + 边界所在行的已唱部分
    private readonly RectangleGeometry _doneArea = new(Rect.Empty);
    private readonly RectangleGeometry _frontArea = new(Rect.Empty);
    private readonly GeometryGroup _clip = new();

    private readonly List<Run> _runs = new();       // 逐字 Run（只为取字符位置，颜色由层决定）
    private readonly List<CharBox> _boxes = new();  // 每个字在底层文本里的位置

    private bool _active;           // 当前行 + 总开关开 + 有逐字时间表
    private bool _boxesValid;
    private double _basePosition;   // 最近一次收到位置时的播放位置
    private long _baseTimestamp;    // 收到位置时的 Stopwatch 时间戳
    private bool _renderHooked;

    public KaraokeTextBlock()
    {
        _clip.Children.Add(_doneArea);
        _clip.Children.Add(_frontArea);

        _idleText = CreateText();
        _fillText = CreateText();
        _fillText.Clip = _clip;
        _fillText.IsHitTestVisible = false;

        Children.Add(_idleText);
        Children.Add(_fillText);

        ApplyTextLayout();

        Loaded += (_, _) =>
        {
            Rebuild();
            UpdateRenderHook();
        };

        Unloaded += (_, _) => UpdateRenderHook(forceOff: true);
        IsVisibleChanged += (_, _) => UpdateRenderHook();
        SizeChanged += (_, _) =>
        {
            // 换行位置可能随宽度变化，字符矩形要重新量
            _boxesValid = false;
            UpdateVisual();
        };

        // 文本换了但尺寸没变时（行与行等宽），SizeChanged 不会触发，
        // 字符矩形必须等布局跑完再量，否则拿到的还是上一行的位置。
        _idleText.LayoutUpdated += (_, _) =>
        {
            if (_boxesValid) return;
            UpdateVisual();
        };
    }

    /// <summary>两层文本的公共排版设置（对齐与换行由 <see cref="ApplyTextLayout"/> 同步）。</summary>
    private static TextBlock CreateText() => new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Top,
    };

    // ---------- 依赖属性回调 ----------

    private static void OnLineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((KaraokeTextBlock)d).Rebuild();

    private static void OnKaraokeStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (KaraokeTextBlock)d;
        control.Rebuild();
        control.UpdateRenderHook();
    }

    private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (KaraokeTextBlock)d;
        control._basePosition = (double)e.NewValue;
        control._baseTimestamp = Stopwatch.GetTimestamp();
        control.UpdateVisual();
    }

    private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (KaraokeTextBlock)d;
        // 暂停时位置不再推进；播放时以最近一次推送的位置为准，等待下一帧插值。
        control._basePosition = control.PositionSeconds;
        control._baseTimestamp = Stopwatch.GetTimestamp();
        control.UpdateRenderHook();
        control.UpdateVisual();
    }

    private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((KaraokeTextBlock)d).ApplyBrushes();

    private static void OnTextLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((KaraokeTextBlock)d).ApplyTextLayout();

    // ---------- 构建 / 绘制 ----------

    private void ApplyTextLayout()
    {
        _idleText.TextAlignment = TextAlignment;
        _idleText.TextWrapping = TextWrapping;
        _fillText.TextAlignment = TextAlignment;
        _fillText.TextWrapping = TextWrapping;

        _boxesValid = false;
        UpdateVisual();
    }

    /// <summary>
    /// 按当前状态重建内容：当前行 + 开启卡拉OK → 两层逐字 Run；
    /// 其余情况 → 一层普通文本。
    /// </summary>
    private void Rebuild()
    {
        _runs.Clear();
        _boxes.Clear();
        _boxesValid = false;

        var line = Line;
        string content = line is null ? string.Empty : SourceText(line);
        var chars = line is null ? Array.Empty<KaraokeChar>() : SourceChars(line);

        _active = line is not null
            && IsCurrent
            && KaraokeEnabled
            && chars.Count > 0
            && !string.IsNullOrEmpty(content);

        if (line is null)
        {
            SetPlainText(_idleText, string.Empty);
            SetPlainText(_fillText, string.Empty);
        }
        else if (!_active)
        {
            SetPlainText(_idleText, content);
            SetPlainText(_fillText, string.Empty);
        }
        else
        {
            BuildRuns(chars);
        }

        ApplyBrushes();
        UpdateVisual();
        UpdateRenderHook();
    }

    /// <summary>当前渲染的是原文还是翻译。</summary>
    private string SourceText(LyricLineItem line)
        => UseTranslation ? line.Translation : line.Text;

    private IReadOnlyList<KaraokeChar> SourceChars(LyricLineItem line)
        => UseTranslation ? line.TranslationChars : line.Chars;

    /// <summary>两层都按同一批逐字 Run 排版，保证行盒与换行完全一致。</summary>
    private void BuildRuns(IReadOnlyList<KaraokeChar> chars)
    {
        _runs.Clear();
        _idleText.Inlines.Clear();
        _fillText.Inlines.Clear();

        foreach (var item in chars)
        {
            var idleRun = new Run(item.Text);
            _runs.Add(idleRun);
            _idleText.Inlines.Add(idleRun);
            _fillText.Inlines.Add(new Run(item.Text));
        }
    }

    /// <summary>设置纯文本内容（先清 Inlines，Text 与 Inlines 是同一份内容的两个入口）。</summary>
    private static void SetPlainText(TextBlock text, string value)
    {
        text.Inlines.Clear();
        text.Text = value;
    }

    /// <summary>
    /// 已唱用上层配色，未唱用底层配色；
    /// 未进入扫过状态时两层都沿用继承的前景（此时只有底层可见）。
    /// </summary>
    private void ApplyBrushes()
    {
        if (!_active)
        {
            _idleText.ClearValue(TextElement.ForegroundProperty);
            return;
        }

        // 控件自身的前景由样式给（当前行是强调色、其余行是主文本色），
        // BaseBrush/FillBrush 未设置时退到它，行为与旧实现一致。
        Brush? inherited = TextElement.GetForeground(this);

        _idleText.Foreground = BaseBrush ?? inherited ?? FallbackBaseBrush;
        _fillText.Foreground = FillBrush ?? inherited ?? FallbackFillBrush;
    }

    /// <summary>把裁剪几何推进到当前播放位置对应的位置。</summary>
    private void UpdateVisual()
    {
        var line = Line;

        if (!_active || line is null || !EnsureBoxes())
        {
            ClearClip();
            return;
        }

        var chars = SourceChars(line);

        // 尺寸取自身而不是上层的 ActualWidth：上层一旦被折叠就不再参与布局，
        // 宽度恒为 0，会陷进「先折叠 → 量到宽度 0 → 继续折叠」的死循环。
        double width = Math.Max(0, RenderSize.Width);
        double height = Math.Max(0, RenderSize.Height);
        if (width <= 0 || height <= 0)
        {
            ClearClip();
            return;
        }

        double position = EffectivePosition();

        // 已完全唱完的字数
        int filled = 0;
        while (filled < chars.Count && position >= chars[filled].EndSeconds)
            filled++;

        if (filled >= chars.Count || filled >= _boxes.Count)
        {
            // 整行唱完：上层整块覆盖
            _doneArea.Rect = new Rect(0, 0, width, height);
            _frontArea.Rect = Rect.Empty;
            return;
        }

        CharBox box = _boxes[filled];
        KaraokeChar current = chars[filled];

        // 前沿字内部按时间插值，边界连续推进而不是逐字跳
        double span = current.EndSeconds - current.StartSeconds;
        double local = span > 1e-6
            ? Math.Clamp((position - current.StartSeconds) / span, 0, 1)
            : 0;
        double edge = box.X0 + local * Math.Max(0, box.X1 - box.X0);

        // 边界线以上各行整体填充；边界所在行只填左侧一截。
        // 文字居中，行左侧的空白没有墨迹，因此 x 从 0 起即可。
        _doneArea.Rect = new Rect(0, 0, width, Math.Max(0, box.Top));
        _frontArea.Rect = new Rect(
            0,
            box.Top,
            Math.Clamp(edge, 0, width),
            Math.Max(0, box.Bottom - box.Top));
    }

    /// <summary>清空裁剪（不折叠上层：折叠会让它退出布局，尺寸永远拿不到）。</summary>
    private void ClearClip()
    {
        _doneArea.Rect = Rect.Empty;
        _frontArea.Rect = Rect.Empty;
    }

    /// <summary>
    /// 计算每个字在底层文本里的位置（含所在视觉行）。
    ///
    /// 用 TextPointer.GetCharacterRect：TextBlock 对插入点只给出零宽的矩形，
    /// 拿不到字形宽度，但「字首」与「字尾」两个插入点的 x 之差正好是本字占的宽度，
    /// 边界位置用这两个点插值即可；行顶与行高也从这里取得，换行由排版自己决定。
    /// </summary>
    private bool EnsureBoxes()
    {
        if (_boxesValid && _boxes.Count > 0) return true;
        if (_runs.Count == 0)
        {
            _boxesValid = true;
            return false;
        }

        // 还没排版时取不到矩形，等下一次 SizeChanged 再来
        if (_idleText.ActualWidth <= 0 || _idleText.ActualHeight <= 0) return false;

        _boxes.Clear();
        double fallbackHeight = _idleText.ActualHeight;

        foreach (var run in _runs)
        {
            Rect start = run.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            Rect end = run.ContentEnd.GetCharacterRect(LogicalDirection.Backward);

            // 还没完成排版时取到的是空矩形，等下一次布局回调再量
            if (start.IsEmpty || end.IsEmpty)
            {
                _boxes.Clear();
                return false;
            }

            // 结束点落到下一行时（本行行尾），边界收在这个字自己的起点上，
            // 剩下的进度由下一个字承接，避免在行尾画出一段多余的填充。
            bool sameLine = Math.Abs(end.Y - start.Y) <= 1;
            double x1 = sameLine && end.X >= start.X ? end.X : start.X;

            double height = start.Height > 0.5 ? start.Height : fallbackHeight;
            _boxes.Add(new CharBox(start.X, x1, start.Y, start.Y + height));
        }

        _boxesValid = true;
        return _boxes.Count > 0;
    }

    /// <summary>一个字的横向范围与所在行盒（相对底层文本的坐标）。</summary>
    private readonly record struct CharBox(double X0, double X1, double Top, double Bottom);

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
            && _active
            && IsPlaying
            && _runs.Count > 0;

        if (need == _renderHooked) return;

        _renderHooked = need;
        if (need)
            CompositionTarget.Rendering += OnRendering;
        else
            CompositionTarget.Rendering -= OnRendering;
    }
}
