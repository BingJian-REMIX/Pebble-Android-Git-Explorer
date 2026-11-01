using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Pebble.Models;

/// <summary>文件在 Git 工作区中的状态。数值用于目录状态聚合时的优先级比较。</summary>
public enum GitFileStatus
{
    Unmodified = 0,
    Modified = 1,
    New = 2,
    Deleted = 3
}

/// <summary>文件浏览器列表项。所有路径均为绝对路径，RelativePath 始终使用 '/' 分隔。</summary>
public sealed class FileItem : INotifyPropertyChanged
{
    public string Name { get; set; } = string.Empty;

    /// <summary>绝对路径，用于打开文件 / 进入目录。</summary>
    public string FullPath { get; set; } = string.Empty;

    /// <summary>相对仓库根目录的路径，'/' 分隔，根目录为空字符串。</summary>
    public string RelativePath { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    /// <summary>供 XAML 里切换图标用（两个 Path 各自 IsVisible，避免写 DataTrigger）。</summary>
    public bool IsFile => !IsDirectory;

    public GitFileStatus GitStatus { get; set; } = GitFileStatus.Unmodified;

    /// <summary>右侧徽章文字：M / U / D，无变更时为空。</summary>
    public string StatusLetter => GitStatus switch
    {
        GitFileStatus.Modified => "M",
        GitFileStatus.New => "U",
        GitFileStatus.Deleted => "D",
        _ => string.Empty
    };

    public bool HasChanges => GitStatus != GitFileStatus.Unmodified;

    /// <summary>抽屉里的副标题：只给目录部分，避免和上一行的文件名重复。</summary>
    public string FolderLabel
    {
        get
        {
            var index = RelativePath.LastIndexOf('/');
            return index < 0 ? string.Empty : RelativePath[..(index + 1)];
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RowBackground));
        }
    }

    /// <summary>选中行底色。半透明紫在亮/暗两套主题下都成立，所以不走 AppThemeBinding。</summary>
    public Color RowBackground => _isSelected ? Color.FromArgb("#2E7C5CFF") : Colors.Transparent;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => $"{RelativePath} [{GitStatus}]";
}
