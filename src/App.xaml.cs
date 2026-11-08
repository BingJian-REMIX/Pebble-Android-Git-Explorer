using Microsoft.Maui.ApplicationModel;
using Pebble.Services;

namespace Pebble;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();

        // 恢复用户上次选择的主题（默认跟随系统）
        UserAppTheme = AppSettings.Theme;

        // 主题切换时状态栏颜色/图标要跟着翻转，否则亮色下白图标会看不见
        RequestedThemeChanged += (_, _) => ApplyStatusBarTheme();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var navigation = new NavigationPage(new MainPage());

        navigation.SetAppThemeColor(NavigationPage.BarBackgroundColorProperty,
            Color.FromArgb("#F7F8FC"), Color.FromArgb("#07090F"));
        navigation.SetAppThemeColor(NavigationPage.BarTextColorProperty,
            Color.FromArgb("#14161F"), Color.FromArgb("#E9ECF5"));

        // 桌面调试时按手机竖屏比例开窗，方便对照 Android 效果。
        var window = new Window(navigation)
        {
            Title = "Pebble",
            Width = 400,
            Height = 780
        };

        window.Created += (_, _) =>
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(300), ApplyStatusBarTheme);

        return window;
    }

    /// <summary>
    /// 让 Android 状态栏背景跟随主题（与导航栏同色），并翻转图标明暗。
    /// 直接调用原生 Window，避免依赖 CommunityToolkit 的 StatusBar（运行时未生效）。
    /// </summary>
    private static void ApplyStatusBarTheme()
    {
#if ANDROID
        try
        {
            var activity = Platform.CurrentActivity;
            if (activity?.Window is not { } win)
                return;

            // Android 15 / MIUI 下状态栏默认透明(edge-to-edge)，SetStatusBarColor 常被忽略；
            // 显式声明"可绘制系统栏背景"并清除透明，才能让状态栏真正显示指定颜色。
            win.AddFlags(Android.Views.WindowManagerFlags.DrawsSystemBarBackgrounds);
            win.ClearFlags(Android.Views.WindowManagerFlags.TranslucentStatus);

            var userTheme = Application.Current?.UserAppTheme ?? AppTheme.Unspecified;
            var reqTheme = Application.Current?.RequestedTheme ?? AppTheme.Unspecified;
            var isDark = userTheme == AppTheme.Dark ||
                         (userTheme != AppTheme.Light && reqTheme == AppTheme.Dark);

            // 状态栏背景 = 导航栏背景（浅色 #F7F8FC / 深色 #07090F），消除突兀的紫色
            win.SetStatusBarColor(Android.Graphics.Color.ParseColor(isDark ? "#07090F" : "#F7F8FC"));

            // 浅色背景 -> 深色图标；深色背景 -> 浅色图标。
            // 注意：WindowInsetsController 的 APPEARANCE_LIGHT_STATUS_BARS = 0x8，
            // 与旧版 View.SystemUiFlags.LightStatusBar(0x2000) 数值不同，必须分别使用，
            // 否则 SetSystemBarsAppearance 的 mask 不匹配，图标颜色设置完全不生效。
            const int AppearanceLightStatusBars = 0x8;
            var lightIcons = !isDark;
            if (win.InsetsController is { } controller)
            {
                controller.SetSystemBarsAppearance(lightIcons ? AppearanceLightStatusBars : 0, AppearanceLightStatusBars);
            }
            else
            {
                var flags = (Android.Views.SystemUiFlags)win.DecorView!.SystemUiVisibility;
                if (lightIcons) flags |= Android.Views.SystemUiFlags.LightStatusBar;
                else flags &= ~Android.Views.SystemUiFlags.LightStatusBar;
                win.DecorView.SystemUiVisibility = (Android.Views.StatusBarVisibility)flags;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("statusbar-ex", ex);
        }
#endif
    }
}
