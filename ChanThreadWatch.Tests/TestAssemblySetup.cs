using System;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The default folders and the application data folder go to a temporary folder for the whole run (see
    // TestDefaultFolders, compiled in from ChanThreadWatch.Core.Tests)
    [TestClass]
    public static class TestAssemblySetup {
        // The folder of the app's log (Logger), whose path is taken once, by the first log. The local API tests log, and
        // the open log would keep a test's own settings folder from being deleted, so it is one fixed temporary folder,
        // reused by every run (as in ChanThreadWatch.Api.Tests).
        public static string LogFolder { get; private set; }

        [AssemblyInitialize]
        public static void RedirectDefaultFolders(TestContext context) {
            TestDefaultFolders.Redirect();
            LogFolder = Path.Combine(Path.GetTempPath(), "ctw-app-tests-log");
            Directory.CreateDirectory(LogFolder);
            // Emptied first, so a test that reads the log never finds a line from an earlier run
            File.WriteAllText(Path.Combine(LogFolder, Settings.LogFileName), String.Empty);
            Settings.SettingsDirectoryOverride = LogFolder;
            RuntimeHelpers.RunClassConstructor(typeof(Logger).TypeHandle);
            Settings.SettingsDirectoryOverride = null;
        }

        [AssemblyCleanup]
        public static void DeleteDefaultFolders() {
            TestDefaultFolders.Delete();
        }
    }
}
