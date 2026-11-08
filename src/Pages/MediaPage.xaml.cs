using CommunityToolkit.Maui.Converters;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Pebble.Models;
using Pebble.Services;

namespace Pebble;

public partial class MediaPage : ContentPage
{
    private readonly FileItem _item;
    private readonly MediaElement _player;

    public MediaPage(FileItem item)
    {
        InitializeComponent();

        _item = item;
        Title = item.Name;
        TitleLabel.Text = item.Name;

        // MediaElement 通过代码创建并加入页面，避开 XAML 源生成器的跨程序集类型解析问题。
        _player = new MediaElement
        {
            ShouldAutoPlay = true,
            ShouldShowPlaybackControls = true,
            BackgroundColor = Colors.Black
        };
        PlayerHost.Content = _player;

        try
        {
            _player.Source = MediaSource.FromFile(item.FullPath);
        }
        catch (Exception ex)
        {
            AppLog.Error("media-source", ex);
            _ = this.SafeAlertAsync("无法加载", ex.Message, "好");
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Android 有时会忽略 ShouldAutoPlay，这里兜底显式播放一次。
        // 音频没有视频画面，控件已开启，手动播放能保证出声。
        Dispatcher.Dispatch(async () =>
        {
            await Task.Delay(200);
            try { _player.Play(); } catch (Exception ex) { AppLog.Error("media-play", ex); }
        });
    }
}
