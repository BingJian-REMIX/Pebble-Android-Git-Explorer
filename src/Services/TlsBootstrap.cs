namespace Pebble.Services;

/// <summary>
/// 首次使用时把打包的 CA bundle 落到私有目录，并交给 libgit2。
/// Android 没有 /etc/ssl/certs，mbedTLS 后端不会自动找到系统根证书，
/// 不设置的话 HTTPS clone/push 会报 "failed to load CA certificates"。
/// </summary>
public static class TlsBootstrap
{
    private const string CaAssetName = "cacert.pem";

#if ANDROID
    private static bool _configured;
#endif

    public static async Task EnsureCaBundleAsync()
    {
#if ANDROID
        if (_configured) return;

        try
        {
            var target = Path.Combine(FileSystem.AppDataDirectory, CaAssetName);

            if (!File.Exists(target))
            {
                await using var source = await FileSystem.OpenAppPackageFileAsync(CaAssetName);
                await using var destination = File.Create(target);
                await source.CopyToAsync(destination);
            }

            _configured = NativeGit.SetSslCertFile(target);
        }
        catch (Exception)
        {
            // 拿不到 CA 不该挡启动：本地仓库操作完全不依赖 TLS。
        }
#else
        await Task.CompletedTask;
#endif
    }
}
