using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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
            string folder = WaitForShellTargets(1, "Open Folder")[0];
            Assert.IsTrue(folder.StartsWith(Path.Combine(_appDir, "downloads") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Open Folder target " + folder + " is not in the download folder");
            Assert.AreEqual(1, Directory.GetFiles(folder, "123.html", SearchOption.AllDirectories).Length, "Open Folder target " + folder + " is not the thread's folder");

            // Until the first menu has closed, opening the menu again can find that closing menu, and
            // a click on its Open URL item is lost
            WaitUntil(() => ProcessMenus().Length == 0, "the thread menu to close after Open Folder");
            FindItem(RightClickFirstRow(threadList), "Open URL").DoDefaultAction();
            Assert.AreEqual(threadURL, WaitForShellTargets(2, "Open URL")[1]);

            FindById(window, "btnHelp").AsButton().Invoke();
            Assert.AreEqual(WikiURL, WaitForShellTargets(3, "Help")[2]);
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

            CollectionAssert.AreEqual(new[] { ReleasesURL }, WaitForShellTargets(1, "the update prompt's Yes"));
            Assert.AreNotEqual(0, _server.RequestsTo(LatestReleasePath).Count, "the app did not ask the stub for the latest release");
        }

        // Waits until the app has handed exactly count targets to the shell, and returns them in order.
        // On a timeout the message names the targets logged so far and the app's own log, where the
        // app writes any exception its Open Folder or Open URL worker caught.
        private string[] WaitForShellTargets(int count, string step) {
            string[] targets = new string[0];
            Stopwatch stopwatch = Stopwatch.StartNew();
            while ((targets = ReadShellLog()).Length < count) {
                if (stopwatch.Elapsed > Timeout) {
                    Assert.Fail("Timed out waiting for " + count + " shell target(s) after " + step + " in " + ShellLogPath
                        + "; logged: [" + string.Join(", ", targets) + "]; app log: " + string.Join(Environment.NewLine, ReadSharedLines(Path.Combine(SettingsDir, "log.txt"))));
                }
                Thread.Sleep(100);
            }
            Assert.AreEqual(count, targets.Length, "shell targets after " + step + ": " + string.Join(", ", targets));
            return targets;
        }

        private string[] ReadShellLog() {
            return ReadSharedLines(ShellLogPath);
        }

        // Reads a file the app may be writing at the same moment. File.ReadAllLines shares the file
        // for reading only, so an append by the app during the read fails with a sharing violation,
        // and the app's worker drops that target (it logs the exception and goes on). Sharing it for
        // writing as well lets the app's append go through.
        private static string[] ReadSharedLines(string path) {
            try {
                if (!File.Exists(path)) return new string[0];
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream)) {
                    // Only whole lines count: the last piece is empty, or a line the app is still writing
                    string[] pieces = reader.ReadToEnd().Split(new[] { Environment.NewLine }, StringSplitOptions.None);
                    return pieces.Take(pieces.Length - 1).ToArray();
                }
            }
            catch (IOException) {
                // The file was deleted between the check and the open
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
