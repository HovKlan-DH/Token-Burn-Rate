using System;
using System.IO;
using System.Runtime.Versioning;

namespace TokenBurnRate.Services;

/// <summary>
/// Registers the widget to start with the user's desktop session.
///
/// There is no cross-platform autostart standard, so each OS gets its own implementation.
/// All three write to a per-user location and none requires administrator rights:
///
///   Windows  HKCU\Software\Microsoft\Windows\CurrentVersion\Run
///   macOS    ~/Library/LaunchAgents/&lt;id&gt;.plist
///   Linux    ~/.config/autostart/&lt;id&gt;.desktop  (XDG Desktop Application Autostart spec)
///
/// Each is the conventional mechanism for its platform rather than a workaround, so the
/// per-OS branching here is the normal cost of supporting three desktops.
/// </summary>
public static class AutostartService
{
    private const string AppId = "TokenBurnRate";
    private const string DisplayName = "TokenBurnRate";

    /// <summary>False when the platform is unsupported or the executable cannot be located.</summary>
    public static bool IsSupported =>
        ExecutablePath is not null &&
        (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux());

    /// <summary>
    /// The published single-file executable. Under `dotnet run` this is the host rather
    /// than a standalone binary, which would register something unhelpful, so autostart is
    /// only offered for a real build.
    /// </summary>
    private static string? ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path)) return null;

            var name = Path.GetFileNameWithoutExtension(path);
            return name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? null : path;
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            if (ExecutablePath is null) return false;
            if (OperatingSystem.IsWindows()) return WindowsIsEnabled();
            if (OperatingSystem.IsMacOS()) return File.Exists(MacPlistPath);
            if (OperatingSystem.IsLinux()) return File.Exists(LinuxDesktopPath);
        }
        catch (Exception)
        {
            // A locked-down or unreadable profile is reported as "not enabled" rather than
            // taking the app down.
        }
        return false;
    }

    /// <summary>Returns true when the state afterwards matches what was asked for.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            if (ExecutablePath is null) return false;

            if (OperatingSystem.IsWindows()) WindowsSet(enabled);
            else if (OperatingSystem.IsMacOS()) MacSet(enabled);
            else if (OperatingSystem.IsLinux()) LinuxSet(enabled);
            else return false;

            return IsEnabled() == enabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- Windows -----------------------------------------------------------------------

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [SupportedOSPlatform("windows")]
    private static bool WindowsIsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        var value = key?.GetValue(AppId) as string;
        if (string.IsNullOrWhiteSpace(value)) return false;

        // Treat a stale entry pointing at a moved or renamed exe as not enabled, so
        // toggling it on rewrites the path instead of silently doing nothing.
        return value.Contains(ExecutablePath!, StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsSet(bool enabled)
    {
        // HKCU is the current user's own hive: writable without elevation, unlike the
        // machine-wide HKLM equivalent.
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (key is null) return;

        if (enabled) key.SetValue(AppId, $"\"{ExecutablePath}\"");
        else key.DeleteValue(AppId, throwOnMissingValue: false);
    }

    // ---- macOS -------------------------------------------------------------------------

    private static string MacPlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", $"com.{AppId.ToLowerInvariant()}.plist");

    private static void MacSet(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(MacPlistPath)) File.Delete(MacPlistPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(MacPlistPath)!);
        File.WriteAllText(MacPlistPath, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>com.{AppId.ToLowerInvariant()}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{System.Security.SecurityElement.Escape(ExecutablePath)}</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
            </dict>
            </plist>
            """);
    }

    // ---- Linux -------------------------------------------------------------------------

    private static string LinuxDesktopPath
    {
        get
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome))
            {
                configHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }
            return Path.Combine(configHome, "autostart", $"{AppId}.desktop");
        }
    }

    private static void LinuxSet(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(LinuxDesktopPath)) File.Delete(LinuxDesktopPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(LinuxDesktopPath)!);
        File.WriteAllText(LinuxDesktopPath, $"""
            [Desktop Entry]
            Type=Application
            Name={DisplayName}
            Exec="{ExecutablePath}"
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """);
    }
}
