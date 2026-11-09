using System.Runtime.InteropServices;
using System.Security.Principal;

namespace StartupManager.Core;

/// <summary>
/// Reports the identity facts that decide whether this process can write to a given
/// location: user SID, integrity level, elevation type and AppContainer status.
/// Used by <c>--whoami</c> on both the GUI and the MCP server so two processes can be
/// compared without a debugger.
/// </summary>
public static class ProcessIdentity
{
    private const int TokenElevationTypeInfo = 18;
    private const int TokenIntegrityLevelInfo = 25;
    private const int TokenIsAppContainer = 29;

    public static string Describe(string label)
    {
        var sb = new System.Text.StringBuilder();
        using var id = WindowsIdentity.GetCurrent();
        var proc = System.Diagnostics.Process.GetCurrentProcess();

        var principal = new WindowsPrincipal(id);
        var elevated = principal.IsInRole(WindowsBuiltInRole.Administrator);

        sb.AppendLine($"== {label} ==");
        sb.AppendLine($"  exe           : {Environment.ProcessPath}");
        sb.AppendLine($"  pid           : {proc.Id}");
        sb.AppendLine($"  user          : {id.Name}");
        sb.AppendLine($"  userSid       : {id.User?.Value}");
        sb.AppendLine($"  integrity     : {IntegrityLevel(id.Token)}");
        sb.AppendLine($"  inAdminsGroup : {elevated}");
        sb.AppendLine($"  elevationType : {ElevationType(id)}");
        sb.AppendLine($"  isAppContainer: {IsAppContainer(id)}");
        sb.AppendLine($"  sessionId     : {proc.SessionId}");
        return sb.ToString();
    }

    /// <summary>
    /// Read the mandatory label (integrity level) from the token. WindowsIdentity.Groups
    /// does not expose it, so query the token directly.
    /// </summary>
    private static string IntegrityLevel(IntPtr token)
    {
        try
        {
            // First call intentionally fails with ERROR_INSUFFICIENT_BUFFER to report the
            // required size — only len matters here, not the return value.
            GetTokenInformation(token, TokenIntegrityLevelInfo, IntPtr.Zero, 0, out var len);
            if (len <= 0) return "(n/a)";

            var buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevelInfo, buf, len, out _))
                    return "(n/a)";

                // TOKEN_MANDATORY_LABEL: SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; }
                var sidPtr = Marshal.ReadIntPtr(buf);
                var sid = new SecurityIdentifier(sidPtr);

                // The integrity RID is the last sub-authority of the label SID.
                var subs = sid.Value.Split('-');
                var last = subs.Length > 0 ? subs[^1] : "";

                return last switch
                {
                    "0" => $"Untrusted ({sid.Value})",
                    "4096" => $"Low ({sid.Value})",
                    "8192" => $"Medium ({sid.Value})",
                    "8448" => $"Medium Plus ({sid.Value})",
                    "12288" => $"High ({sid.Value})",
                    "16384" => $"System ({sid.Value})",
                    _ => sid.Value,
                };
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return "(n/a)"; }
    }

    private static string ElevationType(WindowsIdentity id)
    {
        try
        {
            // Probe call fails by design; only the returned length is meaningful.
            GetTokenInformation(id.Token, TokenElevationTypeInfo, IntPtr.Zero, 0, out var len);
            if (len <= 0) return "(n/a)";

            var buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetTokenInformation(id.Token, TokenElevationTypeInfo, buf, len, out _))
                    return "(n/a)";
                return Marshal.ReadInt32(buf) switch
                {
                    1 => "Default",
                    2 => "Full (elevated)",
                    3 => "Limited (UAC filtered)",
                    var v => $"Unknown({v})",
                };
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return "(n/a)"; }
    }

    private static string IsAppContainer(WindowsIdentity id)
    {
        try
        {
            var buf = Marshal.AllocHGlobal(4);
            try
            {
                if (!GetTokenInformation(id.Token, TokenIsAppContainer, buf, 4, out _)) return "(n/a)";
                return Marshal.ReadInt32(buf) != 0 ? "Yes" : "No";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return "(n/a)"; }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr token, int cls, IntPtr info, int len, out int retLen);
}
