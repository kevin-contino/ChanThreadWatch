using System;
using System.Diagnostics;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using JDP.Tests.Integration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.UITests {
    // The Downloads window keeps a finished download listed with its result for a few seconds
    // (frmChanThreadWatch.FinishedDownloadHoldMilliseconds), so small files that finish between two
    // updates of the list still show up.
    public partial class MainWindowSmokeTests {
        private const string DownloadsThreadPage = "<html><head><title>Downloads</title></head><body>" +
            "<a href=\"/b/src/1.png\">1.png</a> <a href=\"/b/src/2.png\">2.png</a> <a href=\"/b/src/3.png\">3.png</a>" +
            "</body></html>";
        private static readonly string[] DownloadsImagePaths = { "/b/src/1.png", "/b/src/2.png", "/b/src/3.png" };
        private const string DoneResult = "Done";
        // Matches frmChanThreadWatch.FinishedDownloadHoldMilliseconds
        private static readonly TimeSpan FinishedDownloadHold = TimeSpan.FromSeconds(5);

        [TestMethod]
        [TestCategory("UI")]
        public void DownloadsWindowShowsFinishedDownloadUntilTheHoldTimeEnds() {
            _server.Route(ThreadPath, LoopbackResponse.Html(DownloadsThreadPage));
            for (int i = 0; i < DownloadsImagePaths.Length; i++) {
                _server.Route(DownloadsImagePaths[i], LoopbackResponse.Bytes(new byte[1500 + i], "image/png"));
            }
            Window window = LaunchApp();
            WaitUntilReady(window);
            // The window opens before the thread is added, so it is open when the first file finishes
            FindById(window, "btnDownloads").AsButton().Invoke();
            AutomationElement downloadList = FindById(FindDownloadsWindow(window), "lvDownloads");

            string imageURL = _server.URL(DownloadsImagePaths[0]);
            AddThread(window, _server.URL(ThreadPath), true);
            WaitUntil(() => RowHasCells(downloadList, imageURL, DoneResult), "the first image to show as done in the Downloads window");
            Stopwatch shownFor = Stopwatch.StartNew();

            WaitUntil(() => !RowHasCells(downloadList, imageURL, String.Empty), "the finished image to leave the Downloads window");
            // The row may have been seen late, so only the upper bound is checked: the main form drops
            // the download once a second after the hold, and the window updates its list each tick
            Assert.IsTrue(shownFor.Elapsed < FinishedDownloadHold + TimeSpan.FromSeconds(5),
                "the finished image stayed listed for " + shownFor.Elapsed + ", longer than the hold time allows");
            Assert.AreNotEqual(0, _server.RequestsTo(DownloadsImagePaths[0]).Count, "the app did not request the image");
        }

        // The Downloads window is owned by the main window, so UI Automation lists it either as a
        // child of the main window or as a top-level window of the app's process
        private AutomationElement FindDownloadsWindow(Window mainWindow) {
            AutomationElement downloadsWindow = null;
            WaitUntil(() => (downloadsWindow = mainWindow.FindFirstChild(cf => cf.ByAutomationId("frmDownloads"))
                ?? _automation.GetDesktop().FindFirstChild(cf => cf.ByAutomationId("frmDownloads").And(cf.ByProcessId(_process.Id)))) != null,
                "the Downloads window");
            return downloadsWindow;
        }

        // A row whose URL cell is url and that has a cell with the given text (any row of that URL when text is empty)
        private static bool RowHasCells(AutomationElement list, string url, string text) {
            return list.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Any(row => {
                AutomationElement[] cells = row.FindAllChildren();
                return (row.Name == url || cells.Any(cell => cell.Name == url)) && (text.Length == 0 || cells.Any(cell => cell.Name == text));
            });
        }
    }
}
