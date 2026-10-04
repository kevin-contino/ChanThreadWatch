using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The app's exception handlers. The download transport tests are in ChanThreadWatch.Core.Tests.
    [TestClass]
    public class DownloadTransportTests {
        [ClassInitialize]
        public static void LoadEmptySettings(TestContext context) {
            // Keep the settings store in the test output folder (no settings file there) instead of AppData
            Settings.UseExeDirectoryForSettings = true;
            Settings.Load();
        }

        // R2
        [TestMethod]
        public void HandleUIExceptionShowsOneMessageWhileOneIsOpen() {
            int shown = 0;
            Action<string> show = null;
            show = message => {
                shown++;
                // A recurring exception while the box is open re-enters the handler
                Program.HandleUIException(new InvalidOperationException("again"), show);
            };

            Program.HandleUIException(new InvalidOperationException("first"), show);

            Assert.AreEqual(1, shown);
        }

        [TestMethod]
        public void HandleUIExceptionSurvivesAFailingMessageAndShowsTheNextOne() {
            int shown = 0;
            Action<string> show = message => {
                shown++;
                throw new InvalidOperationException("cannot show");
            };

            Program.HandleUIException(new InvalidOperationException("first"), show);
            Program.HandleUIException(new InvalidOperationException("second"), show);

            Assert.AreEqual(2, shown);
        }

        [TestMethod]
        public void FormatUnhandledExceptionIncludesSourceAndDetails() {
            string text = Program.FormatUnhandledException("UI thread", new InvalidOperationException("boom"), true);

            StringAssert.Contains(text, "UI thread");
            StringAssert.Contains(text, "terminating");
            StringAssert.Contains(text, "System.InvalidOperationException: boom");
        }

        [TestMethod]
        public void FormatUnhandledExceptionHandlesMissingOrForeignObjects() {
            StringAssert.Contains(Program.FormatUnhandledException("Background thread", null, false), "(no exception object)");
            StringAssert.Contains(Program.FormatUnhandledException("Background thread", "thrown string", false), "thrown string");
            Assert.IsFalse(Program.FormatUnhandledException("Background thread", null, false).Contains("terminating"));
        }
    }
}
