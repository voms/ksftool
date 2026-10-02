using System;
using System.ComponentModel;
using System.Diagnostics;

namespace KsfCompanion
{
    /// <summary>The desktop around the app: opening links and folders, and notifications.</summary>
    static class Desktop
    {
        /// <summary>Opens a web page, a steam:// link or a folder with your default app (xdg-open). False if that didn't work.</summary>
        public static bool Open(string target)
        {
            if (string.IsNullOrEmpty(target)) return false;
            return Run("xdg-open", target);
        }

        /// <summary>A desktop notification (what the tray's balloon tip was on Windows); quietly nothing without a notification service.</summary>
        public static void Notify(string title, string body) =>
            Run("notify-send", "--app-name=" + Program.AppName, "--icon=" + Program.AppId, title, body);

        static bool Run(string program, params string[] args)
        {
            try
            {
                var start = new ProcessStartInfo(program) { UseShellExecute = false };
                foreach (var arg in args) start.ArgumentList.Add(arg);
                using var process = Process.Start(start);
                return process != null;
            }
            catch (Win32Exception ex)
            {
                Program.Trace($"{program}: {ex.Message}");
                return false;
            }
        }
    }
}
