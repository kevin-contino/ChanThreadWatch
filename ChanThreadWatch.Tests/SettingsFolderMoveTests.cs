using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // Moving the settings folder (frmSettings): every file is copied before any old one is deleted, so a copy that
    // fails leaves the old folder whole, and api-threads.txt moves with threads.txt
    [TestClass]
    public class SettingsFolderMoveTests {
        private static readonly string[] FileNames = { Settings.SettingsFileName, Settings.ApiThreadsFileName, Settings.ThreadsFileName };
        private string _old;
        private string _new;

        [TestInitialize]
        public void Setup() {
            string root = Path.Combine(Path.GetTempPath(), "ctw-move-" + Guid.NewGuid().ToString("N"));
            _old = Path.Combine(root, "old");
            _new = Path.Combine(root, "new");
            Directory.CreateDirectory(_old);
            Directory.CreateDirectory(_new);
            foreach (string name in FileNames) File.WriteAllText(Path.Combine(_old, name), name);
        }

        [TestCleanup]
        public void Cleanup() {
            Directory.Delete(Path.GetDirectoryName(_old), true);
        }

        [TestMethod]
        public void EveryFileMovesWithTheThreadList() {
            frmSettings.MoveSettingsFiles(_old, _new);

            foreach (string name in FileNames) {
                Assert.AreEqual(name, File.ReadAllText(Path.Combine(_new, name)), name);
                Assert.IsFalse(File.Exists(Path.Combine(_old, name)), name);
            }
        }

        // The last copy fails (a folder is where threads.txt goes): no old file is deleted
        [TestMethod]
        public void AFailedCopyDeletesNoOldFile() {
            Directory.CreateDirectory(Path.Combine(_new, Settings.ThreadsFileName));

            Assert.Throws<Exception>(() => frmSettings.MoveSettingsFiles(_old, _new));

            foreach (string name in FileNames) {
                Assert.AreEqual(name, File.ReadAllText(Path.Combine(_old, name)), name);
            }
        }
    }
}
