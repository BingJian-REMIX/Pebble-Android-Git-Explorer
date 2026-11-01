using Pebble.Models;
using Pebble.Services;

namespace Pebble;

public partial class ImagePreviewPage : ContentPage
{
    private readonly FileItem _item;
    private double _pinchStartScale = 1;
    private double _panStartX;
    private double _panStartY;

    /// <summary>解码一张 4000x3000 的照片要约 48MB 堆内存，Android 默认堆扛不住。
    /// 超过这个大小直接劝退，让用户用系统相册看。</summary>
    private const long MaxBytes = 25L * 1024 * 1024;

    public ImagePreviewPage(FileItem item)
    {
        InitializeComponent();

        _item = item;
        Title = item.Name;
        NameLabel.Text = item.Name;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        try
        {
            var size = FileOperations.SizeOf(_item.FullPath);
            MetaLabel.Text = $"{FileOperations.FormatSize(size)} · {_item.RelativePath}";

            if (size > MaxBytes)
            {
                MetaLabel.Text = $"{FileOperations.FormatSize(size)} · 过大，已停止加载";
                MainThread.BeginInvokeOnMainThread(async () =>
                    await this.SafeAlertAsync("图片过大",
                        $"这张图片 {FileOperations.FormatSize(size)}，直接解码会耗尽内存。建议用系统相册查看。", "好"));
                return;
            }

            Pic.Source = ImageSource.FromFile(_item.FullPath);
        }
        catch (Exception ex)
        {
            AppLog.Error("image", ex);
            MetaLabel.Text = "无法加载图片";
        }
    }

    private void OnPinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        switch (e.Status)
        {
            case GestureStatus.Started:
                _pinchStartScale = Pic.Scale;
                break;

            case GestureStatus.Running:
                Pic.Scale = Math.Clamp(_pinchStartScale * e.Scale, 1, 8);
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                if (Pic.Scale <= 1.02) ResetTransform();
                break;
        }
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panStartX = Pic.TranslationX;
                _panStartY = Pic.TranslationY;
                break;

            case GestureStatus.Running:
                if (Pic.Scale <= 1.02) return;   // 未放大时不做平移，避免图片被拖出屏幕
                Pic.TranslationX = _panStartX + e.TotalX;
                Pic.TranslationY = _panStartY + e.TotalY;
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                ClampTranslation();
                break;
        }
    }

    private async void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Pic.Scale > 1.02)
        {
            ResetTransform();
            return;
        }

        await Pic.ScaleToAsync(2.5, 180, Easing.CubicOut);
    }

    private void ClampTranslation()
    {
        var maxX = Math.Max(0, (Pic.Width * Pic.Scale - Width) / 2);
        var maxY = Math.Max(0, (Pic.Height * Pic.Scale - Height) / 2);

        Pic.TranslationX = Math.Clamp(Pic.TranslationX, -maxX, maxX);
        Pic.TranslationY = Math.Clamp(Pic.TranslationY, -maxY, maxY);
    }

    private void ResetTransform()
    {
        Pic.Scale = 1;
        Pic.TranslationX = 0;
        Pic.TranslationY = 0;
    }

    private async void OnCloseTapped(object? sender, TappedEventArgs e) => await Navigation.PopAsync();
}
