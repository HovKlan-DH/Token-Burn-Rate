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
    /// across updates, so that is what gets registered when it exists. The Linux build ships
    /// as an AppImage instead, which never has that stub - see AppImagePath for its own,
    /// differently-shaped stability problem.
    /// </summary>
    private static string? ExecutablePath
    {
        get
        {
            if (OperatingSystem.IsLinux() && AppImagePath is { } appImage) return appImage;

            var path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path)) return null;

            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return null;

            return VelopackStub(path) ?? path;
        }
    }

    /// <summary>
    /// The AppImage file itself, when running as one - null otherwise (a Linux dev build run
    /// with `dotnet run`, or any non-Linux OS).
    ///
    /// An AppImage runs by mounting itself via FUSE and exec'ing the binary from inside that
    /// mount, so Environment.ProcessPath resolves to something like
    /// "/tmp/.mount_AbCdEf/usr/bin/Token-Burn-Rate" - a path that is unique to this one
    /// running process and stops existing the moment it exits, let alone across a reboot. An
    /// autostart entry written with that path silently launches nothing at the next login: the
    /// exec target is already gone by the time the session reads the .desktop file.
    ///
    /// AppImage's runtime is documented to set $APPIMAGE in every process it launches to the
    /// real, stable path of the .AppImage file - the same value Velopack's own Linux locator
    /// uses for this exact reason - but that turned out not to hold on the machine this was
    /// diagnosed on: a live, correctly mounted process there had no APPIMAGE entry anywhere
    /// in its environment (confirmed via /proc/&lt;pid&gt;/environ). $TARGET_APPIMAGE covers the
    /// same ground for a runtime invoked through a wrapper, and is equally free.
    ///
    /// Both are the runtime passing information along, though, so both can go missing
    /// together - which is why the real answer is MountSourcePath, asking the kernel what
    /// this process is running out of instead of trusting anything to have been handed down.
    /// Returning null rather than falling back to Environment.ProcessPath is deliberate: the
    /// mount path is precisely the wrong answer, and no autostart entry at all beats one that
    /// silently launches nothing.
    /// </summary>
    private static string? AppImagePath
    {
        get
        {
            if (EnvPath("APPIMAGE") is { } appImage && ResolveCandidatePath(appImage) is { } fromEnv) return fromEnv;
            if (EnvPath("TARGET_APPIMAGE") is { } target && ResolveCandidatePath(target) is { } fromTarget) return fromTarget;
            if (MountSourcePath() is { } fromMount) return fromMount;
            return null;
        }
    }

    private static string? EnvPath(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Turns a possibly-relative candidate path (an environment variable, or the mount
    /// absolute one and rejects it if it does not exist or still names a spot inside the
    /// transient mount - a stale or unusual runtime could hand back the mount path from any
    /// of these three sources, and registering that would recreate the exact bug this whole
    /// fallback chain exists to avoid.
    /// </summary>
    private static string? ResolveCandidatePath(string candidate)
    {
        try
        {
            var owd = EnvPath("OWD");   // runtime's "original working directory", when set
            var full = Path.IsPathRooted(candidate)
                ? candidate
                : Path.GetFullPath(candidate, owd ?? Directory.GetCurrentDirectory());

            if (!File.Exists(full)) return null;
            if (full.Contains("/.mount_", StringComparison.Ordinal)) return null;

            return full;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The file backing the FUSE mount this process is running out of - which, for an
    /// AppImage, is the .AppImage itself.
    ///
    /// The last resort, and the only one that cannot be defeated by a lost environment
    /// variable, because the kernel is recording it rather than the runtime passing it along:
    /// the AppImage runtime hands realpath("/proc/self/exe") to squashfuse as the mount
    /// source, and /proc/self/mountinfo reports it verbatim. Every path-shaped alternative
    /// was tried first and does not survive Velopack's own AppRun, a shell script ending in
    /// `exec "${EXEC}" "$@"` - that exec overwrites argv[0] with the in-mount binary, so
    /// neither /proc/self/cmdline nor $ARGV0 can ever name the .AppImage.
    ///
    /// Lines look like (fields elided):
    ///     462 30 0:48 / /tmp/.mount_abc123 ro,... - fuse.app /home/u/App.AppImage ro,...
    /// so the mount point is field 5, and the source is the second field after the " - "
    /// separator, whose position varies with the optional fields before it. Paths are escaped
    /// octally for the few characters that would otherwise break the field split.
    /// </summary>
    private static string? MountSourcePath()
    {
        try
        {
            // Only the mount this process is actually executing from counts: a machine can
            // have several AppImages mounted at once, and the others are not us.
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return null;

            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var separator = line.IndexOf(" - ", StringComparison.Ordinal);
                if (separator < 0) continue;

                var left = line[..separator].Split(' ');
                if (left.Length < 5) continue;

                var mountPoint = Unescape(left[4]);
                if (mountPoint.Length == 0 || !exe.StartsWith(mountPoint, StringComparison.Ordinal)) continue;

                var right = line[(separator + 3)..].Split(' ');
                if (right.Length < 2) continue;

                return ResolveCandidatePath(Unescape(right[1]));
            }
        }
        catch (Exception)
        {
            // No procfs, an unreadable mount table, or a layout this does not recognise:
            // autostart simply stays unavailable rather than registering a guess.
        }

        return null;

        // mountinfo escapes space, tab, newline and backslash as octal, and nothing else.
        static string Unescape(string field) => field
            .Replace("\\040", " ", StringComparison.Ordinal)
            .Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal)
            .Replace("\\134", "\\", StringComparison.Ordinal);
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

    /// <summary>
    /// Rewrites an autostart entry that exists but no longer points at this executable.
    ///
    /// Only ever repairs, never enables: an entry that is simply absent means the user turned
    /// autostart off, and resurrecting that would override a deliberate choice with a guess.
    /// The condition is exactly "the file is there and IsEnabled() disagrees with it", which
    /// is what a path gone stale looks like from here - an entry naming a moved .AppImage, or
    /// one of the /tmp/.mount_* paths written before AppImagePath learned to read the mount
    /// table. Both leave the user with autostart they switched on and a login that ignores it.
    /// </summary>
    public static void RepairIfStale()
    {
        try
        {
            if (ExecutablePath is null) return;

            var entry = OperatingSystem.IsWindows() ? null
                      : OperatingSystem.IsMacOS() ? MacPlistPath
                      : OperatingSystem.IsLinux() ? LinuxDesktopPath
                      : null;

            // Windows keeps its entry in the registry rather than a file, and stores a plain
            // absolute path that no update relocates, so there is nothing here to go stale.
            if (entry is null || !File.Exists(entry)) return;
            if (IsEnabled()) return;

            Set(true);
        }
        catch (Exception)
        {
            // A repair that cannot be made leaves things exactly as they were.
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

            var applied = IsEnabled() == enabled;
            if (OperatingSystem.IsLinux()) WriteLinuxDiagnostic(enabled, applied);
            return applied;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Records what the AppImage path resolution actually saw, beside the state file.
    ///
    /// Autostart failing on Linux is invisible from inside the app: it is a GUI process with
    /// no console, the failure only shows up a reboot later, and the inputs that decide the
    /// registered path (three environment variables and the raw argv[0]) are all gone by the
    /// time anyone can look. Two rounds of guessing at this from the outside each cost a full
    /// release-and-reboot cycle, so the app writes down its own reasoning instead. Rewritten
    /// on every toggle, so it always describes the entry currently on disk.
    /// </summary>
    private static void WriteLinuxDiagnostic(bool requested, bool applied)
    {
        try
        {
            var dir = Path.GetDirectoryName(AppState.Path);
            if (string.IsNullOrWhiteSpace(dir)) return;

            var text = $"""
                Token Burn Rate - Linux autostart diagnostic
                ===========================================
                When              : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
                Requested         : {(requested ? "enable" : "disable")}
                Reported as applied: {applied}

                Resolution inputs
                -----------------
                $APPIMAGE         : {Describe(Environment.GetEnvironmentVariable("APPIMAGE"))}
                $TARGET_APPIMAGE  : {Describe(Environment.GetEnvironmentVariable("TARGET_APPIMAGE"))}
                $OWD              : {Describe(Environment.GetEnvironmentVariable("OWD"))}
                mount source      : {Describe(MountSourcePath())}
                ProcessPath       : {Describe(Environment.ProcessPath)}
                CurrentDirectory  : {Describe(Directory.GetCurrentDirectory())}

                Outcome
                -------
                AppImagePath      : {Describe(AppImagePath)}
                ExecutablePath    : {Describe(ExecutablePath)}
                Autostart file    : {LinuxDesktopPath}
                File exists       : {File.Exists(LinuxDesktopPath)}

                """;

            File.WriteAllText(Path.Combine(dir, "autostart-diagnostic.txt"), text);
        }
        catch (Exception)
        {
            // Diagnostics must never break the thing they are diagnosing.
        }

        static string Describe(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "(not set)" : value;
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
    /// The characters the Desktop Entry spec reserves inside an Exec value. A path holding
    /// any of them has to be quoted; one holding none must not be, see EscapeExecArgument.
    /// </summary>
    private static readonly char[] ExecReservedChars =
        " \t\"'\\<>~|&;$*?#()`".ToCharArray();

    /// <summary>
    /// Renders a path for a Desktop Entry Exec key, quoting it only when the spec actually
    /// requires it.
    ///
    /// Quoting unconditionally is spec-legal but walks into three separate downstream bugs,
    /// and Ubuntu 22.04+ runs XDG autostart through systemd's generator rather than
    /// gnome-session, so it meets all of them: systemd's generator mishandles quoted paths
    /// when rewriting them into ExecStart=, does not unescape \$ and \` the way the spec
    /// says, and GLib's own get_executable() hands back the first field with the quotes
    /// still attached. None of that can bite a bare path, which is what every real install
    /// has - so the quotes now appear only for the paths that genuinely need them, where the
    /// escaping below (the spec requires ", `, $ and \ to be backslash-escaped inside a
    /// quoted argument) still applies.
    /// </summary>
    private static string EscapeExecArgument(string path)
    {
        if (path.IndexOfAny(ExecReservedChars) < 0) return path;

        var escaped = path
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("`", "\\`")
            .Replace("$", "\\$");

        return $"\"{escaped}\"";
    }
}
