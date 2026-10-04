using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // MP-2c moved the app from .NET Framework 4.8 to .NET 10. These tests pin what must not change
    // with the runtime, and the test seams the UI tests use.
    [TestClass]
    public class RuntimeMigrationTests {
        // Recorded from the .NET Framework 4.8 build of main b5fc6bf (Windows PowerShell 5.1, the same
        // formula over ChanThreadWatch.Core.dll). A different name would let a 4.8 instance and a
        // .NET 10 instance run at the same time on one settings folder.
        [TestMethod]
        [DataRow(@"C:\Users\runneradmin\AppData\Roaming\ChanThreadWatch", "56D4B3D8F6E35CC8")]
        [DataRow("C:\\Users\\J\u00FCrgen \u00C5ngstr\u00F6m\\AppData\\Roaming\\ChanThreadWatch", "BF9348385C7B5D86")]
        [DataRow(@"D:\Tools\ChanThreadWatch", "63C0998D1948D86E")]
        [DataRow("\\\\nas\\share\\\u00DF\u01C5\u10D0\u0131\u03C2\\ChanThreadWatch", "9C085E7C9CD1843F")]
        public void MutexNameIsTheSameAsOnNetFramework(string settingsFolder, string net48Hash) {
            Assert.AreEqual(@"Global\ChanThreadWatch_" + net48Hash, Program.GetMutexName(settingsFolder));
        }

        // Assembly.Location is empty in a single-file app, so ExeDirectory uses AppContext.BaseDirectory.
        // Outside a single-file app both give the same folder, without a trailing separator.
        [TestMethod]
        public void ExeDirectoryIsTheAssemblyFolderWithoutTrailingSeparator() {
            Assert.AreEqual(Path.GetDirectoryName(typeof(Settings).Assembly.Location), Settings.ExeDirectory);
        }

        // Drag and drop of the ANSI "UniformResourceLocator" format decodes with code page 0, which is
        // the system ANSI code page (Encoding.Default on .NET Framework) once Core registered the
        // code page provider. Without it, .NET 10 would decode it as UTF-8.
        [TestMethod]
        public void CodePageZeroIsTheSystemAnsiCodePage() {
            RuntimeHelpers.RunClassConstructor(typeof(General).TypeHandle);
            Assert.AreEqual((int)GetACP(), Encoding.GetEncoding(0).CodePage);
        }

        [TestMethod]
        public void ShellOpenWritesTheTargetToTheTestLogInsteadOfOpeningIt() {
            string log = Path.Combine(Path.GetTempPath(), "ctw-shell-" + Guid.NewGuid().ToString("N") + ".txt");
            // Opening this for real would fail, so a pass also shows nothing was started
            string target = Path.Combine(Path.GetTempPath(), "ctw-missing-" + Guid.NewGuid().ToString("N"));
            string saved = Environment.GetEnvironmentVariable(Shell.TestLogVariable);
            try {
                Environment.SetEnvironmentVariable(Shell.TestLogVariable, log);
                Shell.Open(target);
                Shell.Open("https://example.invalid/b/thread/1");
                CollectionAssert.AreEqual(new[] { target, "https://example.invalid/b/thread/1" }, File.ReadAllLines(log));
            }
            finally {
                Environment.SetEnvironmentVariable(Shell.TestLogVariable, saved);
                File.Delete(log);
            }
        }

        [TestMethod]
        [DataRow(null, false)]
        [DataRow("", false)]
        [DataRow("http://127.0.0.1:5000/releases/latest", true)]
        [DataRow("http://[::1]:5000/releases/latest", true)]
        [DataRow("http://localhost:5000/releases/latest", true)]
        [DataRow("https://127.0.0.1:5000/releases/latest", false)]
        [DataRow("http://example.com/releases/latest", false)]
        [DataRow("http://127.0.0.1.example.com/releases/latest", false)]
        [DataRow("/releases/latest", false)]
        public void UpdateCheckURLCanOnlyBeMovedToLoopback(string testURL, bool used) {
            string saved = Environment.GetEnvironmentVariable(frmChanThreadWatch.TestUpdateURLVariable);
            try {
                Environment.SetEnvironmentVariable(frmChanThreadWatch.TestUpdateURLVariable, testURL);
                Assert.AreEqual(used ? testURL : General.LatestReleaseAPIURL, frmChanThreadWatch.GetLatestReleaseAPIURL());
            }
            finally {
                Environment.SetEnvironmentVariable(frmChanThreadWatch.TestUpdateURLVariable, saved);
            }
        }

        // The app and these tests run with System.Globalization.UseNls, so casing is .NET Framework's: ICU
        // would upper-case the Greek final sigma to a capital sigma and change the mutex name above
        [TestMethod]
        public void CasingIsTheNetFrameworkOne() {
            Assert.AreEqual("\u03C2\u01C5\u10D0", "\u03C2\u01C5\u10D0".ToUpperInvariant());
            Assert.IsFalse(string.Equals("\u03C2", "\u03A3", StringComparison.OrdinalIgnoreCase));
        }

        [TestMethod]
        public void AppRuntimeConfigUsesNls() {
            string config = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ChanThreadWatch.runtimeconfig.json"));
            StringAssert.Contains(config, "\"System.Globalization.UseNls\": true");
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetACP();
    }
}
