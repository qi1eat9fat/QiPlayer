using System.Text.RegularExpressions;

namespace QiPlayer.Services;

/// <summary>
/// 一行歌词。
/// </summary>
public sealed class LyricLine
{
    /// <summary>该行起始时间。</summary>
    public TimeSpan Time { get; init; }

    /// <summary>原文。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>翻译（同一行以 " / " 分隔的后半部分）。无翻译时为空。</summary>
    public string Translation { get; init; } = string.Empty;

    /// <summary>
    /// 逐字时间点（相对歌曲开始的绝对时间）。
    /// 仅在扩展 lrc / ksc 等逐字歌词中出现；普通 lrc 为空。
    /// 与 <see cref="WordSplit"/> 配合使用：split[i] 是第 i 段的结束字符位置。
    /// </summary>
    public IReadOnlyList<TimeSpan> WordTimes { get; init; } = Array.Empty<TimeSpan>();

    /// <summary>
    /// 逐字分割位置：第 i 个元素表示前 i 段累积到 <see cref="Text"/> 的第几个字符。
    /// 例如 Text="abcd"、WordSplit=[2,4] 表示分成 "ab" 和 "cd" 两段。
    /// </summary>
    public IReadOnlyList<int> WordSplit { get; init; } = Array.Empty<int>();
}

/// <summary>
/// 歌词解析结果。
/// </summary>
public sealed class Lyrics
{
    public List<LyricLine> Lines { get; } = new();

    /// <summary>是否为空（无有效歌词行）。</summary>
    public bool IsEmpty => Lines.Count == 0;
}

/// <summary>
/// LRC 歌词解析器。
///
/// 移植自 MusicPlayer2 的 CLyrics（Lyric.cpp），保留核心解析逻辑：
///   · 标准 lrc：  [mm:ss.xx]歌词
///   · 压缩标签：  [mm:ss.xx][mm:ss.xx]歌词（同一句重复时间点）
///   · 翻译：      歌词 / 翻译
///   · 元数据：    [ti:] [ar:] [al:] [by:] [offset:]
///   · 扩展 lrc：  行内 &lt;mm:ss.xx&gt; 字标签 → 逐字高亮
///
/// 未保留（已从迁移范围删去）：繁简转换、编辑、在线下载、模糊匹配。
/// </summary>
public static class LyricParser
{
    // 行首时间标签：[mm:ss.xx] / [mm:ss:xx] / [mm:ss]（兼容全角冒号）
    private static readonly Regex TimeTagRegex = new(
        @"[\[<](?<m>\d{1,3})[:：](?<s>\d{1,2})(?:[.．:：](?<f>\d{1,3}))?[\]>]",
        RegexOptions.Compiled);

    // 元数据标签：[ti:xxx] 等
    private static readonly Regex MetaRegex = new(
        @"^\[(?<key>ti|ar|al|by|offset)[:：](?<val>.*)\]\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 解析歌词文本。无法解析时返回空 Lyrics（而非抛异常）。
    /// </summary>
    public static Lyrics Parse(string? lyricText)
    {
        var result = new Lyrics();
        if (string.IsNullOrWhiteSpace(lyricText))
            return result;

        // 统一换行；去掉 BOM
        string text = lyricText.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('﻿');

        var pendingLines = new List<(TimeSpan Time, string Body)>();

        foreach (var rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            // 元数据行（[ti:] / [ar:] / [al:] / [by:] / [offset:]）：跳过，不参与显示
            if (MetaRegex.IsMatch(line)) continue;

            // 只把「行首连续的时间标签」当作行时间（支持 [t1][t2] 压缩标签）。
            // 正文里的 <mm:ss.xx> 是逐字标签，必须留给 ParseBody 处理；
            // 若把它们也当成行标签，一句逐字歌词会被拆成多行，
            // 且正文只剩最后一个字之后的部分。
            var matches = TimeTagRegex.Matches(line);
            if (matches.Count == 0) continue;   // 无时间标签的行不是歌词（与 Lyric.cpp:199 一致）

            var leadingTags = new List<Match>();
            int cursor = 0;
            char bracket = '\0';
            foreach (Match m in matches)
            {
                if (m.Index < cursor) break;
                if (!IsBlank(line, cursor, m.Index)) break;   // 标签之间只允许空白

                // 行标签与逐字标签用不同的括号区分：
                //   [mm:ss.xx] 行标签；<mm:ss.xx> 逐字标签
                // 一旦括号类型变了，说明已经进入正文，不能继续当作行标签，
                // 否则 "[t]<t>歌词" 会同时产生两条重复歌词。
                char current = line[m.Index];
                if (leadingTags.Count == 0)
                    bracket = current;
                else if (current != bracket)
                    break;

                leadingTags.Add(m);
                cursor = m.Index + m.Length;
            }

            if (leadingTags.Count == 0) continue;

            // 行标签之后的部分才是正文（可能还含逐字标签）
            string body = line[cursor..];

            foreach (var m in leadingTags)
            {
                if (!TryParseTime(m, out var time)) continue;
                pendingLines.Add((time, body));
            }
        }

        // 按时间排序后生成歌词行
        foreach (var (time, body) in pendingLines.OrderBy(p => p.Time))
        {
            var lyricLine = ParseBody(body, time);
            if (lyricLine != null)
                result.Lines.Add(lyricLine);
        }

        return result;
    }

    // ---------- 内部 ----------

    private static bool TryParseTime(Match m, out TimeSpan time)
    {
        time = TimeSpan.Zero;

        if (!int.TryParse(m.Groups["m"].Value, out int minutes)) return false;
        if (!int.TryParse(m.Groups["s"].Value, out int seconds)) return false;

        int ms = 0;
        if (m.Groups["f"].Success)
        {
            string frac = m.Groups["f"].Value;
            // 按位数换算：2 位是百分秒，1 位是十分秒，3 位是毫秒
            if (!int.TryParse(frac, out int f)) return false;
            ms = frac.Length switch
            {
                1 => f * 100,
                2 => f * 10,
                3 => f,
                _ => f,
            };
        }

        time = new TimeSpan(0, 0, minutes, seconds, ms);
        return true;
    }

    /// <summary>
    /// 解析一行正文：剥离逐字标签、分离翻译。
    ///
    /// 顺序很重要：逐字标签必须先剥离再做翻译切分，
    /// 否则 "&lt;00:12.50&gt;你的指尖" 会被 CJK 边界判断成「英文原文 + 中文译文」。
    ///
    /// 翻译分隔规则（按优先级）：
    ///   1. " / "  —— MusicPlayer2 的标准格式
    ///   2. CJK 起始 —— 实测本机歌词库最常见的格式：
    ///      "Your fingertips across my skin 你的指尖轻滑过我肌肤"
    ///      原文（英文）之后直接跟译文，中间是单个空格。
    ///      以「第一段连续 CJK 字符」为分界，可正确切开
    ///      "Images 影像交替映现" 这类原文本身含空格的情况。
    /// </summary>
    private static LyricLine? ParseBody(string body, TimeSpan lineTime)
    {
        body = body.Trim();
        if (body.Length == 0) return null;

        // ---------- 1) 剥离逐字标签 ----------
        // 标签之前的文字属于当前段，标签时间 = 该段结束时间（与扩展 lrc 一致）。
        var wordTimes = new List<TimeSpan>();
        var wordEnds = new List<int>();          // 相对 plainRaw 的字符位置

        var sb = new System.Text.StringBuilder(body.Length);
        int lastEnd = 0;

        foreach (Match m in TimeTagRegex.Matches(body))
        {
            sb.Append(body, lastEnd, m.Index - lastEnd);
            lastEnd = m.Index + m.Length;

            if (TryParseTime(m, out var t))
            {
                wordTimes.Add(t);
                wordEnds.Add(sb.Length);
            }
        }

        sb.Append(body, lastEnd, body.Length - lastEnd);
        string plainRaw = sb.ToString();

        // ---------- 2) 在纯文本上拆分原文与翻译 ----------
        string text;
        string translation;

        int slash = plainRaw.IndexOf(" / ", StringComparison.Ordinal);
        if (slash > 0)
        {
            text = plainRaw[..slash];
            translation = plainRaw[(slash + 3)..].Trim();
        }
        else
        {
            int split = FindCjkBoundary(plainRaw);
            if (split > 0)
            {
                text = plainRaw[..split];
                translation = plainRaw[split..].Trim();
            }
            else
            {
                text = plainRaw;
                translation = string.Empty;
            }
        }

        // 去掉原文首尾空白，并记录左端偏移，避免逐字位置整体前移
        int lead = 0;
        while (lead < text.Length && char.IsWhiteSpace(text[lead])) lead++;

        int tail = text.Length;
        while (tail > lead && char.IsWhiteSpace(text[tail - 1])) tail--;

        text = text[lead..tail];
        if (text.Length == 0) return null;

        // ---------- 3) 把逐字位置换算到裁剪后的原文 ----------
        // 只有「行起始时间之后」还有标签，才算真正的逐字歌词。
        // 若标签全落在行首（如 "[t]<t>整句"），逐字信息没有意义，
        // 应退回普通行按整行时长填充，否则会用 0.5 秒草草填完整句。
        bool hasWordTiming = wordTimes.Any(t => t > lineTime + TimeSpan.FromMilliseconds(50));

        if (wordEnds.Count == 0 || !hasWordTiming)
        {
            return new LyricLine
            {
                Time = lineTime,
                Text = text,
                Translation = translation,
            };
        }

        var wordSplit = new List<int>(wordEnds.Count);
        foreach (int end in wordEnds)
            wordSplit.Add(Math.Clamp(end - lead, 0, text.Length));

        // 末段若无显式时长，用前一段的间隔补足（与 Lyric.cpp:545 的处理一致）
        if (wordSplit[^1] < text.Length)
        {
            wordSplit.Add(text.Length);
            var lastInterval = wordTimes.Count >= 2
                ? wordTimes[^1] - wordTimes[^2]
                : TimeSpan.FromMilliseconds(500);
            wordTimes.Add(wordTimes[^1] + lastInterval);
        }

        return new LyricLine
        {
            Time = lineTime,
            Text = text,
            Translation = translation,
            WordTimes = wordTimes,
            WordSplit = wordSplit,
        };
    }

    /// <summary>判断 [start, end) 区间是否全为空白。</summary>
    private static bool IsBlank(string s, int start, int end)
    {
        for (int i = start; i < end; i++)
            if (!char.IsWhiteSpace(s[i])) return false;

        return true;
    }

    /// <summary>
    /// 找「第一段连续 CJK 字符」的起始位置，作为原文与译文的分界。
    /// 返回 0 表示整行都是 CJK（无原文），或未找到分界（无译文）。
    /// </summary>
    private static int FindCjkBoundary(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (!IsCjk(s[i])) continue;

            // CJK 出现在行首：整行都是中文，无需切分
            if (i == 0) return 0;

            // 分界前必须真的有外文内容。
            // 否则「（前奏）」「【副歌】」这类以全角标点开头的整行中文，
            // 会被误判成「原文=标点，译文=整句」。
            if (!HasLatinContent(s, 0, i)) return 0;

            return i;
        }
        return 0;
    }

    /// <summary>[start, end) 内是否含有字母或数字（用于确认存在外文原文）。</summary>
    private static bool HasLatinContent(string s, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            char c = s[i];
            if (char.IsLetterOrDigit(c) && !IsCjk(c)) return true;
        }
        return false;
    }

    /// <summary>判断是否为中日韩文字。</summary>
    internal static bool IsCjk(char c) => c switch
    {
        >= '一' and <= '鿿' => true,   // 中日韩统一表意文字
        >= '㐀' and <= '䶿' => true,   // 扩展 A
        >= '぀' and <= 'ヿ' => true,   // 日文假名
        >= '가' and <= '힯' => true,   // 韩文音节
        _ => false,
    };
}