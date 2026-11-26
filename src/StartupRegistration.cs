using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MD301Plus
{
    internal sealed class StartupRegistration
    {
        private readonly string executable;
        private readonly string runPath;
        private readonly string approvalPath;

        internal StartupRegistration()
            : this(Application.ExecutablePath, @"Software\Microsoft\Windows\CurrentVersion\Run",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run") { }

        internal StartupRegistration(string executable, string runPath, string approvalPath)
        {
            this.executable = executable;
            this.runPath = runPath;
            this.approvalPath = approvalPath;
        }

        internal bool IsEnabled()
        {
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(runPath))
            {
                return run != null && string.Equals(run.GetValue(AppInfo.Name) as string,
                    Command, StringComparison.OrdinalIgnoreCase) && !IsDisabledByWindows();
            }
        }

        internal void SetEnabled(bool enabled)
        {
            if (enabled)
            {
                if (IsDisabledByWindows())
                    throw new InvalidOperationException(
                        "Windows has disabled MD301 Plus startup. Open Windows Startup settings and enable MD301 Plus there.");
                using (RegistryKey run = Registry.CurrentUser.CreateSubKey(runPath))
                    run.SetValue(AppInfo.Name, Command, RegistryValueKind.String);
            }
            else
            {
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(runPath, true))
                    if (run != null) run.DeleteValue(AppInfo.Name, false);
            }
            if (IsEnabled() != enabled) throw new IOException("Windows did not save the startup setting.");
        }

        private string Command { get { return "\"" + executable + "\" --startup"; } }

        private bool IsDisabledByWindows()
        {
            // Read Windows' override, but never rewrite a user's Startup Apps permission.
            using (RegistryKey approval = Registry.CurrentUser.OpenSubKey(approvalPath))
            {
                object value = approval == null ? null : approval.GetValue(AppInfo.Name);
                if (value == null) return false;
                byte[] state = value as byte[];
                if (state == null || state.Length < 1)
                    throw new IOException("Windows' startup permission for MD301 Plus could not be read.");
                return state[0] == 3 || state[0] == 7;
            }
        }
    }
}
