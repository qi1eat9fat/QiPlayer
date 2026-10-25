using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using QiPlayer.Classes;
using QiPlayer.Classes.Settings;
using QiPlayer.ViewModels;

namespace QiPlayer.Controls;

/// <summary>
/// 任务栏媒体挂件 + 单行歌词。
///
/// 布局与交互来自 FluentFlyout 的 TaskbarWidgetControl，
/// 新增歌词区（当前行，开启翻译时译文跟在原文后面），并去掉外部播放器相关逻辑。
/// </summary>
public partial class TaskbarMediaControl : UserControl
{
    private const double DefaultWidgetHeight = 40;
    private const double SmallWidgetHeight = 28;

    /// <summary>封面边长，与 XAML 里 SongImageBorder 的 Width/Height 保持一致。</summary>
    private const double CoverImageSize = 36;

    /// <summary>挂件与频谱之间、歌名区与歌词区之间的统一间距。</summary>
    public const double WidgetGap = 24;

    /// <summary>封面 + 封面与歌名区之间的间距。</summary>
    private const double CoverImageMargin = CoverImageSize + WidgetGap;

    /// <summary>
    /// 歌词区与歌名区之间的间距。比 <see cref="WidgetGap"/> 少 1：
    /// 歌名/歌词都是文字，汉字的字面留白（左右各约 1 DIP）会计入视觉间距，
    /// 减掉后与“频谱↔封面”这种实体块之间的间距看起来一样宽。
    /// </summary>
    private const double LyricGap = WidgetGap - 1;

    /// <summary>歌名/歌手区最大宽度：沿用原“固定宽度”的整体上限（240 DIP）。</summary>
    private const double TextAreaMaxWidth = 216 / 0.9 - CoverImageMargin;

    private bool _isSmallTaskbar;
    private bool _isVertical;

    private string _title = string.Empty;
    private string _artist = string.Empty;

    private double _cachedTitleWidth;
    private double _cachedArtistWidth;
    private string _cachedTitleText = string.Empty;
    private string _cachedArtistText = string.Empty;

    // 单行歌词
    private string _lyricDisplay = string.Empty;
    private string _lyricOriginal = string.Empty;
    private string? _appliedLyricFont;
    private LyricLineItem? _lyricLine;

    // 当前歌词区的实际宽度（DIP）：按这一行歌词的长度收缩，上限为设置里的最大宽度
    private double _lyricAreaWidth;

    // 普通模式长歌词的平移：整行保持自然宽度，超出视口的部分由 LyricPanel 裁剪
    private readonly TranslateTransform _lyricShift = new();
    private double _lyricScrollRange;
    private bool _lyricScrollActive;
    private double _lastPositionSeconds;

    /// <summary>普通歌词能否按进度平移：需要当前行的时间区间。</summary>
    private bool CanScrollPlainLyric
        => _lyricLine is not null && _lyricLine.EndTime > _lyricLine.Time;

    public event EventHandler? PlayPauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;

    /// <summary>
    /// 鼠标移进封面/歌名区域。宿主据此弹出媒体 Flyout：
    /// 这两个区域对应“这首歌是什么”，鼠标放上去就是想看详情。
    /// </summary>
    public event EventHandler? CoverHovered;

    /// <summary>悬停状态变化（参数：是否悬停）。宿主据此绘制覆盖挂件与频谱的统一高亮。</summary>
    public event EventHandler<bool>? HoverChanged;

    public TaskbarMediaControl()
    {
        InitializeComponent();
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

        // 歌名区、歌词区的左边距都由代码统一控制（见 WidgetGap / LyricGap），
        // 避免 XAML 里写死的数值与 CalculateSize 的计算对不上而把文字裁掉
        SongInfoStackPanel.Margin = new Thickness(WidgetGap, 0, 0, 0);
        LyricPanel.Margin = new Thickness(LyricGap, 0, 0, 0);

        // 普通模式的长歌词平移；卡拉OK 模式的平移在 KaraokeSweepTextBlock 内部完成
        LyricText.RenderTransform = _lyricShift;
    }

    // ---------- 模式 ----------

    public void SetSmallTaskbarMode(bool isSmall)
    {
        _isSmallTaskbar = isSmall;
        Height = isSmall ? SmallWidgetHeight : DefaultWidgetHeight;
    }

    public void SetVerticalMode(bool isVertical)
    {
        _isVertical = isVertical;
    }

    /// <summary>
    /// 应用系统深浅色。
    /// 挂件已去掉背景与描边（文字/图标直接浮在任务栏上），
    /// 这里保留入口，供任务栏窗口在主题切换时调用。
    /// </summary>
    public void ApplyWindowsTheme()
    {
    }

    // ---------- 内容 ----------

    public void UpdateContent(string title, string artist, BitmapSource? cover, bool isPlaying,
        string lyricText)
    {
        bool textChanged = !string.Equals(_title, title) || !string.Equals(_artist, artist);

        _title = title ?? string.Empty;
        _artist = artist ?? string.Empty;

        // 只在文本真正变化时改 TextBlock.Text。
        // 滚动中的文本是「原文 + 间隔 + 原文」的两份拷贝，
        // 封面/歌词/播放状态更新也会走到这里，若无条件重设文本，
        // 会把第二份抹掉，滚动到末尾后就只剩空白。
        if (textChanged)
        {
            RefreshTextMetrics();
            SongTitle.Text = _title;
            SongArtist.Text = _artist;
            UpdateMarquees();
        }

        if (cover is null)
        {
            SongImage.ImageSource = null;
            SongImagePlaceholder.Visibility = Visibility.Visible;
        }
        else
        {
            SongImage.ImageSource = cover;
            SongImagePlaceholder.Visibility = Visibility.Collapsed;
        }

        PlayPauseButton.Icon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = isPlaying
                ? Wpf.Ui.Controls.SymbolRegular.Pause16
                : Wpf.Ui.Controls.SymbolRegular.Play16,
            Filled = true,
        };

        UpdateLyric(lyricText);


    }

    /// <summary>
    /// 刷新任务栏单行歌词：只显示当前歌词行的原文。
    /// 任务栏不放译文——译文会把一行撑长、让卡拉OK 的扫过边界一起走过译文。
    /// 开启卡拉OK 时原文走逐字填充。
    /// </summary>
    private void UpdateLyric(string lyricText)
    {
        var settings = SettingsManager.Current;

        _lyricOriginal = (lyricText ?? string.Empty).Trim();
        string display = _lyricOriginal;

        _lyricDisplay = display;

        double fontSize = Math.Clamp(settings.TaskbarLyricsFontSize, 8, 30);
        double maxWidth = Math.Clamp(settings.TaskbarLyricsMaxWidth, 80, 1200);

        // 卡拉OK：有当前歌词行的逐字时间表才可用
        bool karaoke = settings.TaskbarLyricsEnabled
            && settings.TaskbarLyricsKaraokeEnabled
            && _lyricOriginal.Length > 0
            && _lyricLine is not null;

        ApplyLyricFont(settings.TaskbarLyricsFontFamily);

        LyricText.FontSize = fontSize;
        LyricKaraoke.FontSize = fontSize;

        LyricPanel.Visibility = settings.TaskbarLyricsEnabled && display.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!karaoke)
        {
            LyricKaraoke.Active = false;
            LyricKaraoke.Visibility = Visibility.Collapsed;

            LyricText.Visibility = Visibility.Visible;

            // 先按不限宽排版，拿到整句的自然宽度：视口宽度仍是设置里的上限，
            // 超出的部分不截断、留给平移（行尾正好露出整句）
            LyricText.MaxWidth = double.PositiveInfinity;
            LyricText.TextTrimming = TextTrimming.None;
            if (!string.Equals(LyricText.Text, display, StringComparison.Ordinal))
                LyricText.Text = display;

            // 歌词区按本行实际长度收缩，上限仍是设置里的歌词区最大宽度
            var (viewport, natural) = MeasureLyricArea(LyricText, maxWidth);
            _lyricAreaWidth = viewport;
            LyricPanel.Width = viewport;
            LyricViewport.Width = viewport;
            LyricViewport.Height = LyricText.DesiredSize.Height;

            _lyricScrollRange = natural - viewport;
            _lyricScrollActive = _lyricScrollRange > 0.5 && CanScrollPlainLyric;

            if (_lyricScrollActive)
            {
                LyricText.Width = natural;
            }
            else
            {
                // 没有行时间可用时不滚动：退回单行省略号，至少提示还有内容
                _lyricScrollRange = 0;
                LyricText.Width = double.NaN;
                LyricText.MaxWidth = viewport;
                LyricText.TextTrimming = TextTrimming.CharacterEllipsis;
                ResetPlainLyricScroll();
            }

            UpdatePlainLyricScroll();
            return;
        }

        LyricText.Visibility = Visibility.Collapsed;
        ResetPlainLyricScroll();

        LyricKaraoke.Visibility = Visibility.Visible;
        LyricKaraoke.Active = true;

        // 控件保持整句的自然宽度，视口之外由 LyricPanel 裁剪，
        // 平移在控件内部按扫过进度推进（与扫过边界同帧，不会一顿一顿）
        LyricKaraoke.MaxWidth = double.PositiveInfinity;

        // 控件文字以本行的歌词为准。逐字时间表（Line）与歌词文本来自播放器的两次
        // 属性通知，先后顺序不固定，只靠 Line 更新文字的话会量到上一行的宽度，
        // 窗口跟着变窄，这一行末尾的字就被裁掉（表现为“时不时展示不全”）。
        if (!string.Equals(LyricKaraoke.Text, display, StringComparison.Ordinal))
            LyricKaraoke.Text = display;

        // 文本刚换过时 DesiredSize 还停留在上一行的宽度，必须先清掉上一次的固定宽度、
        // 让测量失效再量，否则窗口会按上一行收缩，这一行末尾的字就被裁掉
        LyricKaraoke.Width = double.NaN;
        LyricKaraoke.InvalidateMeasure();
        LyricKaraoke.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double karaokeNatural = LyricKaraoke.DesiredSize.Width;

        _lyricAreaWidth = Math.Clamp(karaokeNatural, 40, maxWidth);
        LyricKaraoke.Width = karaokeNatural;
        LyricKaraoke.ViewportWidth = _lyricAreaWidth;
        LyricPanel.Width = _lyricAreaWidth;
        LyricViewport.Width = _lyricAreaWidth;
        LyricViewport.Height = LyricKaraoke.DesiredSize.Height;
    }

    /// <summary>
    /// 测量歌词区的视口宽度与整句的自然宽度（DIP）。
    /// 视口按内容宽度收缩，上限为设置里的歌词区最大宽度。
    /// </summary>
    private static (double viewport, double natural) MeasureLyricArea(
        FrameworkElement element, double maxWidth)
    {
        // 上一次设为可滚动的宽度必须清掉，否则量到的还是旧宽度
        element.Width = double.NaN;
        element.InvalidateMeasure();
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        double natural = element.DesiredSize.Width;
        return (Math.Clamp(natural, 40, maxWidth), natural);
    }

    /// <summary>
    /// 更新当前歌词行的逐字数据与播放位置（供卡拉OK 填充使用）。
    /// 行没变时只推进进度，避免每 100ms 重建一次 Run。
    /// </summary>
    public void UpdateLyricLine(LyricLineItem? line, double positionSeconds, bool isPlaying)
    {
        _lyricLine = line;

        if (!ReferenceEquals(LyricKaraoke.Line, line))
            LyricKaraoke.Line = line;

        UpdateLyricProgress(positionSeconds, isPlaying);
    }

    /// <summary>只推进卡拉OK 进度（播放器每 100ms 调用一次）。</summary>
    public void UpdateLyricProgress(double positionSeconds, bool isPlaying)
    {
        _lastPositionSeconds = positionSeconds;

        LyricKaraoke.PositionSeconds = positionSeconds;
        LyricKaraoke.IsPlaying = isPlaying;

        UpdatePlainLyricScroll();
    }

    // ---------- 长歌词平移 ----------

    /// <summary>
    /// 普通模式的长歌词：按本行播放进度把整行左移，行尾正好露出完整一句。
    /// 卡拉OK 模式不走这里——那里由 <see cref="KaraokeSweepTextBlock"/> 在帧回调里平移，
    /// 才能与扫过边界同步。
    /// </summary>
    private void UpdatePlainLyricScroll()
    {
        if (!_lyricScrollActive || _lyricScrollRange <= 0 || _lyricLine is null) return;

        double start = _lyricLine.Time.TotalSeconds;
        double end = _lyricLine.EndTime.TotalSeconds;
        double ratio = end > start
            ? Math.Clamp((_lastPositionSeconds - start) / (end - start), 0, 1)
            : 0;

        // 播放位置 100ms 才推一次，用一段短动画补间，避免整行一格一格地跳
        _lyricShift.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(-ratio * _lyricScrollRange, TimeSpan.FromMilliseconds(150))
            {
                FillBehavior = FillBehavior.HoldEnd,
            });
    }

    private void ResetPlainLyricScroll()
    {
        _lyricScrollActive = false;
        _lyricScrollRange = 0;
        _lyricShift.BeginAnimation(TranslateTransform.XProperty, null);
        _lyricShift.X = 0;
    }

    /// <summary>应用歌词字体；空字符串表示沿用应用的默认字体。</summary>
    private void ApplyLyricFont(string? family)
    {
        string value = family?.Trim() ?? string.Empty;
        if (string.Equals(_appliedLyricFont, value, StringComparison.Ordinal)) return;
        _appliedLyricFont = value;

        if (value.Length == 0)
        {
            LyricText.ClearValue(TextBlock.FontFamilyProperty);
            LyricKaraoke.ClearValue(TextBlock.FontFamilyProperty);
            return;
        }

        var font = new FontFamily(value);
        LyricText.FontFamily = font;
        LyricKaraoke.FontFamily = font;
    }

    public void SetControlsVisible(bool visible)
        => ControlsStackPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    // ---------- 尺寸 ----------

    /// <summary>按文本与开关计算逻辑尺寸（DIP）。</summary>
    public (double logicalWidth, double logicalHeight) CalculateSize()
    {
        var settings = SettingsManager.Current;

        RefreshTextMetricsIfNeeded();

        // 歌名/歌手区按文本长度动态收缩，上限沿用原“固定宽度”的整体宽度
        double textWidth = _isSmallTaskbar
            ? _cachedTitleWidth
            : Math.Max(_cachedTitleWidth, _cachedArtistWidth);
        textWidth = Math.Clamp(textWidth, 0, TextAreaMaxWidth);

        double logicalWidth = CoverImageMargin + textWidth;

        // 文本容器宽度只由歌名/歌手决定；歌词宽度只追加到总宽，
        // 不能算进文本区，否则标题区会把歌词挤出控件可见范围。
        double containerWidth = textWidth;
        // 首次布局时 Width 还是 NaN，NaN 与任何数比较都为 false，
        // 必须显式当作“已变化”，否则滚动动画永远不会启动。
        bool sizeChanged = double.IsNaN(SongTitleContainer.Width)
            || Math.Abs(SongTitleContainer.Width - containerWidth) > 0.5;

        SongTitleContainer.Width = containerWidth;
        SongArtistContainer.Width = containerWidth;

        if (sizeChanged) UpdateMarquees();

        // 单行歌词：接在歌名区右侧，与它保持统一间距；
        // 宽度由这一行歌词的实际长度决定（见 UpdateLyric），随换行伸缩。
        if (settings.TaskbarLyricsEnabled && _lyricDisplay.Length > 0)
            logicalWidth += LyricGap + _lyricAreaWidth;

        // 控制键
        if (settings.TaskbarWidgetControlsEnabled)
        {
            double controlsWidth = PreviousButton.Width + PlayPauseButton.Width + NextButton.Width;
            if (!_isVertical) controlsWidth += ControlsStackPanel.Margin.Left + ControlsStackPanel.Margin.Right;
            logicalWidth += controlsWidth;
        }

        double logicalHeight = _isSmallTaskbar ? SmallWidgetHeight : DefaultWidgetHeight;
        return (logicalWidth, logicalHeight);
    }

    // ---------- 文本度量 ----------

    /// <summary>
    /// 用 TextBlock 自身测量文本宽度（DIP）。
    /// 之前的 FormattedText 估算与实际渲染字体/字号存在偏差，
    /// 会把“刚好放不下”的长标题判成放得下，导致不滚动。
    /// </summary>
    private static double MeasureTextElement(TextBlock textBlock, string text)
    {
        textBlock.Text = text ?? string.Empty;
        textBlock.Width = double.NaN;
        textBlock.TextTrimming = TextTrimming.None;
        textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return textBlock.DesiredSize.Width;
    }

    private void RefreshTextMetrics()
    {
        _cachedTitleText = _title;
        _cachedArtistText = _artist;
        _cachedTitleWidth = MeasureTextElement(SongTitle, _title);
        _cachedArtistWidth = MeasureTextElement(SongArtist, _artist);
    }

    private void RefreshTextMetricsIfNeeded()
    {
        if (string.Equals(_title, _cachedTitleText, StringComparison.Ordinal)
            && string.Equals(_artist, _cachedArtistText, StringComparison.Ordinal))
            return;

        RefreshTextMetrics();
    }

    // ---------- 滚动（Marquee） ----------

    private LinearGradientBrush? _cachedTitleMask;
    private LinearGradientBrush? _cachedArtistMask;
    private double _cachedTitleMaskWidth = -1;
    private double _cachedArtistMaskWidth = -1;

    /// <summary>
    /// 重排标题与艺术家的滚动动画。
    /// 文本或容器宽度变化时调用。
    /// </summary>
    public void UpdateMarquees()
    {
        RefreshTextMetricsIfNeeded();

        double available = double.IsNaN(SongTitleContainer.Width) ? 0 : SongTitleContainer.Width;
        bool enabled = SettingsManager.Current.TaskbarWidgetScrollingEnabled;

        UpdateMarquee(SongTitle, SongTitleContainer, _cachedTitleWidth, available, enabled, isTitle: true);
        UpdateMarquee(SongArtist, SongArtistContainer, _cachedArtistWidth, available, enabled, isTitle: false);
    }

    private void UpdateMarquee(
        TextBlock textBlock, Canvas container,
        double textWidth, double availableWidth,
        bool isEnabled, bool isTitle)
    {
        if (textBlock.RenderTransform is not TranslateTransform transform) return;

        double containerWidth = container.Width;
        ref LinearGradientBrush? cachedMask = ref (isTitle ? ref _cachedTitleMask : ref _cachedArtistMask);
        ref double cachedMaskWidth = ref (isTitle ? ref _cachedTitleMaskWidth : ref _cachedArtistMaskWidth);

        bool shouldScroll = isEnabled
            && textWidth > availableWidth + 0.5
            && containerWidth > 0
            && !double.IsNaN(containerWidth);

        if (!shouldScroll)
        {
            if (cachedMask is not null)
            {
                // 清掉颜色动画，避免残留动画在遮罩隐藏后继续运行
                cachedMask.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, null);
                cachedMask.GradientStops[3].BeginAnimation(GradientStop.ColorProperty, null);
            }

            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;

            textBlock.Text = isTitle ? _title : _artist;
            textBlock.Width = containerWidth;
            textBlock.TextTrimming = TextTrimming.CharacterEllipsis;
            container.OpacityMask = null;
            return;
        }

        // 滚动时不做省略号截断
        textBlock.Width = double.NaN;
        textBlock.TextTrimming = TextTrimming.None;

        string originalText = isTitle ? _title : _artist;

        if (cachedMask is null || double.IsNaN(cachedMaskWidth) || Math.Abs(containerWidth - cachedMaskWidth) > 0.5)
        {
            // 两端各留 12px 渐隐
            double fadeFraction = Math.Min(12.0 / containerWidth, 0.5);

            var mask = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(containerWidth, 0),
                MappingMode = BrushMappingMode.Absolute,
            };

            mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.0));
            mask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), fadeFraction));
            mask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), 1.0 - fadeFraction));
            mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0));

            cachedMask = mask;
            cachedMaskWidth = containerWidth;
        }

        container.OpacityMask = cachedMask;

        int speed = Math.Max(1, SettingsManager.Current.TaskbarWidgetScrollingTextSpeed);

        if (SettingsManager.Current.TaskbarWidgetScrollingTextLoopForever)
        {
            // 循环滚动：两端渐隐常驻
            cachedMask.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, null);
            cachedMask.GradientStops[3].BeginAnimation(GradientStop.ColorProperty, null);
            cachedMask.GradientStops[0].Color = Color.FromArgb(0, 255, 255, 255);
            cachedMask.GradientStops[3].Color = Color.FromArgb(0, 255, 255, 255);

            // 用不换行空格拼接两遍，形成无缝循环
            const string spacer = "     ";
            textBlock.Text = originalText + spacer + originalText;

            double spacerWidth = StringWidth.GetStringWidth(spacer, 400);
            double scrollDistance = textWidth + spacerWidth;

            var animation = new DoubleAnimation
            {
                From = 0,
                To = -scrollDistance,
                Duration = TimeSpan.FromSeconds(scrollDistance / speed),
                RepeatBehavior = RepeatBehavior.Forever,
            };

            transform.BeginAnimation(TranslateTransform.XProperty, animation);
        }
        else
        {
            // 往返滚动：滚到末尾停 2 秒，再滚回来
            double scrollDistance = textWidth - containerWidth + 10;
            textBlock.Text = originalText;

            double durationSeconds = scrollDistance / speed;
            const double pause = 2.0;
            double tScrollEnd = pause + durationSeconds;
            double tWaitEnd = tScrollEnd + pause;
            double tScrollBackEnd = tWaitEnd + durationSeconds;
            double tTotal = tScrollBackEnd + pause;

            var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(pause))));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(-scrollDistance, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tScrollEnd))));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(-scrollDistance, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitEnd))));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tScrollBackEnd))));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tTotal))));

            // 渐隐与往返同步：滚到哪端，哪端就淡出
            var transparent = Color.FromArgb(0, 255, 255, 255);
            var solid = Color.FromArgb(255, 255, 255, 255);
            var fade = TimeSpan.FromMilliseconds(Math.Min(300, durationSeconds * 1000 / 2.0));

            var leftFade = new ColorAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            leftFade.KeyFrames.Add(new DiscreteColorKeyFrame(solid, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            leftFade.KeyFrames.Add(new LinearColorKeyFrame(solid, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(pause))));
            leftFade.KeyFrames.Add(new LinearColorKeyFrame(transparent, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(pause) + fade)));
            leftFade.KeyFrames.Add(new LinearColorKeyFrame(transparent, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tScrollBackEnd) - fade)));
            leftFade.KeyFrames.Add(new LinearColorKeyFrame(solid, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tScrollBackEnd))));
            leftFade.KeyFrames.Add(new LinearColorKeyFrame(solid, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tTotal))));

            var rightFade = new ColorAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            rightFade.KeyFrames.Add(new DiscreteColorKeyFrame(transparent, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            rightFade.KeyFrames.Add(new LinearColorKeyFrame(transparent, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tScrollEnd) - fade)));
            rightFade.KeyFrames.Add(new LinearColorKeyFrame(solid, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tScrollEnd))));
            rightFade.KeyFrames.Add(new LinearColorKeyFrame(solid, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitEnd))));
            rightFade.KeyFrames.Add(new LinearColorKeyFrame(transparent, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitEnd) + fade)));
            rightFade.KeyFrames.Add(new LinearColorKeyFrame(transparent, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tTotal))));

            cachedMask.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, leftFade);
            cachedMask.GradientStops[3].BeginAnimation(GradientStop.ColorProperty, rightFade);

            transform.BeginAnimation(TranslateTransform.XProperty, animation);
        }
    }

    // ---------- 交互 ----------

    public void SetHover(bool hovered)
    {
        // 高亮背景改由任务栏宿主窗口统一绘制：
        // 它按挂件 + 频谱的联合区域铺一层圆角底色，
        // 这样鼠标停在频谱上时也算悬停，两块不会出现两截高亮。
        HoverChanged?.Invoke(this, hovered);

        // 悬停时显示控制键
        if (SettingsManager.Current.TaskbarWidgetControlsEnabled)
            SetControlsVisible(hovered);
    }

    private bool _hovered;

    /// <summary>鼠标上一次是否在封面/歌名区域内，用来只在“进入”时触发一次。</summary>
    private bool _coverAreaHovered;

    private void MainBorder_MouseEnter(object sender, MouseEventArgs e)
    {
        _hovered = true;
        SetHover(true);
    }

    private void MainBorder_MouseLeave(object sender, MouseEventArgs e)
    {
        _hovered = false;
        _coverAreaHovered = false;
        SetHover(false);
    }

    private void MainBorder_MouseMove(object sender, MouseEventArgs e)
    {
        // 封面 + 歌名区（不含歌词区）：鼠标一移进去就请求弹出媒体 Flyout。
        // GetPosition 用的是 MainGrid 的局部坐标，0.9 的缩放不影响这里的判定。
        bool inCoverArea = e.GetPosition(MainGrid).X
            <= CoverImageMargin + SongInfoStackPanel.ActualWidth;

        if (inCoverArea == _coverAreaHovered) return;

        _coverAreaHovered = inCoverArea;
        if (inCoverArea) CoverHovered?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>当前是否处于悬停状态（控制键可见性由它决定）。</summary>
    public bool IsHovered => _hovered;

    /// <summary>
    /// 封面区左上角在屏幕上的坐标（物理像素）。媒体 Flyout 用它把左边缘对齐到封面。
    /// 挂件隐藏或还没完成布局时返回 null。
    /// </summary>
    public Point? GetCoverScreenPosition()
    {
        if (!IsVisible || ActualWidth <= 0 || SongImageBorder.ActualWidth <= 0) return null;

        try
        {
            return SongImageBorder.PointToScreen(new Point(0, 0));
        }
        catch (InvalidOperationException)
        {
            // 窗口还没接入 PresentationSource 时 PointToScreen 会抛
            return null;
        }
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
        => PreviousRequested?.Invoke(this, EventArgs.Empty);

    private void PlayPause_Click(object sender, RoutedEventArgs e)
        => PlayPauseRequested?.Invoke(this, EventArgs.Empty);

    private void Next_Click(object sender, RoutedEventArgs e)
        => NextRequested?.Invoke(this, EventArgs.Empty);
}
