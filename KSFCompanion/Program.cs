using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace KsfCompanion
{
    static class Program
    {
        public const string AppName = "KSF Companion";
        /// <summary>The name of the app on Linux: its folders, its desktop entry, its command.</summary>
        public const string AppId = "ksf-companion";

        static string XdgDir(string variable, string fallback)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            return !string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value) ? value : Path.Combine(Home, fallback);
        }

        public static string Home
        {
            get
            {
                var home = Environment.GetEnvironmentVariable("HOME");
                return !string.IsNullOrEmpty(home) ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
        }

        /// <summary>
        /// Settings and the play-later list live in ~/.config/ksf-companion (XDG_CONFIG_HOME) so they are easy to find,
        /// or in KSFC_DATA_DIR for a test copy.
        /// </summary>
        public static string DataDir
        {
            get
            {
                var overridden = Environment.GetEnvironmentVariable("KSFC_DATA_DIR");
                return string.IsNullOrWhiteSpace(overridden) ? Path.Combine(XdgDir("XDG_CONFIG_HOME", ".config"), AppId) : overridden;
            }
        }

        /// <summary>
        /// What's kept from ksf.surf (map pictures, records, the map list, the maps you've finished) lives in
        /// ~/.cache/ksf-companion (XDG_CACHE_HOME) - or, for a test copy pointed at another data folder, in a "cache" folder
        /// inside that, so it never touches the real ones.
        /// </summary>
        public static string CacheDir => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KSFC_DATA_DIR"))
            ? Path.Combine(XdgDir("XDG_CACHE_HOME", ".cache"), AppId)
            : Path.Combine(DataDir, "cache");

        /// <summary>Where the app's socket and lock go: XDG_RUNTIME_DIR, or the cache folder without one.</summary>
        public static string RuntimeDir
        {
            get
            {
                var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
                return !string.IsNullOrWhiteSpace(runtime) && Directory.Exists(runtime) ? runtime : CacheDir;
            }
        }

        /// <summary>Set for a test copy pointed at another data folder: it gets its own instance (and socket).</summary>
        public static string InstanceScope => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KSFC_DATA_DIR"))
            ? ""
            : "." + Hash(Path.GetFullPath(DataDir));

        /// <summary>A short name for a path (8 hex digits).</summary>
        internal static string Hash(string text)
        {
            var bytes = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
        }

        static Settings startSettings;
        static bool startHidden;

        static int Main(string[] args)
        {
            Directory.CreateDirectory(DataDir);
            var settings = new Settings(Path.Combine(DataDir, "settings.ini"));

            if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal) && args[0] != "--background")
                return Cli.Run(args, settings);

            // One running copy per data folder (a test copy pointed at another folder doesn't clash with the real one).
            using var instance = SingleInstance.TryStart(Path.Combine(RuntimeDir, AppId + InstanceScope));
            if (instance == null)
            {
                // Already running in the tray: ask that copy to bring its dashboard up instead (but not when this is
                // just a second autostart).
                if (!args.Contains("--background")) SingleInstance.AskToShow(Path.Combine(RuntimeDir, AppId + InstanceScope));
                return 0;
            }

            // Never pop an error dialog over a fullscreen game; write it to a file instead.
            AppDomain.CurrentDomain.UnhandledException += (s, e) => LogError(e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                LogError(e.Exception);
                e.SetObserved();
            };

            startSettings = settings;
            startHidden = args.Contains("--background");
            App.Started += () =>
            {
                Dispatcher.UIThread.UnhandledException += (s, e) =>
                {
                    LogError(e.Exception);
                    e.Handled = true;
                };
                var companion = new Companion(startSettings, startHidden);
                instance.ShowRequested += () => Dispatcher.UIThread.Post(() => companion.ShowDashboard(activate: true));
            };
            BuildApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
            return 0;
        }

        /// <summary>The Avalonia app with the dark theme and the app's fonts.</summary>
        public static AppBuilder BuildApp() => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
            .With(new X11PlatformOptions { WmClass = AppId })
            .LogToTrace();

        static void LogError(Exception ex)
        {
            if (ex == null) return;
            try { File.AppendAllText(Path.Combine(DataDir, "errors.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {ex}\n\n"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static readonly bool Tracing = Environment.GetEnvironmentVariable("KSFC_DEBUG") == "1";
        const long TraceLimit = 1024 * 1024;
        static readonly object traceLock = new object();
        static string traceFile;

        /// <summary>
        /// What the app saw and did, for when something goes wrong: always kept (the last 1-2 MB, in the cache
        /// folder), or in the data folder's debug.log when KSFC_DEBUG=1 is set.
        /// </summary>
        public static void Trace(string message)
        {
            lock (traceLock)
            {
                try
                {
                    if (traceFile == null)
                    {
                        var folder = Tracing ? DataDir : CacheDir;
                        Directory.CreateDirectory(folder);
                        traceFile = Path.Combine(folder, "debug.log");
                    }
                    if (!Tracing && File.Exists(traceFile) && new FileInfo(traceFile).Length > TraceLimit)
                    {
                        var old = Path.ChangeExtension(traceFile, ".old.log");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(traceFile, old);
                    }
                    File.AppendAllText(traceFile, $"{DateTime.Now:HH:mm:ss.fff}  {message}\n");
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
