using System.Collections.ObjectModel;
using Pebble.Models;
using Pebble.Services;

namespace Pebble;

public partial class ImagePreviewPage : ContentPage
{
    private readonly FileItem _item;
    private readonly IReadOnlyList<FileItem> _all;
    private double _pinchStartScale = 1;
    private double _panStartX;
    private double _panStartY;

    /// <summary>解码一张 4000x3000 的照片要约 48MB 堆内存，Android 默认堆扛不住。
    /// 超过这个大小直接劝退，让用户用系统相册看。</summary>
    private const long MaxBytes = 25L * 1024 * 1024;

    public ObservableCollection<FileItem> Photos { get; } = new();

    public ImagePreviewPage(FileItem item, IReadOnlyList<FileItem>? siblings = null)
    {
        InitializeComponent();

        _item = item;
        _all = siblings ?? new[] { item };

        foreach (var f in _all) Photos.Add(f);

        Title = item.Name;
        NameLabel.Text = item.Name;

        Carousel.ItemsSource = Photos;

        // 定位到初始图片
        var index = 0;
        for (var i = 0; i < _all.Count; i++)
        {
            if (_all[i].FullPath == item.FullPath) { index = i; break; }
        }
        Carousel.Position = index;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        UpdateMeta(_item);
    }

    private void UpdateMeta(FileItem item)
    {
        try
        {
            var size = FileOperations.SizeOf(item.FullPath);
            NameLabel.Text = item.Name;
            MetaLabel.Text = size > MaxBytes
                ? $"{FileOperations.FormatSize(size)} · 过大"
                : $"{FileOperations.FormatSize(size)} · {Photos.IndexOf(item) + 1}/{Photos.Count}";
        }
        catch (Exception ex)
        {
            AppLog.Error("image-meta", ex);
            MetaLabel.Text = "无法读取文件信息";
        }
    }

    private void OnPositionChanged(object? sender, PositionChangedEventArgs e)
    {
        if (e.CurrentPosition < 0 || e.CurrentPosition >= Photos.Count) return;

        var current = Photos[e.CurrentPosition];
        ResetTransform();

        if (FileOperations.SizeOf(current.FullPath) > MaxBytes)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
                await this.SafeAlertAsync("图片过大",
                    $"这张图片 {FileOperations.FormatSize(FileOperations.SizeOf(current.FullPath))}，直接解码会耗尽内存。建议用系统相册查看。", "好"));
        }
    }

    private void OnPinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        if (sender is not Image img) return;

        switch (e.Status)
        {
            case GestureStatus.Started:
                _pinchStartScale = img.Scale;
                break;
            case GestureStatus.Running:
                img.Scale = Math.Clamp(_pinchStartScale * e.Scale, 1, 8);
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                if (img.Scale <= 1.02) ResetTransform();
                break;
        }
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (sender is not Image img) return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panStartX = img.TranslationX;
                _panStartY = img.TranslationY;
                break;
            case GestureStatus.Running:
                if (img.Scale <= 1.02) return;
                img.TranslationX = _panStartX + e.TotalX;
                img.TranslationY = _panStartY + e.TotalY;
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                ClampTranslation(img);
                break;
        }
    }

    private async void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Image img) return;

        if (img.Scale > 1.02)
        {
            ResetTransform();
            return;
        }

        await img.ScaleToAsync(2.5, 180, Easing.CubicOut);
    }

    private void ClampTranslation(Image img)
    {
        var maxX = Math.Max(0, (img.Width * img.Scale - Width) / 2);
        var maxY = Math.Max(0, (img.Height * img.Scale - Height) / 2);

        img.TranslationX = Math.Clamp(img.TranslationX, -maxX, maxX);
        img.TranslationY = Math.Clamp(img.TranslationY, -maxY, maxY);
    }

    private void ResetTransform()
    {
        // 遍历 Carousel 视觉树里所有已实例化的 Image，全部复位缩放与位移
        foreach (var img in Carousel.GetVisualTreeDescendants().OfType<Image>())
        {
            img.Scale = 1;
            img.TranslationX = 0;
            img.TranslationY = 0;
        }
    }

    private async void OnCloseTapped(object? sender, TappedEventArgs e) => await Navigation.PopAsync();
}
