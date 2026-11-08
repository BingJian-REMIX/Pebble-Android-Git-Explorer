using System.Runtime.InteropServices;

namespace Pebble.Services;

/// <summary>
/// Android 上 libgit2 用 mbedTLS，没有系统证书库，必须显式喂 CA bundle。
/// LibGit2Sharp 没暴露这个选项，直接 P/Invoke 原生库。
/// </summary>
internal static class NativeGit
{
    // LibGit2Sharp 就是按这个名字 dlopen 的（Android 上会解析成 libgit2-5853918.so）
    private const string Library = "git2-5853918";

    // include/git2/common.h: GIT_OPT_SET_SSL_CERT_LOCATIONS 是第 13 个枚举值（从 0 起）
    private const int GitOptSetSslCertLocations = 12;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int git_libgit2_opts(int option, IntPtr file, IntPtr path);

    public static bool SetSslCertFile(string caFilePath)
    {
        var file = Marshal.StringToHGlobalAnsi(caFilePath);
        try
        {
            return git_libgit2_opts(GitOptSetSslCertLocations, file, IntPtr.Zero) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(file);
        }
    }
}
