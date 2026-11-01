namespace Pebble.Services;

/// <summary>
/// Android 的存储权限适配。App 私有目录（FileSystem.AppDataDirectory）本来就不需要权限，
/// 这里申请的是"浏览整个存储"的能力，用于后续打开用户自己的仓库目录。
/// </summary>
public static class StoragePermission
{
    public static async Task<bool> EnsureAsync()
    {
#if ANDROID
        // Android 11+ 用 MANAGE_EXTERNAL_STORAGE（特殊权限，只能跳到系统设置页由用户手动开）。
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            if (Android.OS.Environment.IsExternalStorageManager) return true;

            try
            {
                var context = Android.App.Application.Context;
                var intent = new Android.Content.Intent(
                    Android.Provider.Settings.ActionManageAppAllFilesAccessPermission,
                    Android.Net.Uri.Parse("package:" + context.PackageName));
                intent.AddFlags(Android.Content.ActivityFlags.NewTask);
                context.StartActivity(intent);
            }
            catch (Exception)
            {
                // 部分定制 ROM 没有该设置页，忽略即可。
            }

            return false;
        }

        var status = await Permissions.RequestAsync<Permissions.StorageRead>();
        return status == PermissionStatus.Granted;
#else
        await Task.CompletedTask;
        return true;
#endif
    }
}
