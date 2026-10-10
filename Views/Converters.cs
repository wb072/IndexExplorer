using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace IndexExplorer.Views;

public sealed class BoolToVisConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>묶음 이름(폴더 경로)에서 폴더 이름만: "D:\업무\견적" → "견적". 경로가 아니면 그대로.</summary>
public sealed class PathLeafConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var s = value?.ToString() ?? "";
        if (!s.Contains(":\\")) return s.Length == 0 ? "(위치 없음)" : s;
        var leaf = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(s));
        return string.IsNullOrEmpty(leaf) ? s : leaf;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>묶음 이름(폴더 경로)의 상위 경로: "D:\업무\견적" → "D:\업무". 경로가 아니면 빈 문자열.</summary>
public sealed class PathParentConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var s = value?.ToString() ?? "";
        return s.Contains(":\\") ? System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(s)) ?? "" : "";
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>묶음 이름이 폴더 경로이면 보이게 (폴더 아이콘·열기 버튼).</summary>
public sealed class IsPathToVisConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (value?.ToString() ?? "").Contains(":\\") ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
