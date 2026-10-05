using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The default download and completed folders (Documents, or the home folder) and the application data folder,
    // redirected for the whole test run to a temporary folder, so a test that falls back to a default never reads or
    // writes the user's folders. Each test assembly calls Redirect from its AssemblyInitialize and Delete from its
    // AssemblyCleanup. Also compiled into ChanThreadWatch.Tests and ChanThreadWatch.Cli.Tests.
    public static class TestDefaultFolders {
        public static string Root { get; private set; }

        public static void Redirect() {
            Root = Path.Combine(Path.GetTempPath(), "ctw-test-defaults-" + Guid.NewGuid().ToString("N"));
            WatchSession.DefaultFoldersParentForTesting = Path.Combine(Root, "Documents");
            Settings.AppDataDirectoryForTesting = Path.Combine(Root, "AppData", Settings.ApplicationName);
        }

        public static void Delete() {
            if (Root != null && Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    [TestClass]
    public class TestDefaultFoldersTests {
        // Fails if an assembly did not redirect the defaults, so a fallback can never reach the user's folders
        [TestMethod]
        public void DefaultFoldersAreUnderTheTemporaryFolder() {
            string temp = Path.GetFullPath(Path.GetTempPath());
            Assert.IsNotNull(TestDefaultFolders.Root, "TestDefaultFolders.Redirect was not called by this assembly's AssemblyInitialize");
            StringAssert.StartsWith(Path.GetFullPath(TestDefaultFolders.Root), temp);
            StringAssert.StartsWith(WatchSession.GetDefaultFolder("Watched Threads"), TestDefaultFolders.Root);
            StringAssert.StartsWith(WatchSession.GetDefaultFolder("Completed Threads"), TestDefaultFolders.Root);
            StringAssert.StartsWith(Settings.AppDataDirectory, TestDefaultFolders.Root);
        }
    }
}
