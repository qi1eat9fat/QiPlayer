using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using QiPlayer.Classes;
using QiPlayer.Classes.Settings;
using QiPlayer.ViewModels;
using MicaWPF.Controls;
using Wpf.Ui.Controls;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace QiPlayer.Windows;

/// <summary>
/// 媒体 Flyout。
///
/// 视觉与布局来自 FluentFlyout 的 MainWindow；
/// 数据源换成本地播放器（<see cref="PlayerViewModel"/>），不再读取 SMTC。
/// </summary>
public partial class MediaFlyoutWindow : MicaWindow
{
    private readonly PlayerViewModel _player;
    private NowPlayingState? _state;

    private bool _isDragging;
    private bool _suppressSeek;

    /// <summary>单击封面：请求打开主窗口。窗口本身不依赖主窗口，由 FlyoutService 处理。</summary>
    public event EventHandler? CoverClicked;

    public MediaFlyoutWindow(PlayerViewModel player)
    {
        _player = player;

        // 不抢焦点：Flyout 弹出时不应打断用户当前操作
        WindowHelper.SetNoActivate(this);

        InitializeComponent();
        WindowHelper.SetTopmost(this);
        CustomWindowChrome.CaptionHeight = 0;

        ApplySettings();
    }

    public void SetState(NowPlayingState state)
    {
        _state = state;
        state.PropertyChanged += (_, _) => Dispatcher.Invoke(UpdateContent);
        UpdateContent();
    }

    // ---------- 设置 ----------

    /// <summary>应用与外观相关的设置（布局、模糊、亚克力、进度条）。</summary>
    public void ApplySettings()
    {
        var s = SettingsManager.Current;

        ApplyLayout();
        ApplyBackgroundBlur();
        ApplyAcrylic();
        UpdateSeekbarVisibility();
    }

    private void ApplyLayout()
    {
        var s = SettingsManager.Current;

        int extraHeight = s.SeekbarEnabled ? 36 : 0;

        Height = 112 + extraHeight;
        Width = 310;

        BodyStackPanel.Orientation = Orientation.Vertical;
        BodyStackPanel.Width = 194;
        ControlsStackPanelContainer.Margin = new Thickness(8, 8, 0, 0);
        ControlsStackPanelContainer.Width = double.NaN;
        ControlsStackPanelContainer.HorizontalAlignment = HorizontalAlignment.Stretch;

        // 播放控制键始终居中（标题是否居中仍由 CenterTitleArtist 决定）
        ControlsStackPanel.HorizontalAlignment = HorizontalAlignment.Center;

        SongImageBorder.Margin = new Thickness(6);
        SongImageBorder.Height = 82;
        SongImageBorder.Width = 82;
        SongInfoStackPanel.Margin = new Thickness(8, 0, 0, 0);
        SongInfoStackPanel.Width = 182;

        var alignment = s.CenterTitleArtist ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        SongTitle.HorizontalAlignment = alignment;
        SongArtist.HorizontalAlignment = alignment;
    }

    private void ApplyBackgroundBlur()
    {
        int style = SettingsManager.Current.MediaFlyoutBackgroundBlur;

        BackgroundImageStyle1.Visibility = style == 1 ? Visibility.Visible : Visibility.Collapsed;
        BackgroundImageStyle2.Visibility = style == 2 ? Visibility.Visible : Visibility.Collapsed;
        BackgroundImageStyle3.Visibility = style == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyAcrylic()
    {
        if (SettingsManager.Current.MediaFlyoutAcrylicWindowEnabled)
            WindowBlurHelper.EnableBlur(this);
        else
            WindowBlurHelper.DisableBlur(this);
    }

    private void UpdateSeekbarVisibility()
    {
        SeekbarWrapper.Visibility = SettingsManager.Current.SeekbarEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ---------- 内容更新 ----------

    public void UpdateContent()
    {
        var state = _state;
        if (state is null) return;

        SongTitle.Text = string.IsNullOrEmpty(state.Title) ? "未在播放" : state.Title;
        SongArtist.Text = state.Artist;
        SongInfoStackPanel.ToolTip = string.IsNullOrEmpty(state.Artist)
            ? state.Title
            : $"{state.Title}\n\n{state.Artist}";

        SymbolPlayPause.Symbol = state.IsPlaying
            ? SymbolRegular.Pause16
            : SymbolRegular.Play16;

        // 封面
        var cover = state.Cover;
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

        // 模糊背景使用裁方后的图，保证径向遮罩在非正方形图上正确
        if (SettingsManager.Current.MediaFlyoutBackgroundBlur != 0)
        {
            var square = BitmapHelper.CropToSquare(state.CoverBitmap);
            if (square is not null)
            {
                BackgroundImageStyle1.Source = square;
                BackgroundImageStyle2.Source = square;
                BackgroundImageStyle3.Source = square;
            }
        }

        // 进度
        if (SettingsManager.Current.SeekbarEnabled)
        {
            SeekbarMaxDuration.Text = state.DurationText;

            if (!_isDragging)
            {
                _suppressSeek = true;
                Seekbar.Maximum = Math.Max(1, state.DurationSeconds);
                Seekbar.Value = Math.Clamp(state.PositionSeconds, 0, Seekbar.Maximum);
                _suppressSeek = false;
            }

            SeekbarCurrentDuration.Text = state.PositionText;
        }
    }

    // ---------- 交互 ----------

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        // 悬停时保持显示由 FlyoutService 的轮询处理，这里只刷新一次内容
        UpdateContent();
    }

    private void Cover_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CoverClicked?.Invoke(this, EventArgs.Empty);
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
        => _player.PlayPreviousCommand.Execute(null);

    private void PlayPause_Click(object sender, RoutedEventArgs e)
        => _player.PlayPauseCommand.Execute(null);

    private void Next_Click(object sender, RoutedEventArgs e)
        => _player.PlayNextCommand.Execute(null);

    private void Seekbar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _isDragging = true;

    private void Seekbar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;

        if (sender is Slider slider)
            _player.SeekTo(slider.Value);
    }

    private void Seekbar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSeek || !_isDragging) return;

        SeekbarCurrentDuration.Text = TimeSpan.FromSeconds(e.NewValue).ToString(@"mm\:ss");
    }
}
