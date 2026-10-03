using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using FlaUI.UIA3;
using JDP.Tests.Integration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.UITests {
    // Launches the real ChanThreadWatch.exe and drives it through UI Automation. Each test runs a
    // private copy of the exe in its own temp folder with a settings.txt in place, so the app keeps
    // its settings, thread list and log there instead of in the user's AppData. The thread page
    // comes from a loopback server, so the test never touches the real network.
    [TestClass]
    public class MainWindowSmokeTests {
        private const string AppExeName = "ChanThreadWatch.exe";
        private const string MainWindowTitle = "Chan Thread Watch";
        private const string ThreadPath = "/b/thread/123";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private string _appDir;
        private LoopbackHttpServer _server;
        private UIA3Automation _automation;
        private Process _process;

        [TestInitialize]
        public void SetUp() {
            _appDir = Path.Combine(Path.GetTempPath(), "ctw-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(SettingsDir);
            CopyApp(_appDir);
            WriteSettings(Path.Combine(_appDir, "downloads"));
            _server = new LoopbackHttpServer();
            _automation = new UIA3Automation();
        }

        [TestCleanup]
        public void TearDown() {
            // The app must never outlive the test, even when an assertion failed mid-run
            KillApp();
            _automation?.Dispose();
            _server?.Dispose();
            DeleteDirectory(_appDir);
        }

        // In Debug builds the app keeps its portable settings in a "Debug" subfolder of the exe folder
        private string SettingsDir {
            get {
#if DEBUG
                return Path.Combine(_appDir, "Debug");
#else
                return _appDir;
#endif
            }
        }

        [TestMethod]
        [TestCategory("UI")]
        public void AddedThreadShowsInListAndIsSavedOnClose() {
            _server.Route(ThreadPath, LoopbackResponse.Html("<html><head><title>Smoke</title></head><body><p>Smoke test thread</p></body></html>"));
            string threadURL = _server.URL(ThreadPath);

            Window window = LaunchApp();
            Assert.AreEqual(MainWindowTitle, window.Title);
            // The app creates its log in the settings folder at startup, so this fails fast if it
            // ignored the test's settings.txt and fell back to the user's AppData
            Assert.IsTrue(File.Exists(Path.Combine(SettingsDir, "log.txt")), "the app did not use the test folder for its settings");

            Button addButton = FindById(window, "btnAdd").AsButton();
            WaitUntil(() => addButton.IsEnabled, "the Add Thread button to be enabled after the thread list loads");
            // A one-time download stops after the first check, so the final status is deterministic
            FindById(window, "chkOneTime").AsCheckBox().IsChecked = true;
            FindById(window, "txtPageURL").AsTextBox().Text = threadURL;
            addButton.Invoke();

            AutomationElement threadList = FindById(window, "lvThreads");
            WaitUntil(() => RowHasCell(threadList, "Stopped: Download complete"), "the added thread to show as downloaded in the thread list");
            Assert.AreEqual(1, threadList.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Length, "thread list rows");
            Assert.AreNotEqual(0, _server.RequestsTo(ThreadPath).Count, "the app did not request the thread page");
            string savedPage = Directory.GetFiles(Path.Combine(_appDir, "downloads"), "123.html", SearchOption.AllDirectories).SingleOrDefault();
            Assert.IsNotNull(savedPage, "the thread page was not saved to the download folder");
            StringAssert.Contains(File.ReadAllText(savedPage), "Smoke test thread");

            // The periodic save runs once a minute, so it has usually not written the thread list yet.
            // Removing any copy it did write makes sure the file checked below comes from the exit save.
            string threadListPath = Path.Combine(SettingsDir, "threads.txt");
            File.Delete(threadListPath);
            window.Close();
            Assert.IsTrue(_process.WaitForExit((int)Timeout.TotalMilliseconds), "the app did not exit after its window was closed");
            Assert.AreEqual(0, _process.ExitCode, "exit code");

            Assert.IsTrue(File.Exists(threadListPath), "the thread list was not saved on exit");
            string[] savedThreads = File.ReadAllLines(threadListPath);
            CollectionAssert.Contains(savedThreads, threadURL, "threads.txt does not list the added thread");
            // The app writes its window layout to settings.txt on exit
            string[] savedSettings = File.ReadAllLines(Path.Combine(SettingsDir, "settings.txt"));
            Assert.IsTrue(savedSettings.Any(line => line.StartsWith("ColumnWidths=", StringComparison.Ordinal)), "settings.txt was not saved on exit");
        }

        private Window LaunchApp() {
            var startInfo = new ProcessStartInfo(Path.Combine(_appDir, AppExeName)) { WorkingDirectory = _appDir, UseShellExecute = false };
            _process = Process.Start(startInfo);
            // FlaUI gets its own handle to the process so that disposing it leaves _process usable
            using (Application app = Application.Attach(_process.Id)) {
                Window window = app.GetMainWindow(_automation, Timeout);
                Assert.IsNotNull(window, "the main window did not appear");
                return window;
            }
        }

        private void KillApp() {
            if (_process == null) return;
            if (!_process.HasExited) {
                _process.Kill();
                _process.WaitForExit((int)Timeout.TotalMilliseconds);
            }
            _process.Dispose();
        }

        private static AutomationElement FindById(AutomationElement parent, string automationId) {
            AutomationElement element = null;
            WaitUntil(() => (element = parent.FindFirstDescendant(cf => cf.ByAutomationId(automationId))) != null, "control " + automationId);
            return element;
        }

        // Each list row exposes its column texts as child elements
        private static bool RowHasCell(AutomationElement list, string text) {
            return list.FindAllChildren().Any(row => row.FindAllChildren().Any(cell => cell.Name == text));
        }

        private static void WaitUntil(Func<bool> condition, string description) {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (!TryCondition(condition)) {
                if (stopwatch.Elapsed > Timeout) Assert.Fail("Timed out waiting for " + description);
                Thread.Sleep(100);
            }
        }

        // UI Automation calls can fail for a moment while the app is busy or a control is
        // redrawing, so such a failure counts as "not yet" and the wait tries again
        private static bool TryCondition(Func<bool> condition) {
            try {
                return condition();
            }
            catch (COMException) {
                return false;
            }
            catch (ElementNotAvailableException) {
                return false;
            }
            catch (TimeoutException) {
                return false;
            }
        }

        // Copies only the app's own files, not the test runner's assemblies
        private static void CopyApp(string appDir) {
            string buildDir = AppDomain.CurrentDomain.BaseDirectory;
            string exePath = Path.Combine(buildDir, AppExeName);
            if (!File.Exists(exePath)) Assert.Fail("Built app not found at " + exePath);
            File.Copy(exePath, Path.Combine(appDir, AppExeName));
            File.Copy(exePath + ".config", Path.Combine(appDir, AppExeName + ".config"));
        }

        // A settings.txt in place switches the app to portable mode. The download folder is set so
        // the app never falls back to My Documents, and the update check is off so the app makes no
        // requests beyond the loopback server. Debug builds add a "Debug" subfolder to the download
        // folder, and the app only keeps the setting if that folder exists.
        private void WriteSettings(string downloadDir) {
            Directory.CreateDirectory(Path.Combine(downloadDir, "Debug"));
            File.WriteAllLines(Path.Combine(SettingsDir, "settings.txt"), new[] {
                "DownloadFolder=" + downloadDir,
                "DownloadFolderIsRelative=0",
                "CheckForUpdates=0"
            });
        }

        private static void DeleteDirectory(string dir) {
            for (int attempt = 0; attempt < 10 && Directory.Exists(dir); attempt++) {
                try {
                    Directory.Delete(dir, true);
                }
                catch (IOException) {
                    Thread.Sleep(200);
                }
                catch (UnauthorizedAccessException) {
                    Thread.Sleep(200);
                }
            }
        }
    }
}
