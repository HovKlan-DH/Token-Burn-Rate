using Avalonia;
using System;

namespace TokenBurnRate
{
    internal class Program
    {
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

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
