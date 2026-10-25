using System.Windows;
using System.Windows.Controls;

namespace QiPlayer.Controls;

/// <summary>
/// 设置页中的一行：左侧是标题与说明，右侧是控件。
///
/// 对应参考稿的 .row —— 卡片内相邻两行之间画 1px 分隔线。
/// 第一行紧接卡片标题，用 <see cref="IsFirst"/> 关掉顶部分隔线。
///
/// 用 ContentControl 而不是自定义面板，是为了让右侧控件
/// 可以直接写成子元素（开关、下拉、滑杆组等），不需要额外的属性包装。
/// </summary>
public class SettingsRow : ContentControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(
            nameof(Header), typeof(string), typeof(SettingsRow),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(
            nameof(Description), typeof(string), typeof(SettingsRow),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsFirstProperty =
        DependencyProperty.Register(
            nameof(IsFirst), typeof(bool), typeof(SettingsRow),
            new PropertyMetadata(false));

    /// <summary>行标题。</summary>
    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>标题下方的说明文字；留空则整行只显示标题。</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>是否为本卡片的第一行（不画顶部分隔线）。</summary>
    public bool IsFirst
    {
        get => (bool)GetValue(IsFirstProperty);
        set => SetValue(IsFirstProperty, value);
    }
}
