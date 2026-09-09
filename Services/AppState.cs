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
/// back to the per-user local app data folder (%LOCALAPPDATA% / ~/.local/share, via
/// SpecialFolder.LocalApplicationData - not ApplicationData/~/.config, which is for
/// roaming config rather than an app's own data, and is where Windows and Linux otherwise
/// diverge), because losing every day's opening balance would break the pacing bars
/// entirely. The GitHub token deliberately does not live here: it stays in that same
/// per-user folder with owner-only permissions, since a portable folder may be a USB stick
/// or a share.
///
/// A Velopack install also uses that folder rather than the beside-the-exe location: there
/// the exe lives in a versioned "current"/"app-x.y.z" folder that each update replaces
/// wholesale, so state written beside it would be discarded on every auto-update.
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

    /// <summary>Which bar the tray's live ring icon follows, and what colour it draws in.</summary>
    [JsonPropertyName("icon")]
    public IconState? Icon { get; set; }

    /// <summary>Per-panel accent color overrides.</summary>
    [JsonPropertyName("colors")]
    public ColorsState? Colors { get; set; }

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

    /// <summary>
    /// The whole widget's text/UI scale, set from "Make bigger"/"Make smaller" in the
    /// context menu. Absent means never set, and the widget defaults to 1.0 - its
    /// as-designed size.
    /// </summary>
    [JsonPropertyName("fontScale")]
    public double? FontScale { get; set; }

    /// <summary>
    /// Seconds between refreshes. Nothing in the UI offers this: it exists for the rare
    /// case of wanting a slower or faster poll than the default, set by editing the file.
    ///
    /// Written back with the default when absent, so the key is always present to be
    /// edited - a setting that has to be typed from memory before it exists is one nobody
    /// will find, and the file is the only place this one is visible at all.
    /// </summary>
    [JsonPropertyName("refreshSeconds")]
    public int? RefreshSeconds { get; set; }

    /// <summary>
    /// The trailing comment written after <see cref="RefreshSeconds"/>, explaining what
    /// the number costs.
    ///
    /// This is the one setting with no UI, so the file is the only place a reader can be
    /// told why the default is what it is - without it the key is a bare number that looks
    /// arbitrary, and the obvious edit is downwards. It is a real "//" comment rather than
    /// a sibling string key so that it reads as annotation rather than as data the app
    /// might act on. See <see cref="Annotate"/> for how it survives a format that cannot
    /// serialize comments.
    /// </summary>
    public const string RefreshNote =
        "seconds between polls of both services. Default is 180. Lower values are accepted "
        + "down to 5, at your own risk: the Anthropic usage endpoint is undocumented and "
        + "rate-limits hard, and an HTTP 429 there can outlast the poll that caused it, "
        + "so it is not recommended to go lower.";

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

    public sealed class IconState
    {
        /// <summary>Which bar the tray ring follows; "max" for whichever is highest.</summary>
        [JsonPropertyName("source")] public string Source { get; set; } = "max";

        /// <summary>"accent" to take the source panel's colour, or an explicit #RRGGBB.</summary>
        [JsonPropertyName("colour")] public string Colour { get; set; } = "accent";
    }

    /// <summary>
    /// Per-panel accent overrides: "default" keeps the built-in color, an explicit
    /// #RRGGBB replaces it everywhere that panel's accent appears - the section header, the
    /// bar fill while inside budget, and the tray ring when that panel's bar is the one
    /// tracked (see MainViewModel.ResolveIconColour).
    /// </summary>
    public sealed class ColorsState
    {
        [JsonPropertyName("claude")] public string Claude { get; set; } = "default";
        [JsonPropertyName("copilot")] public string Copilot { get; set; } = "default";
        [JsonPropertyName("pacing")] public string Pacing { get; set; } = "default";
    }

    // ---- location ----------------------------------------------------------------------

    private static readonly Lazy<string> _path = new(ResolvePath);
    private static readonly object _gate = new();

    public static string Path => _path.Value;

    /// <summary>
    /// Beside the executable, named after it (TokenBurnRate.exe -> TokenBurnRate.json),
    /// unless that directory is not writable or the app is Velopack-installed - see the
    /// class comment for why an installed build must not write beside its exe.
    /// </summary>
    private static string ResolvePath()
    {
        try
        {
            var exe = IsVelopackInstalled ? null : Environment.ProcessPath;
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
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TokenBurnRate");
        Directory.CreateDirectory(appData);
        return System.IO.Path.Combine(appData, "TokenBurnRate.json");
    }

    /// <summary>
    /// Whether this build is running from a Velopack install. Detected from the layout it
    /// creates - the exe sits in a "current" or "app-x.y.z" folder next to the
    /// ".velopack" bookkeeping directory - rather than by asking Velopack, so resolving a
    /// path stays free of package state and cannot throw on an unpackaged build.
    /// </summary>
    private static bool IsVelopackInstalled
    {
        get
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Environment.ProcessPath);
                if (string.IsNullOrWhiteSpace(dir)) return false;

                var parent = Directory.GetParent(dir)?.FullName;
                return parent is not null && Directory.Exists(System.IO.Path.Combine(parent, ".velopack"));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Probes the directory by creating and deleting a temporary file.
    ///
    /// DeleteOnClose is the normal path, but it does not fire when the process is killed -
    /// and this app is killed outright at logoff - so the probe is also deleted explicitly
    /// and any leftover from an earlier kill is swept. Probes carry their own prefix rather
    /// than sharing WriteAtomic's: sweeping on that pattern could delete a temp file another
    /// instance was mid-write to, which is exactly what the atomic write exists to prevent.
    /// </summary>
    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = System.IO.Path.Combine(dir, $".tbr-probe-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            try { if (File.Exists(probe)) File.Delete(probe); } catch (Exception) { }

            SweepStaleProbes(dir);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes probe files a previous kill left behind, so they cannot accumulate beside
    /// the executable. Best effort throughout: a probe another instance holds open right
    /// now simply fails to delete and is swept by whichever run comes after it.
    /// </summary>
    private static void SweepStaleProbes(string dir)
    {
        try
        {
            foreach (var stale in Directory.EnumerateFiles(dir, ".tbr-probe-*.tmp"))
            {
                try { File.Delete(stale); } catch (Exception) { }
            }
        }
        catch (Exception)
        {
            // Enumeration itself can fail on an odd filesystem; the probe already answered
            // the question this method was called to support.
        }
    }

    // ---- load / save -------------------------------------------------------------------

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // The file carries a "//" note against refreshSeconds - see Annotate. Strict JSON
        // has no comments, so without this the reader throws on the app's own output and
        // Load() answers that with a blank state, discarding the pacing opening balances.
        ReadCommentHandling = JsonCommentHandling.Skip,

        // A hand-edited file is the whole point of this one setting; a stray trailing
        // comma is the commonest way to typo one, and rejecting the file over it would
        // silently reset far more than the setting being edited.
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Puts the <see cref="RefreshNote"/> comment back after the refreshSeconds line.
    ///
    /// System.Text.Json reads comments but will not write them, and there is no writer
    /// hook for one - so the only way to keep an annotated file is to add it to the text
    /// after serializing. Applied on every write, since the serializer has just discarded
    /// whatever comment the file previously held.
    ///
    /// Failure is not an error: a line that does not match simply goes un-annotated, which
    /// costs a comment rather than the state it was describing.
    /// </summary>
    private static string Annotate(string json)
    {
        const string key = "\"refreshSeconds\":";

        var i = json.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return json;

        // End of that line, wherever the writer put the newline - and never past it, so a
        // key absent from the output cannot drag the comment onto an unrelated line.
        var end = json.IndexOf('\n', i);
        if (end < 0) end = json.Length;

        var line = json[i..end].TrimEnd('\r');
        return json[..i] + line + "  // " + OneLine(RefreshNote) + json[end..];
    }

    /// <summary>
    /// Flattens a note to a single line, so splicing it after "//" cannot swallow the rest
    /// of the document.
    ///
    /// A "//" comment ends at the first line break: a note containing one would leave the
    /// remaining JSON - the closing brace included - on the far side of a comment that has
    /// already ended, producing a file Load() rejects and answers with a blank state,
    /// discarding the pacing opening balances. RefreshNote is one logical line today, but
    /// it is a wrapped literal an editor would naturally break, so the guard lives here
    /// rather than in a comment asking the next editor not to.
    /// </summary>
    private static string OneLine(string note) =>
        note.IndexOfAny(NewlineChars) < 0
            ? note
            : string.Join(" ", note.Split(NewlineChars,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static readonly char[] NewlineChars = { '\r', '\n' };

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
                WriteAtomic(Annotate(JsonSerializer.Serialize(state, Options)));
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
