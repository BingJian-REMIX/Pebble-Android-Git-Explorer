namespace Pebble.Services;

/// <summary>
/// Android 11+ 的全盘访问权限。App 沙盒内读文件不需要它，浏览手机存储才需要。
/// </summary>
public static class StorageAccess
{
    public const string ExternalRoot = "/storage/emulated/0";

    public static bool IsAndroid => OperatingSystem.IsAndroid();

    /// <summary>
    /// Android 10 及以下用 manifest 里的 READ/WRITE_EXTERNAL_STORAGE（安装时授予）；
    /// 11 起要看用户有没有在设置里打开「所有文件访问权限」。
    /// </summary>
    public static bool HasAllFilesAccess()
    {
#if ANDROID
        if (!OperatingSystem.IsAndroidVersionAtLeast(30)) return true;

        try
        {
            return Android.OS.Environment.IsExternalStorageManager;
        }
        catch (Exception ex)
        {
            AppLog.Error("perm-check", ex);
            return false;
        }
#else
        return true;   // Windows/macOS 直接能读
#endif
    }

    /// <summary>
    /// 跳系统「所有文件访问权限」设置页，并直接定位到本应用。
    /// MIUI/HyperOS 上有些版本不认带 package 参数的 Action，会再退回不带参数的。
    /// </summary>
    public static void RequestAllFilesAccess()
    {
#if ANDROID
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity is null) return;

        try
        {
            var intent = new Android.Content.Intent(Android.Provider.Settings.ActionManageAllFilesAccessPermission);
            intent.SetData(Android.Net.Uri.Parse("package:" + activity.PackageName));
            activity.StartActivity(intent);
            return;
        }
        catch (Exception ex)
        {
            AppLog.Error("perm-request", ex);
        }

        try
        {
            activity.StartActivity(new Android.Content.Intent(
                Android.Provider.Settings.ActionManageAllFilesAccessPermission));
        }
        catch (Exception ex)
        {
            AppLog.Error("perm-request2", ex);
        }
#endif
    }

    public static bool ExternalRootExists()
    {
        try { return Directory.Exists(ExternalRoot); }
        catch (Exception) { return false; }
    }
}
