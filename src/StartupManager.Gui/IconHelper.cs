using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StartupManager.Core.Models;

namespace StartupManager.Gui;

/// <summary>
/// WPF value converter: maps a file path to its associated 16x16 shell icon.
/// Uses SHGetFileInfo via P/Invoke so no NuGet package is required (offline build).
/// </summary>
public class FilePathToIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrWhiteSpace(path)) return null;
        return ShellIcon.GetSmallIcon(path);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// WPF value converter: maps a command line to the icon of its executable.
/// Used by the execution-queue grid, whose rows expose a Command string only.
/// </summary>
public class CommandToIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var cmd = value as string;
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        var (exe, _) = StartupEntry.SplitCommandLine(cmd);
        if (exe is null) return null;
        return ShellIcon.GetSmallIcon(exe);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// P/Invoke wrapper around SHGetFileInfo to extract associated file icons.
/// Results are cached by path so the same executable isn't queried repeatedly.
/// </summary>
internal static class ShellIcon
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Sync = new();

    public static ImageSource? GetSmallIcon(string path)
    {
        lock (Sync)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;
        }

        ImageSource? result = null;
        try
        {
            var fi = new SHFILEINFO();
            var flags = SHGFI_ICON | SHGFI_SMALLICON;
            if (!System.IO.File.Exists(path))
                flags |= SHGFI_USEFILEATTRIBUTES; // generic icon for missing files
            SHGetFileInfo(path, 0, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (fi.hIcon != IntPtr.Zero)
            {
                var bs = Imaging.CreateBitmapSourceFromHIcon(
                    fi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                result = bs;
                DestroyIcon(fi.hIcon);
            }
        }
        catch
        {
            result = null;
        }

        lock (Sync)
        {
            Cache[path] = result;
        }
        return result;
    }
}
