using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The default folders and the application data folder go to a temporary folder for the whole run (see
    // TestDefaultFolders, compiled in from ChanThreadWatch.Core.Tests)
    [TestClass]
    public static class TestAssemblySetup {
        [AssemblyInitialize]
        public static void RedirectDefaultFolders(TestContext context) {
            TestDefaultFolders.Redirect();
        }

        [AssemblyCleanup]
        public static void DeleteDefaultFolders() {
            TestDefaultFolders.Delete();
        }
    }
}
