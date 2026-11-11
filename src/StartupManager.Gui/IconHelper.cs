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
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrWhiteSpace(path)) return DefaultAppIcon.Image;
        return ShellIcon.GetSmallIcon(path) ?? DefaultAppIcon.Image;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// WPF value converter: maps a command line to the icon of its executable.
/// Used by the execution-queue grid, whose rows expose a Command string only.
/// When no real icon can be found (missing exe, unparseable command, or a shell
/// error), it returns a drawn placeholder instead of leaving the cell blank or
/// showing the system's white blank-document icon.
/// </summary>
public class CommandToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var cmd = value as string;
        if (string.IsNullOrWhiteSpace(cmd)) return DefaultAppIcon.Image;
        var (exe, _) = StartupEntry.SplitCommandLine(cmd);
        if (exe is null) return DefaultAppIcon.Image;
        return ShellIcon.GetSmallIcon(exe) ?? DefaultAppIcon.Image;
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
            // Only ask the shell for a *real* associated icon. For missing files or
            // unknown types SHGFI_USEFILEATTRIBUTES would hand back the generic blank
            // white document icon, which is exactly what we want to avoid — return
            // null instead so callers can show the drawn application placeholder.
            if (System.IO.File.Exists(path))
            {
                var fi = new SHFILEINFO();
                const uint flags = SHGFI_ICON | SHGFI_SMALLICON;
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
