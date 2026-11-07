namespace Pebble.Services;

/// <summary>App 级持久化设置：GitHub Token、主题、服务器端口。基于 Preferences（Android 上等价 SharedPreferences）。</summary>
public static class AppSettings
{
    private const string KeyTheme = "theme";
    private const string KeyPort = "server_port";

    public static AppTheme Theme
    {
        get
        {
            var stored = Preferences.Default.Get(KeyTheme, "system");
            return stored switch
            {
                "light" => AppTheme.Light,
                "dark" => AppTheme.Dark,
                _ => AppTheme.Unspecified
            };
        }
        set
        {
            var key = value switch
            {
                AppTheme.Light => "light",
                AppTheme.Dark => "dark",
                _ => "system"
            };
            Preferences.Default.Set(KeyTheme, key);

            if (Application.Current is not null)
                Application.Current.UserAppTheme = value;
        }
    }

    public static int ServerPort
    {
        get => Preferences.Default.Get(KeyPort, 8080);
        set => Preferences.Default.Set(KeyPort, value);
    }

    // ---------------------------------------------------------------- GitHub Token

    /// <summary>读 GitHub Token。SecureStorage 在部分设备上可能不可用，读失败返回 null 而非抛异常。</summary>
    public static async Task<string?> GetGitHubTokenAsync()
    {
        try { return await SecureStorage.Default.GetAsync("github_token"); }
        catch (Exception ex) { AppLog.Error("secure-read", ex); return null; }
    }

    public static async Task SetGitHubTokenAsync(string? token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token)) SecureStorage.Default.Remove("github_token");
            else await SecureStorage.Default.SetAsync("github_token", token.Trim());
        }
        catch (Exception ex) { AppLog.Error("secure-write", ex); }
    }
}
