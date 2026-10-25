using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using QiPlayer.Services;

namespace QiPlayer.ViewModels;

/// <summary>
/// 卡拉OK逐字填充的最小单元：一个可显示字符（含组合字符）及其演唱时间区间。
/// 时间均为相对歌曲开始的秒数。
/// </summary>
public sealed class KaraokeChar
{
    public string Text { get; init; } = string.Empty;

    public double StartSeconds { get; init; }

    public double EndSeconds { get; init; }
}

/// <summary>
/// 滚动歌词列表中的一行。
///
/// 之所以不直接绑定 <see cref="Services.LyricLine"/>：
/// 需要 IsCurrent 随播放进度变化并驱动界面高亮，
/// 而这是视图状态，不应污染解析层的数据模型。
///
/// 卡拉OK效果所需的字符时间表按需构建（<see cref="Chars"/>）：
/// 只有当前行才会被访问，避免为整首歌词预先分配大量对象。
/// </summary>
public sealed class LyricLineItem : INotifyPropertyChanged
{
    private readonly IReadOnlyList<TimeSpan> _wordTimes;
    private readonly IReadOnlyList<int> _wordSplit;
    private IReadOnlyList<KaraokeChar>? _chars;

    public LyricLineItem(LyricLine line, TimeSpan endTime)
    {
        Text = line.Text;
        Translation = line.Translation;
        Time = line.Time;
        EndTime = endTime;
        _wordTimes = line.WordTimes;
        _wordSplit = line.WordSplit;
    }

    public string Text { get; }

    public string Translation { get; }

    public bool HasTranslation => !string.IsNullOrEmpty(Translation);

    /// <summary>该行起始时间。</summary>
    public TimeSpan Time { get; }

    /// <summary>
    /// 该行结束时间（下一行开始，或末行的推定结束）。
    /// 普通 lrc 用它把整行匀速填满；逐字歌词只作兜底。
    /// </summary>
    public TimeSpan EndTime { get; }

    /// <summary>
    /// 逐字时间表。逐字歌词按 &lt;mm:ss.xx&gt; 标签分段，
    /// 普通歌词则整行从头到尾匀速分配。
    /// </summary>
    public IReadOnlyList<KaraokeChar> Chars => _chars ??= BuildChars();

    /// <summary>
    /// 翻译行的逐字时间表。翻译本身没有独立时间轴，
    /// 按本行的起止时间、以字宽权重均分 —— 与原歌词的整行填充同一套算法，
    /// 让两行在跟唱时同步扫过。
    /// </summary>
    public IReadOnlyList<KaraokeChar> TranslationChars
        => _translationChars ??= BuildTranslationChars();

    private IReadOnlyList<KaraokeChar>? _translationChars;

    private bool _isCurrent;

    /// <summary>是否为当前演唱行。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ---------- 逐字时间表构建 ----------

    private IReadOnlyList<KaraokeChar> BuildChars()
    {
        if (string.IsNullOrEmpty(Text)) return Array.Empty<KaraokeChar>();

        // 按「文本元素」切分：正确处理代理对（emoji）与组合字符，
        // 避免把一个可见字符拆成两段。
        int[] starts = StringInfo.ParseCombiningCharacters(Text);
        int count = starts.Length;
        var elements = new string[count];
        var weights = new double[count];

        for (int i = 0; i < count; i++)
        {
            int end = i + 1 < count ? starts[i + 1] : Text.Length;
            elements[i] = Text[starts[i]..end];
            weights[i] = CharWeight(elements[i]);
        }

        // 把解析层的「字尾字符位置 + 绝对时间」转成「分段时间」。
        // 说明：WordSplit[i] 表示第 i 段结束于正文的哪个字符，WordTimes[i] 是该段的结束时间。
        var segments = new List<(int EndElement, double EndSeconds)>(_wordTimes.Count + 1);

        if (_wordTimes.Count > 0 && _wordSplit.Count > 0)
        {
            int elementCursor = 0;
            int wordCount = Math.Min(_wordTimes.Count, _wordSplit.Count);

            for (int i = 0; i < wordCount; i++)
            {
                int charEnd = Math.Clamp(_wordSplit[i], 0, Text.Length);
                while (elementCursor < count && starts[elementCursor] < charEnd)
                    elementCursor++;

                segments.Add((elementCursor, _wordTimes[i].TotalSeconds));
            }

            // 标签未覆盖到行尾时，剩余字符延续到整行结束
            if (segments.Count == 0 || segments[^1].EndElement < count)
                segments.Add((count, EndTime.TotalSeconds));
        }
        else
        {
            // 普通 lrc：整行作为一段
            segments.Add((count, EndTime.TotalSeconds));
        }

        var result = new List<KaraokeChar>(count);
        int segmentStartElement = 0;
        double segmentStartTime = Time.TotalSeconds;

        foreach (var (endElement, endSeconds) in segments)
        {
            double start = segmentStartTime;
            double end = Math.Max(endSeconds, start);   // 时间倒挂时按零时长处理

            int segmentCount = endElement - segmentStartElement;
            if (segmentCount > 0)
            {
                double segmentWeight = 0;
                for (int i = segmentStartElement; i < endElement; i++)
                    segmentWeight += weights[i];
                if (segmentWeight <= 0) segmentWeight = segmentCount;

                // 段内按字宽比例分配时长 → 中英文混排时视觉填充速度更均匀
                double cursor = start;
                for (int i = segmentStartElement; i < endElement; i++)
                {
                    double share = (end - start) * weights[i] / segmentWeight;
                    result.Add(new KaraokeChar
                    {
                        Text = elements[i],
                        StartSeconds = cursor,
                        EndSeconds = cursor + share,
                    });
                    cursor += share;
                }
            }

            segmentStartElement = endElement;
            segmentStartTime = end;
        }

        return result;
    }

    private IReadOnlyList<KaraokeChar> BuildTranslationChars()
    {
        if (string.IsNullOrEmpty(Translation)) return Array.Empty<KaraokeChar>();

        int[] starts = StringInfo.ParseCombiningCharacters(Translation);
        int count = starts.Length;
        var elements = new string[count];
        double total = 0;

        for (int i = 0; i < count; i++)
        {
            int elementEnd = i + 1 < count ? starts[i + 1] : Translation.Length;
            elements[i] = Translation[starts[i]..elementEnd];
            total += CharWeight(elements[i]);
        }

        if (total <= 0) total = count;

        double lineStart = Time.TotalSeconds;
        double lineEnd = Math.Max(EndTime.TotalSeconds, lineStart);
        double cursor = lineStart;

        var result = new List<KaraokeChar>(count);
        foreach (var element in elements)
        {
            double share = (lineEnd - lineStart) * CharWeight(element) / total;
            result.Add(new KaraokeChar
            {
                Text = element,
                StartSeconds = cursor,
                EndSeconds = cursor + share,
            });
            cursor += share;
        }

        return result;
    }

    /// <summary>
    /// 单字符的视觉宽度权重（粗略值，不追求像素级精确）。
    /// 用于让「按时间匀速填充」在中英文混排时看起来更自然。
    /// </summary>
    private static double CharWeight(string element)
    {
        if (element.Length == 0) return 0;

        char c = element[0];
        if (char.IsWhiteSpace(c)) return c == '　' ? 1.0 : 0.32;   // 全角空格按汉字宽
        if (LyricParser.IsCjk(c)) return 1.0;
        if (char.IsPunctuation(c) || char.IsSymbol(c)) return 0.45;
        return 0.56;
    }
}
