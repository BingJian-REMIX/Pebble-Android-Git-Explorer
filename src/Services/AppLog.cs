namespace Pebble.Services;

/// <summary>崩溃/异常留痕：写 logcat（Android）+ 私有目录日志文件，方便定位闪退。</summary>
internal static class AppLog
{
    public static void Error(string tag, Exception? ex)
    {
        if (ex is null) return;

        System.Diagnostics.Debug.WriteLine($"[Pebble:{tag}] {ex}");

#if ANDROID
        Android.Util.Log.Error("Pebble", $"{tag}: {ex}");
#endif

        try
        {
            var path = Path.Combine(FileSystem.AppDataDirectory, "pebble.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {tag}: {ex}\n\n");
        }
        catch (Exception)
        {
            // 日志本身失败就算了，不能因此再崩一次
        }
    }
}

/// <summary>
/// 弹窗在 Activity 未就绪 / 页面已销毁时会抛异常。
/// 如果放任它从 async void 里逃逸，进程会直接被杀——表现就是"点一下就闪退"。
/// </summary>
internal static class PageExtensions
{
    public static async Task SafeAlertAsync(this Page page, string title, string message, string cancel = "好")
    {
        try
        {
            await page.DisplayAlertAsync(title, message, cancel);
        }
        catch (Exception ex)
        {
            AppLog.Error("alert", ex);
        }
    }

    public static async Task<bool> SafeConfirmAsync(this Page page, string title, string message, string accept, string cancel)
    {
        try
        {
            return await page.DisplayAlertAsync(title, message, accept, cancel);
        }
        catch (Exception ex)
        {
            AppLog.Error("confirm", ex);
            return false;
        }
    }
}
