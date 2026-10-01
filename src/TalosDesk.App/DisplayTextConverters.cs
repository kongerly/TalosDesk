using System.Globalization;
using System.Windows.Data;
using TalosDesk.Core.Configuration;

namespace TalosDesk.App;

public sealed class CommandKindToChineseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is CommandKind.Service ? "服务" : "任务";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class OutputStreamToChineseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() switch
        {
            "stdout" => "标准输出",
            "stderr" => "诊断输出",
            _ => value?.ToString() ?? string.Empty
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class CommandGroupModeToChineseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is CommandGroupExecutionMode.Sequential ? "顺序执行" : "同时执行";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class UtcToLocalTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTimeOffset timestamp ? CrashTimestampFormatter.FormatLocal(timestamp) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

internal static class CrashTimestampFormatter
{
    internal static string FormatLocal(
        DateTimeOffset timestamp,
        TimeZoneInfo? timeZone = null,
        bool includeMilliseconds = false)
    {
        var local = TimeZoneInfo.ConvertTime(timestamp, timeZone ?? TimeZoneInfo.Local);
        var format = includeMilliseconds
            ? "yyyy-MM-dd HH:mm:ss.fff"
            : "yyyy-MM-dd HH:mm:ss";
        return local.ToString(format, CultureInfo.InvariantCulture);
    }
}
