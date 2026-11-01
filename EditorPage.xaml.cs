using Pebble.Models;
using Pebble.Services;

namespace Pebble;

public partial class EditorPage : ContentPage
{
    private readonly FileItem _item;

    public EditorPage(FileItem item)
    {
        InitializeComponent();

        _item = item;
        Title = item.Name;
        Body.Text = File.Exists(item.FullPath) ? File.ReadAllText(item.FullPath) : string.Empty;
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        try
        {
            File.WriteAllText(_item.FullPath, Body.Text ?? string.Empty);
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("保存失败", ex.Message, "好");
        }
    }
}
