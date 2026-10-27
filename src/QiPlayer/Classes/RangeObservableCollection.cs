using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace QiPlayer.Classes;

/// <summary>
/// 支持整体替换的 ObservableCollection。
///
/// 逐项 Clear + Add 会让每一个元素都触发一次集合变更通知，
/// 上万首曲目时界面要重复刷新上万次；整体替换只发一次 Reset。
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification;

    /// <summary>用给定内容整体替换当前集合，只触发一次 Reset 通知。</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        _suppressNotification = true;
        try
        {
            Items.Clear();
            foreach (var item in items)
                Items.Add(item);
        }
        finally
        {
            _suppressNotification = false;
        }

        // 与 ObservableCollection 自身的行为一致：先通知属性，再通知集合
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification) base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressNotification) base.OnPropertyChanged(e);
    }
}
