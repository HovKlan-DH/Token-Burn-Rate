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
    private const string AppId = "Token-Burn-Rate";
    private const string DisplayName = "Token Burn Rate";

    /// <summary>The id used before the app was renamed - see <see cref="RemoveLegacyEntry"/>.</summary>
    private const string LegacyAppId = "TokenBurnRate";

    /// <summary>False when the platform is unsupported or the executable cannot be located.</summary>
    public static bool IsSupported =>
        ExecutablePath is not null &&
        (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux());

    /// <summary>
    /// The running executable. Under `dotnet run` this is the host rather than the app's
    /// own binary, which would register something unhelpful, so autostart is only offered
    /// for a real build.
    ///
    /// On a Velopack install the running exe is the versioned copy inside the current
    /// "app-x.y.z" folder, which an update replaces; the stub one level up keeps its path
    /// across updates, so that is what gets registered when it exists.
    /// </summary>
    private static string? ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path)) return null;

            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return null;

            return VelopackStub(path) ?? path;
        }
    }

    /// <summary>
    /// The update-stable launcher beside a Velopack install's versioned program folder, or
    /// null when this is not such an install. Velopack keeps a stub named after the app one
    /// level above the "current"/"app-x.y.z" directory the exe runs from.
    /// </summary>
    private static string? VelopackStub(string exePath)
    {
        try
        {
            var parent = Directory.GetParent(Path.GetDirectoryName(exePath)!)?.FullName;
            if (parent is null) return null;
            if (!Directory.Exists(Path.Combine(parent, ".velopack"))) return null;

            var stub = Path.Combine(parent, Path.GetFileName(exePath));
            return File.Exists(stub) ? stub : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            if (ExecutablePath is null) return false;
            if (OperatingSystem.IsWindows()) return WindowsIsEnabled();

            // Existence alone is not enough on either of these: an entry left behind by a
            // copy that has since moved would report autostart as on while launching
            // nothing. Same rule as the Windows branch - it counts only if it points here.
            if (OperatingSystem.IsMacOS()) return FileNamesThisExecutable(MacPlistPath);
            if (OperatingSystem.IsLinux()) return FileNamesThisExecutable(LinuxDesktopPath);
        }
        catch (Exception)
        {
            // A locked-down or unreadable profile is reported as "not enabled" rather than
            // taking the app down.
        }
        return false;
    }

    /// <summary>
    /// Deletes the autostart entry written under the pre-rename id.
    ///
    /// The rename changed the registry value name, the plist label and the .desktop
    /// filename, so an existing install's old entry is no longer the one this service reads
    /// or writes. Left in place it would still fire at every login, launching the exe path it
    /// was written with, while the toggle in the menu reported autostart as off - and turning
    /// the toggle on then off again would not clear it, because Set() only ever touches the
    /// new id. The entry is removed rather than rewritten: whether autostart should be on is
    /// already answered by <see cref="AppState.AutostartInitialised"/> and the new-id entry.
    ///
    /// Safe to call on every launch - it is a no-op once the old entry is gone.
    /// </summary>
    public static void RemoveLegacyEntry()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                key?.DeleteValue(LegacyAppId, throwOnMissingValue: false);
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (File.Exists(LegacyMacPlistPath)) File.Delete(LegacyMacPlistPath);
            }
            else if (OperatingSystem.IsLinux())
            {
                if (File.Exists(LegacyLinuxDesktopPath)) File.Delete(LegacyLinuxDesktopPath);
            }
        }
        catch (Exception)
        {
            // Best effort: a locked hive or read-only profile leaves the stale entry, which
            // is no worse than not having tried.
        }
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

    /// <summary>
    /// Whether an existing autostart file refers to the executable running now.
    ///
    /// Both the plist and the .desktop file embed the path in markup this does not need to
    /// parse to answer the only question being asked - is this entry ours, or a leftover
    /// from a copy that has moved. A substring test is sound here in a way it is not for
    /// the registry: these files are written solely by this method, so the path appears in
    /// exactly one place and in a form we chose.
    /// </summary>
    private static bool FileNamesThisExecutable(string path)
    {
        if (!File.Exists(path)) return false;

        try
        {
            var content = File.ReadAllText(path);

            // Each writer escapes the path its own way - XML entities in the plist,
            // backslashes in the .desktop Exec key - so the raw path may not appear
            // literally. Test the escaped forms too rather than reporting a perfectly good
            // entry as stale because it contained a "$" or an "&".
            return content.Contains(ExecutablePath!, StringComparison.Ordinal)
                || content.Contains(EscapeExecArgument(ExecutablePath!), StringComparison.Ordinal)
                || content.Contains(System.Security.SecurityElement.Escape(ExecutablePath!),
                                    StringComparison.Ordinal);
        }
        catch (Exception)
        {
            // Unreadable but present: assume it is ours rather than offering to rewrite a
            // file that cannot be read back.
            return true;
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

        // Compare the path the entry actually points at, not a substring of the line. A
        // Contains test matches a stale "...\Token-Burn-Rate.exe.bak" or a wrapper that names
        // this exe as an argument, and would then report autostart as on when it is not.
        return string.Equals(ParseExecutable(value!), ExecutablePath!,
                             StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the program path from a Run entry, which is a command line rather than a
    /// bare path: normally <c>"C:\dir\App.exe"</c>, but arguments may follow, and an entry
    /// written by something else may not be quoted at all.
    /// </summary>
    private static string ParseExecutable(string command)
    {
        var value = command.Trim();
        if (value.Length == 0) return value;

        if (value[0] == '"')
        {
            var end = value.IndexOf('"', 1);
            return end < 0 ? value[1..] : value[1..end];
        }

        // Unquoted: the path runs to the first space. A path with spaces and no quotes is
        // ambiguous by nature - Windows itself guesses here - and is not something this app
        // ever writes, so the simple reading is the right one.
        var space = value.IndexOf(' ');
        return space < 0 ? value : value[..space];
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

    private static string MacPlistPath => MacPlistPathFor(AppId);

    private static string LegacyMacPlistPath => MacPlistPathFor(LegacyAppId);

    private static string MacPlistPathFor(string id) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", $"com.{id.ToLowerInvariant()}.plist");

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

    private static string LinuxDesktopPath => LinuxDesktopPathFor(AppId);

    private static string LegacyLinuxDesktopPath => LinuxDesktopPathFor(LegacyAppId);

    private static string LinuxDesktopPathFor(string id)
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            configHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(configHome, "autostart", $"{id}.desktop");
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
            Exec={EscapeExecArgument(ExecutablePath!)}
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """);
    }

    /// <summary>
    /// Quotes a path for a Desktop Entry Exec key, per the XDG spec.
    ///
    /// Plain double quotes are not enough: inside a quoted argument the spec requires
    /// <c>"</c>, <c>`</c>, <c>$</c> and <c>\</c> to be escaped with a backslash. An
    /// unescaped path containing any of them yields an entry the session manager either
    /// ignores or mis-splits, and autostart then silently does nothing.
    /// </summary>
    private static string EscapeExecArgument(string path)
    {
        var escaped = path
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("`", "\\`")
            .Replace("$", "\\$");

        return $"\"{escaped}\"";
    }
}
