using System;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using SwiftGet.Core.Models;

namespace SwiftGet.App.Converters;

public sealed class FileSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language)
    {
        if (value is not long bytes || bytes < 0)
        {
            return "Unknown";
        }

        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024m) >= 1 && counter < suffixes.Length - 1)
        {
            number /= 1024m;
            counter++;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", number, suffixes[counter]);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class SpeedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language)
    {
        if (value is not double bytesPerSec || bytesPerSec <= 0)
        {
            return "0 KB/s";
        }

        if (bytesPerSec < 1024)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0} B/s", bytesPerSec);
        }

        if (bytesPerSec < 1024 * 1024)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.#} KB/s", bytesPerSec / 1024.0);
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} MB/s", bytesPerSec / (1024.0 * 1024.0));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class TimeRemainingConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language)
    {
        if (value is not TimeSpan timeSpan || timeSpan == TimeSpan.Zero || timeSpan.TotalSeconds <= 0)
        {
            return "--:--:--";
        }

        if (timeSpan.TotalHours >= 24)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}d {1:00}:{2:00}", (int)timeSpan.TotalDays, timeSpan.Hours, timeSpan.Minutes);
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", (int)timeSpan.TotalHours, timeSpan.Minutes, timeSpan.Seconds);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class PercentageConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language)
    {
        if (value is double pct)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0}%", pct);
        }

        return "0.0%";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class StatusToBrushConverter : IValueConverter
{
    private static SolidColorBrush? _blueBrush;
    private static SolidColorBrush? _greenBrush;
    private static SolidColorBrush? _amberBrush;
    private static SolidColorBrush? _redBrush;
    private static SolidColorBrush? _purpleBrush;
    private static SolidColorBrush? _cyanBrush;
    private static SolidColorBrush? _orangeBrush;
    private static SolidColorBrush? _tealBrush;
    private static SolidColorBrush? _grayBrush;

    public static SolidColorBrush? GetBrush(DownloadStatus status)
    {
        try
        {
            return status switch
            {
                DownloadStatus.Downloading => _blueBrush ??= new SolidColorBrush(ColorHelper.FromArgb(255, 0, 120, 215)),
                DownloadStatus.Completed => _greenBrush ??= new SolidColorBrush(ColorHelper.FromArgb(255, 16, 124, 65)),
                DownloadStatus.Seeding => _tealBrush ??= new SolidColorBrush(ColorHelper.FromArgb(255, 13, 148, 136)),
                DownloadStatus.Paused => _amberBrush ??= new SolidColorBrush(ColorHelper.FromArgb(255, 202, 138, 4)),
                DownloadStatus.Failed => _redBrush ??= new SolidColorBrush(ColorHelper.FromArgb(255, 232, 17, 35)),
                DownloadStatus.Queued => _purpleBrush ??= new SolidColorBrush(ColorHelper.FromArgb(255, 128, 90, 213)),
                DownloadStatus.FetchingMetadata => _cyanBrush ??= new SolidColorBrush(ColorHelper.FromArgb(255, 0, 153, 188)),
                DownloadStatus.Checking => _orangeBrush ??= new SolidColorBrush(ColorHelper.FromArgb(255, 217, 119, 6)),
                _ => _grayBrush ??= new SolidColorBrush(Colors.Gray)
            };
        }
        catch
        {
            return null;
        }
    }

    public object? Convert(object? value, Type targetType, object? parameter, string language) =>
        value is DownloadStatus status ? GetBrush(status) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, string language)
    {
        bool isTrue = value is true;
        if (Invert)
        {
            isTrue = !isTrue;
        }

        return isTrue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, string language) =>
        throw new NotSupportedException();
}
