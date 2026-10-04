using System;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using JDP.Tests.Integration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.UITests {
    // "Open Folder", "Open URL", Help and the update prompt hand a folder or a web address to the
    // shell (Explorer, the browser). The app under test has CTW_TEST_SHELL_LOG set (see LaunchApp),
    // so it writes each target to ShellLogPath instead of opening it, and no window opens outside
    // the test. The update check goes to the loopback server through CTW_TEST_UPDATE_URL.
    public partial class MainWindowSmokeTests {
        private const string ReleasesURL = "https://github.com/kevin-contino/ChanThreadWatch/releases";
        private const string WikiURL = "https://github.com/SuperGouge/ChanThreadWatch/wiki";
        private const string LatestReleasePath = "/repos/kevin-contino/ChanThreadWatch/releases/latest";

        private string ShellLogPath => Path.Combine(_appDir, "shell-log.txt");

        [TestMethod]
        [TestCategory("UI")]
        public void OpenFolderOpenURLAndHelpGoToTheShell() {
            _server.Route(ThreadPath, LoopbackResponse.Html(ThreadPage));
            string threadURL = _server.URL(ThreadPath);
            Window window = LaunchApp();
            AutomationElement threadList = AddThread(window, threadURL, true);
            WaitUntil(() => RowHasCell(threadList, "Stopped: Download complete"), "the thread to stop after its one-time download");

            FindItem(RightClickFirstRow(threadList), "Open Folder").DoDefaultAction();
            string folder = WaitForShellTargets(1)[0];
            Assert.IsTrue(folder.StartsWith(Path.Combine(_appDir, "downloads") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Open Folder target " + folder + " is not in the download folder");
            Assert.AreEqual(1, Directory.GetFiles(folder, "123.html", SearchOption.AllDirectories).Length, "Open Folder target " + folder + " is not the thread's folder");

            FindItem(RightClickFirstRow(threadList), "Open URL").DoDefaultAction();
            Assert.AreEqual(threadURL, WaitForShellTargets(2)[1]);

            FindById(window, "btnHelp").AsButton().Invoke();
            Assert.AreEqual(WikiURL, WaitForShellTargets(3)[2]);
        }

        [TestMethod]
        [TestCategory("UI")]
        public void UpdatePromptOpensTheReleasesPage() {
            // The app is 1.40 or later, so 1.99.0 is newer, and it is close enough to count as plausible
            _server.Route(LatestReleasePath, LoopbackResponse.Text("{\"tag_name\":\"v1.99.0\",\"name\":\"stub\"}"));
            // The first line for a setting wins, so the test settings' CheckForUpdates=0 is replaced
            string settingsPath = Path.Combine(SettingsDir, "settings.txt");
            File.WriteAllLines(settingsPath, File.ReadAllLines(settingsPath).Select(line => line == "CheckForUpdates=0" ? "CheckForUpdates=1" : line));

            LaunchApp(_server.URL(LatestReleasePath));
            AutomationElement prompt = WaitForProcessWindow("Newer Version Found");
            AutomationElement yes = null;
            WaitUntil(() => (yes = prompt.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Yes")))) != null, "the Yes button of the update prompt");
            yes.AsButton().Invoke();

            CollectionAssert.AreEqual(new[] { ReleasesURL }, WaitForShellTargets(1));
            Assert.AreNotEqual(0, _server.RequestsTo(LatestReleasePath).Count, "the app did not ask the stub for the latest release");
        }

        // Waits until the app has handed exactly count targets to the shell, and returns them in order
        private string[] WaitForShellTargets(int count) {
            string[] targets = null;
            WaitUntil(() => (targets = ReadShellLog()).Length >= count, count + " shell target(s) in " + ShellLogPath);
            Assert.AreEqual(count, targets.Length, "shell targets: " + string.Join(", ", targets));
            return targets;
        }

        private string[] ReadShellLog() {
            try {
                return File.Exists(ShellLogPath) ? File.ReadAllLines(ShellLogPath) : new string[0];
            }
            catch (IOException) {
                // The app is still writing it
                return new string[0];
            }
        }

        // A top-level window of the app under test, or a window it owns (a message box)
        private AutomationElement WaitForProcessWindow(string title) {
            AutomationElement found = null;
            WaitUntil(() => {
                AutomationElement[] processWindows = _automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(_process.Id));
                found = processWindows.Concat(processWindows.SelectMany(w => w.FindAllChildren(cf => cf.ByControlType(ControlType.Window))))
                    .FirstOrDefault(w => w.Name == title);
                return found != null;
            }, "the " + title + " window");
            return found;
        }
    }
}
