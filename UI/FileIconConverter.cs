using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DevShell.Interop;
using DevShell.Services;

namespace DevShell.UI;

/// <summary>
/// Resolves a search hit's shell icon while the row is being realized. Icons are cached
/// inside <see cref="ShellIcons"/>, so scrolling a result list stays cheap.
/// </summary>
internal sealed class FileIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            FileEntry entry => ShellIcons.ForEntry(entry.Path, entry.IsDirectory),
            string path => ShellIcons.ForPath(path),
            _ => null,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
