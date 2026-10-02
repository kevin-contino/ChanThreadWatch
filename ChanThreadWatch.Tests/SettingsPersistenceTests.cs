using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // Settings is static, so every test loads its own temp file and the cleanup resets
    // the store to empty for the other test classes.
    [TestClass]
    public class SettingsPersistenceTests {
        private string _dir;
        private string _path;

        [ClassInitialize]
        public static void UseExeDirectoryForLog(TestContext context) {
            Settings.UseExeDirectoryForSettings = true;
        }

        [TestInitialize]
        public void CreateTempDirectory() {
            _dir = Path.Combine(Path.GetTempPath(), "ctw-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "settings.txt");
        }

        [TestCleanup]
        public void ResetSettings() {
            Settings.Load(Path.Combine(_dir, "missing.txt"));
            Directory.Delete(_dir, true);
        }

        [TestMethod]
        public void SettingsRoundTrip() {
            Settings.Load(_path);
            Settings.CheckEvery = 5;
            Settings.UseSlug = true;
            Settings.DownloadFolder = @"C:\Downloads\Threads";
            Settings.ColumnWidths = new[] { 10, 20, 30 };

            Settings.Save(_path);
            Settings.Load(Path.Combine(_dir, "missing.txt"));
            Assert.IsNull(Settings.CheckEvery);
            Settings.Load(_path);

            Assert.AreEqual(5, Settings.CheckEvery);
            Assert.IsTrue(Settings.UseSlug);
            Assert.AreEqual(@"C:\Downloads\Threads", Settings.DownloadFolder);
            CollectionAssert.AreEqual(new[] { 10, 20, 30 }, Settings.ColumnWidths);
        }

        [TestMethod]
        public void NewlineInAValueCannotInjectAnotherSetting() {
            Settings.Load(_path);
            Settings.WindowTitle = "title\r\nUseSlug=1\nCheckEvery=99";

            Settings.Save(_path);
            Settings.Load(_path);

            Assert.AreEqual("title UseSlug=1 CheckEvery=99", Settings.WindowTitle);
            Assert.IsNull(Settings.UseSlug);
            Assert.IsNull(Settings.CheckEvery);
            Assert.HasCount(1, File.ReadAllLines(_path));
        }

        [TestMethod]
        public void SaveReplacesTheFileAndLeavesNoTempFile() {
            File.WriteAllLines(_path, new[] { "CheckEvery=1", "UseSlug=0", "WindowTitle=old" });
            Settings.Load(_path);
            Settings.WindowTitle = null;

            Settings.Save(_path);

            CollectionAssert.AreEquivalent(new[] { "CheckEvery=1", "UseSlug=0" }, File.ReadAllLines(_path));
            CollectionAssert.AreEqual(new[] { _path }, Directory.GetFiles(_dir));
        }

        [TestMethod]
        public void UnreadableSettingsFileIsNeverOverwritten() {
            string[] original = { "CheckEvery=5", "UseSlug=1" };
            File.WriteAllLines(_path, original);

            using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None)) {
                Settings.Load(_path);
            }
            Settings.CheckEvery = 7;
            Settings.Save(_path);

            CollectionAssert.AreEqual(original, File.ReadAllLines(_path));
        }

        [TestMethod]
        public void ReadersNeverSeeAPartiallyLoadedFile() {
            // CheckEvery is the first line and WindowTitle the last, so seeing the first
            // without the last means a reader looked at a half-filled settings store.
            List<string> lines = new List<string> { "CheckEvery=7" };
            for (int i = 0; i < 5000; i++) {
                lines.Add("Filler" + i + "=x");
            }
            lines.Add("WindowTitle=last");
            File.WriteAllLines(_path, lines);
            Settings.Load(_path);

            bool stop = false;
            Exception loaderError = null;
            Thread loader = new Thread(() => {
                try {
                    while (!Volatile.Read(ref stop)) Settings.Load(_path);
                }
                catch (Exception ex) {
                    loaderError = ex;
                }
            });
            loader.Start();
            int partialReads = 0;
            DateTime endTime = DateTime.UtcNow.AddMilliseconds(1500);
            try {
                while (DateTime.UtcNow < endTime && partialReads == 0) {
                    if (Settings.CheckEvery != null && Settings.WindowTitle == null) partialReads++;
                }
            }
            finally {
                Volatile.Write(ref stop, true);
                loader.Join();
            }

            Assert.IsNull(loaderError);
            Assert.AreEqual(0, partialReads);
        }
    }
}
