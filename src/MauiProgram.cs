using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using Pebble.Services;

#if ANDROID
using Microsoft.Maui.Handlers;
#endif

namespace Pebble;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // 兜底：任何逃逸的异常都留痕，否则 Android 上就是静默闪退
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("unhandled", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("unobserved", e.Exception);
            e.SetObserved();
        };

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            })
            .UseMauiCommunityToolkit()
            .UseMauiCommunityToolkitMediaElement(false);

#if ANDROID
        // 安卓 15 (API 35 / Redmi OS2.0.207) 上 MAUI WebView 走硬件加速 GL 通道
        // (GLFunctorDrawable::onDraw) 时 SkSurface 为空，会 SIGSEGV 原生崩溃
        // (fault addr 0x28，RenderThread 里 NullSurface)。统一切到软件渲染层，
        // 绕过该硬件 GL 路径，覆盖编辑器/Diff 页所有 WebView。
        WebViewHandler.Mapper.AppendToMapping("DisableHwAccel", (handler, view) =>
        {
            if (handler.PlatformView is Android.Webkit.WebView wv)
                wv.SetLayerType(Android.Views.LayerType.Software, null);
        });
#endif

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
