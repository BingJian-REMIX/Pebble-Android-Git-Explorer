using Pebble.Services;

namespace Pebble;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UserAppTheme = AppTheme.Unspecified; // 跟随系统亮/暗

        // 主题切换时状态栏图标颜色要跟着翻转，否则亮色下白图标会看不见
        RequestedThemeChanged += (_, _) => StatusBar.Refresh();
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
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(300), StatusBar.Refresh);

        return window;
    }
}
