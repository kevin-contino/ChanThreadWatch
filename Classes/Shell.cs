using System;
using System.Diagnostics;
using System.IO;

namespace JDP {
    // Opens folders and web pages with the user's default program (Explorer, the browser).
    // Process.Start(string) did that through the shell on .NET Framework; on .NET 10 it does not
    // (UseShellExecute defaults to false), so the shell is asked for explicitly.
    internal static class Shell {
        // Test seam: UI tests set this to a file path, and each target is then appended to that file
        // instead of being opened, so a test can check what the app would open without starting
        // Explorer or a browser. Unset (always, outside the UI tests), targets open normally.
        internal const string TestLogVariable = "CTW_TEST_SHELL_LOG";

        public static void Open(string target) {
            string testLog = Environment.GetEnvironmentVariable(TestLogVariable);
            if (!String.IsNullOrEmpty(testLog)) {
                File.AppendAllText(testLog, target + Environment.NewLine);
                return;
            }
            using (Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })) { }
        }
    }
}
