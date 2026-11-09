using System.Runtime.InteropServices;
using System.Text;

namespace StartupManager.Core.Providers;

/// <summary>
/// Minimal COM wrapper around IShellLink / IPersistFile so we can read and
/// write .lnk shortcut files in the Startup folders without any NuGet package.
/// </summary>
internal static class ShellLinkHelper
{
    private const uint STGM_READ = 0x00000000;

    public static (string Path, string Arguments)? Read(string lnkPath)
    {
        var link = (IShellLink)new ShellLink();
        try
        {
            var persist = (IPersistFile)link;
            persist.Load(lnkPath, STGM_READ);
            var sb = new StringBuilder(260);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var path = sb.ToString();
            var args = new StringBuilder(260);
            link.GetArguments(args, args.Capacity);
            return (path, args.ToString());
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    public static void Write(string lnkPath, string targetPath, string? arguments)
    {
        var link = (IShellLink)new ShellLink();
        try
        {
            link.SetPath(targetPath);
            if (!string.IsNullOrEmpty(arguments))
                link.SetArguments(arguments!);
            link.SetWorkingDirectory(Path.GetDirectoryName(targetPath) ?? "");
            var persist = (IPersistFile)link;
            persist.Save(lnkPath, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLink
    {
        void GetPath([Out] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string? ppszFileName);
    }
}
