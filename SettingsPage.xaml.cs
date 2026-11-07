using Pebble.Services;

namespace Pebble;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();

        ThemePicker.SelectedIndex = AppSettings.Theme switch
        {
            AppTheme.Light => 1,
            AppTheme.Dark => 2,
            _ => 0
        };

        PortEntry.Text = AppSettings.ServerPort.ToString();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        var token = await AppSettings.GetGitHubTokenAsync();
        if (!string.IsNullOrEmpty(token)) TokenEntry.Text = token;
    }

    private async void OnSaveTokenClicked(object? sender, EventArgs e)
    {
        var token = TokenEntry.Text?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            await AppSettings.SetGitHubTokenAsync(null);
            await this.SafeAlertAsync("已清除", "GitHub Token 已清除。", "好");
            return;
        }

        await AppSettings.SetGitHubTokenAsync(token);
        await this.SafeAlertAsync("已保存", "Token 已写入系统安全存储。", "好");
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (ThemePicker.SelectedIndex < 0) return;

        AppSettings.Theme = ThemePicker.SelectedIndex switch
        {
            1 => AppTheme.Light,
            2 => AppTheme.Dark,
            _ => AppTheme.Unspecified
        };
    }

    private async void OnSavePortClicked(object? sender, EventArgs e)
    {
        if (!int.TryParse(PortEntry.Text?.Trim(), out var port) || port is < 1024 or > 65535)
        {
            await this.SafeAlertAsync("端口无效", "请输入 1024–65535 之间的端口号。", "好");
            return;
        }

        AppSettings.ServerPort = port;
        await this.SafeAlertAsync("已保存", $"服务器端口设为 {port}。", "好");
    }

    private async void OnClearTrashClicked(object? sender, EventArgs e)
    {
        var trash = Path.Combine(FileSystem.AppDataDirectory, ".trash");
        if (!Directory.Exists(trash))
        {
            await this.SafeAlertAsync("回收站为空", "没有需要清理的内容。", "好");
            return;
        }

        var confirmed = await this.SafeConfirmAsync("清理回收站",
            "将删除沙盒回收站里的所有文件，此操作不可撤销。", "清理", "取消");
        if (!confirmed) return;

        try
        {
            await Task.Run(() => Directory.Delete(trash, recursive: true));
            await this.SafeAlertAsync("完成", "回收站已清空。", "好");
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("清理失败", ex.Message, "好");
        }
    }
}
