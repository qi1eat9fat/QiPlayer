using System.Windows;
using System.Windows.Controls;

namespace QiPlayer.Controls;

/// <summary>
/// 播放列表的列头按钮。
///
/// 点击即按该列排序；若已是当前排序列，则切换升/降序。
/// 当前排序列会显示方向箭头，并加粗以作区分。
/// </summary>
public sealed class ColumnHeaderButton : Button
{
    static ColumnHeaderButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(ColumnHeaderButton),
            new FrameworkPropertyMetadata(typeof(ColumnHeaderButton)));
    }

    /// <summary>是否为当前排序列（决定箭头是否显示）。</summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive),
            typeof(bool),
            typeof(ColumnHeaderButton),
            new PropertyMetadata(false));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>排序方向箭头字形。</summary>
    public static readonly DependencyProperty DirectionGlyphProperty =
        DependencyProperty.Register(
            nameof(DirectionGlyph),
            typeof(string),
            typeof(ColumnHeaderButton),
            new PropertyMetadata(string.Empty));

    public string DirectionGlyph
    {
        get => (string)GetValue(DirectionGlyphProperty);
        set => SetValue(DirectionGlyphProperty, value);
    }
}