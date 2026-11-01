namespace Pebble.Services;

/// <summary>
/// Android 15 起强制 edge-to-edge，状态栏背景透明。
/// 亮色主题下如果不把状态栏图标切成深色，白图标会直接消失在浅色背景里。
/// </summary>
internal static class StatusBar
{
    public static void Refresh()
    {
#if ANDROID
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (!OperatingSystem.IsAndroidVersionAtLeast(30)) return;

                var window = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window;
                var controller = window?.InsetsController;
                if (controller is null) return;

                var light = Application.Current?.RequestedTheme != AppTheme.Dark;
                var flags = (int)Android.Views.WindowInsetsControllerAppearance.LightStatusBars;

                controller.SetSystemBarsAppearance(light ? flags : 0, flags);
            }
            catch (Exception ex)
            {
                AppLog.Error("statusbar", ex);
            }
        });
#endif
    }
}
