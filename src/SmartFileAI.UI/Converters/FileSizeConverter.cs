using System.Globalization;
using System.Windows.Data;

namespace SmartFileAI.UI.Converters;

public sealed class FileSizeConverter : IValueConverter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long bytes || bytes < 0) return "-";
        if (bytes == 0) return "0 B";
        int unit = 0;
        double size = bytes;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {Units[unit]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
