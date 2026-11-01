using Microsoft.Extensions.Logging;
using Pebble.Services;

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
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
