using System.Globalization;
using Pebble.Models;

namespace Pebble.Converters;

/// <summary>
/// 把 GitFileStatus 转成徽章颜色。
/// ConverterParameter：bg 取底色，其它取文字色。
/// 底色低透明、文字中饱和，亮/暗两套主题下都看得清。
/// </summary>
public sealed class GitStatusColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value is GitFileStatus s ? s : GitFileStatus.Unmodified;
        var isBackground = parameter as string == "bg";

        return status switch
        {
            GitFileStatus.Modified => Color.FromArgb(isBackground ? "#24EF4444" : "#EF4444"),
            GitFileStatus.New => Color.FromArgb(isBackground ? "#2222C55E" : "#16A34A"),
            GitFileStatus.Deleted => Color.FromArgb(isBackground ? "#24F59E0B" : "#D97706"),
            _ => Colors.Transparent
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
