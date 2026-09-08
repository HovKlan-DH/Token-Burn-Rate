using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenBurnRate.Services;

/// <summary>
/// The app's own state, kept in a single JSON file named after the executable and sitting
/// beside it, so a portable copy carries its history with it.
///
/// If that folder cannot be written - a read-only share, or Program Files - the file falls
/// back to %APPDATA%, because losing every day's opening balance would break the pacing
/// bars entirely. The GitHub token deliberately does not live here: it stays in %APPDATA%
/// with owner-only permissions, since a portable folder may be a USB stick or a share.
/// </summary>
public sealed class AppState
{
    // ---- persisted shape ---------------------------------------------------------------

    /// <summary>Window position, so the widget reopens where it was left.</summary>
    [JsonPropertyName("window")]
    public WindowState? Window { get; set; }

    /// <summary>Opening balances used to derive spend-per-day.</summary>
    [JsonPropertyName("pacing")]
    public PacingState? Pacing { get; set; }

    /// <summary>Which panels the user has collapsed.</summary>
    [JsonPropertyName("collapsed")]
    public CollapsedState? Collapsed { get; set; }

    /// <summary>Which panels the user has hidden outright via the context menu.</summary>
    [JsonPropertyName("hidden")]
    public HiddenState? Hidden { get; set; }

    /// <summary>
    /// Whether autostart has been configured at least once. Absent means the app has never
    /// run before, which is what triggers enabling it by default.
    /// </summary>
    [JsonPropertyName("autostartInitialised")]
    public bool? AutostartInitialised { get; set; }

    /// <summary>
    /// Whether the widget floats above other windows. Absent means never set, and the
    /// widget defaults to pinned - being always visible is the point of it.
    /// </summary>
    [JsonPropertyName("pinned")]
    public bool? Pinned { get; set; }

    /// <summary>
    /// Whether the close button minimises to the tray instead of exiting. Absent means
    /// never set, and the widget defaults to minimising - a monitor is meant to stay
    /// running, and quitting outright is the rarer intent.
    /// </summary>
    [JsonPropertyName("closeToTray")]
    public bool? CloseToTray { get; set; }

    /// <summary>
    /// Whether the "still running in the tray" notice has been shown. It is a one-off: the
    /// first minimise is the only one where the window vanishing is a surprise.
    /// </summary>
    [JsonPropertyName("trayNoticeShown")]
    public bool? TrayNoticeShown { get; set; }

    public sealed class WindowState
    {
        [JsonPropertyName("x")] public int X { get; set; }
        [JsonPropertyName("y")] public int Y { get; set; }
    }

    public sealed class CollapsedState
    {
        [JsonPropertyName("claude")] public bool Claude { get; set; }
        [JsonPropertyName("copilot")] public bool Copilot { get; set; }
        [JsonPropertyName("pacing")] public bool Pacing { get; set; }
    }

    public sealed class HiddenState
    {
        [JsonPropertyName("claude")] public bool Claude { get; set; }
        [JsonPropertyName("copilot")] public bool Copilot { get; set; }
        [JsonPropertyName("pacing")] public bool Pacing { get; set; }
    }

    public sealed class PacingState
    {
        [JsonPropertyName("day")] public string Day { get; set; } = "";
        [JsonPropertyName("dayOpening")] public double DayOpening { get; set; }
        [JsonPropertyName("weekStart")] public string WeekStart { get; set; } = "";
        [JsonPropertyName("weekOpening")] public double WeekOpening { get; set; }
    }

    // ---- location ----------------------------------------------------------------------

    private static readonly Lazy<string> _path = new(ResolvePath);
    private static readonly object _gate = new();

    public static string Path => _path.Value;

    /// <summary>
    /// Beside the executable, named after it (TokenBurnRate.exe -> TokenBurnRate.json),
    /// unless that directory is not writable.
    /// </summary>
    private static string ResolvePath()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var dir = System.IO.Path.GetDirectoryName(exe);
                var name = System.IO.Path.GetFileNameWithoutExtension(exe);
                if (!string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(name))
                {
                    var candidate = System.IO.Path.Combine(dir, name + ".json");
                    if (IsWritable(dir)) return candidate;
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the per-user location.
        }

        var appData = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TokenBurnRate");
        Directory.CreateDirectory(appData);
        return System.IO.Path.Combine(appData, "TokenBurnRate.json");
    }

    /// <summary>Probes the directory by creating and deleting a temporary file.</summary>
    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = System.IO.Path.Combine(dir, $".tbr-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- load / save -------------------------------------------------------------------

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppState Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(Path)) return new AppState();
                var json = File.ReadAllText(Path);
                return JsonSerializer.Deserialize<AppState>(json, Options) ?? new AppState();
            }
            catch (Exception)
            {
                return new AppState();      // a corrupt file must never stop the app
            }
        }
    }

    /// <summary>
    /// Applies a change and writes the whole file back. Read-modify-write keeps the two
    /// independent writers - the window and the pacing tracker - from clobbering each
    /// other's section.
    /// </summary>
    public static void Update(Action<AppState> mutate)
    {
        lock (_gate)
        {
            AppState state;
            try
            {
                state = File.Exists(Path)
                    ? JsonSerializer.Deserialize<AppState>(File.ReadAllText(Path), Options) ?? new AppState()
                    : new AppState();
            }
            catch (Exception)
            {
                state = new AppState();
            }

            mutate(state);

            try
            {
                WriteAtomic(JsonSerializer.Serialize(state, Options));
            }
            catch (Exception)
            {
                // Best effort: an unwritable file loses tracking, never the running app.
            }
        }
    }

    /// <summary>
    /// Writes via a temporary file and an atomic replace, so the state file is never left
    /// half-written.
    ///
    /// This matters more than it looks: the app now sits in the tray and is killed outright
    /// at logoff. A plain WriteAllText truncates first, so a kill in that window would
    /// leave unparseable JSON, and Load() answers that with a blank state - silently
    /// discarding the pacing opening balances, which cannot be reconstructed from anywhere.
    /// </summary>
    private static void WriteAtomic(string json)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (string.IsNullOrWhiteSpace(dir)) { File.WriteAllText(Path, json); return; }

        // Same directory as the target: File.Replace and a rename are only atomic within
        // one volume, and the temp folder may well be on another.
        var temp = System.IO.Path.Combine(dir, $".tbr-{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(temp, json);

            if (File.Exists(Path))
            {
                // No backup file: the temp copy is already complete on disk, so there is
                // nothing left to recover from a third one.
                File.Replace(temp, Path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, Path);
            }
        }
        catch (Exception)
        {
            // Replace fails on some filesystems - notably a few network shares and FUSE
            // mounts. A direct write gives up atomicity but keeps the update, which is the
            // better trade on a path this rare. If that fails too the caller's catch takes
            // over and the update is dropped.
            File.WriteAllText(Path, json);
        }
        finally
        {
            // A crash between write and replace leaves the temp file behind; clear it so
            // they cannot accumulate beside the executable.
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
        }
    }
}
