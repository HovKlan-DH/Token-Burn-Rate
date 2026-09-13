using Avalonia;
using System;
using System.Threading;
using Velopack;

namespace TokenBurnRate
{
    internal class Program
    {
        /// <summary>
        /// Held until the process exits, releasing the single-instance claim - see
        /// <see cref="TryAcquireSingleInstance"/>. Static purely to keep it reachable: were it
        /// a local, the GC could finalize it mid-run, and Mutex's finalizer closes the handle
        /// and drops the lock while the app is still using it.
        /// </summary>
        private static Mutex? _singleInstanceMutex;

        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            // First thing of all, so a failure during startup is recorded too.
            Services.CrashLog.Install();

            try
            {
                // Must run before Avalonia: on first launch after an update this is what
                // finishes install/uninstall/update bookkeeping and, for some verbs, exits
                // the process immediately without ever starting the UI. Inside the try so a
                // failure here is recorded the same as any other startup crash.
                VelopackApp.Build().Run();

                // Only after Run(). Velopack performs its bookkeeping by relaunching this
                // same exe with a hook verb (--veloapp-install/-updated/-obsolete/-uninstall),
                // and --veloapp-obsolete deliberately runs while the outgoing version is still
                // up and still holding the mutex. Claiming it any earlier would abort those
                // hook processes before Run() could service them, silently skipping the
                // bookkeeping they exist to do.
                if (!TryAcquireSingleInstance()) return;

                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                // The UI thread's own exceptions arrive here rather than at the AppDomain
                // hook, so this is not redundant with Install().
                Services.CrashLog.Record(ex, "fatal");
                throw;
            }
        }

        /// <summary>
        /// Claims the right to be the running instance, or reports that another already holds
        /// it. A refused launch exits silently - no window, no dialog - which is the whole
        /// point, so the log line is the only trace and is what separates "suppressed as a
        /// duplicate" from "crashed on startup" in a report.
        ///
        /// The wait is not instant because an auto-update relaunches the new version around
        /// the outgoing one's exit (<c>ApplyUpdatesAndRestart</c>), and the old process only
        /// releases its handle at teardown. A zero timeout would let the new instance lose
        /// that race and exit, leaving the user with the update applied and nothing running.
        /// Waiting briefly covers the handover; anything longer would just delay a genuine
        /// second launch, which should feel like nothing happened.
        /// </summary>
        private static bool TryAcquireSingleInstance()
        {
            try
            {
                _singleInstanceMutex = new Mutex(initiallyOwned: false, name: SingleInstanceName());

                try
                {
                    if (_singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(5))) return true;
                }
                catch (AbandonedMutexException)
                {
                    // The previous owner died without releasing - killed from Task Manager, or
                    // a hook Velopack cut off at its timeout. The lock is ours regardless, and
                    // refusing to start here would leave the app permanently unlaunchable.
                    return true;
                }

                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                Services.AppLog.Info("Startup: another instance is already running, exiting");
                return false;
            }
            catch (Exception ex)
            {
                // Named mutexes are a thinner guarantee off Windows - .NET backs them with a
                // file under a temp directory, and macOS caps the underlying primitive's name
                // length - so creation can fail outright on the three non-Windows RIDs this
                // ships for. Running a second copy beats refusing to start at all, which is
                // why this is not fatal.
                Services.AppLog.Warn($"Startup: single-instance check unavailable: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// Scoped to the install location, so a build run from the working tree and an
        /// installed copy do not lock each other out - debugging while the real one sits in
        /// the tray is routine, and a single global name would make one of them refuse to
        /// start with no visible reason. Velopack's versioned folder is deliberately excluded
        /// from the path so an update does not hand the new version a different name and let
        /// it run alongside the old one.
        /// </summary>
        private static string SingleInstanceName()
        {
            // The state file's folder, which already answers this exact question: it is beside
            // the exe for a portable copy, and the fixed per-user folder for a Velopack
            // install, whose versioned exe directory changes on every update.
            var location = System.IO.Path.GetDirectoryName(Services.AppState.Path)
                           ?? Services.AppState.AppFolderName;

            // Hashed rather than embedded: a path may hold characters the name cannot (a
            // backslash makes Windows read the rest as a namespace) and can outrun the length
            // limits, which are far tighter on macOS than on Windows.
            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(location.ToLowerInvariant()));

            var suffix = Convert.ToHexString(hash, 0, 8);

            // "Local\" confines the name to this login session on Windows, so two users on one
            // machine each get their own instance. It is meaningless elsewhere, and the plain
            // name is what non-Windows platforms get.
            return OperatingSystem.IsWindows()
                ? $"Local\\{Services.AppState.AppFolderName}-{suffix}"
                : $"{Services.AppState.AppFolderName}-{suffix}";
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
