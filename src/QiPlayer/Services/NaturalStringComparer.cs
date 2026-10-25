using System.Runtime.InteropServices;

namespace QiPlayer.Services;

/// <summary>
/// 本地化字符串比较器，行为对齐 MusicPlayer2 的
/// <c>CCommon::StringCompareInLocalLanguage</c>。
///
/// 直接调用 Win32 <c>CompareStringEx</c>，传入：
///   · LOCALE_NAME_USER_DEFAULT —— 按用户系统区域排序
///     （中文系统下汉字按拼音，而非 Unicode 码点）
///   · NORM_IGNORECASE        —— 忽略大小写
///   · SORT_DIGITSASNUMBERS   —— 数字按数值比较
///     （「曲目2」排在「曲目10」之前，而非字典序）
///
/// 用 <see cref="StringComparer.OrdinalIgnoreCase"/> 会退化为码点比较，
/// 表现为英文在前、假名居中、汉字按码点乱序，不符合中文用户预期。
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    private const uint NORM_IGNORECASE = 0x00000001;
    private const uint SORT_DIGITSASNUMBERS = 0x00000008;

    private const int CSTR_LESS_THAN = 1;
    private const int CSTR_EQUAL = 2;
    private const int CSTR_GREATER_THAN = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CompareStringEx(
        string? lpLocaleName,
        uint dwCmpFlags,
        string lpString1,
        int cchCount1,
        string lpString2,
        int cchCount2,
        IntPtr lpVersionInformation,
        IntPtr lpReserved,
        IntPtr lParam);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        // 快速路径：完全相同直接返回，省一次 P/Invoke
        if (string.Equals(x, y, StringComparison.Ordinal)) return 0;

        int result = CompareStringEx(
            null,                                   // LOCALE_NAME_USER_DEFAULT
            NORM_IGNORECASE | SORT_DIGITSASNUMBERS,
            x, x.Length,
            y, y.Length,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        return result switch
        {
            CSTR_LESS_THAN => -1,
            CSTR_EQUAL => 0,
            CSTR_GREATER_THAN => 1,
            // API 失败（返回 0）时回退到序数比较，保证不抛异常
            _ => string.CompareOrdinal(x, y),
        };
    }
}