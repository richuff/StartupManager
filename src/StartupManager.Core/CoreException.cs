namespace StartupManager.Core;

/// <summary>
/// Friendly, user-facing error raised by the core library (e.g. missing
/// administrator rights for HKLM / common-startup edits).
/// </summary>
public class StartupManagerException : Exception
{
    public StartupManagerException(string message) : base(message) { }
    public StartupManagerException(string message, Exception inner) : base(message, inner) { }
}
