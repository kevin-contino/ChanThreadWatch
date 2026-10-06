using System;
using System.Globalization;
using System.IO;
using System.Linq;
using JDP.Api;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The local API's api-token.txt is never copied by a backup: the thread list backup (threads.txt.bak and
    // api-threads.txt.bak), the copies kept aside of a thread list or api-threads.txt that does not load, and the
    // settings save that rewrites the copies of settings.txt. Only the file itself holds the token's hash.
    [TestClass]
    public class ApiTokenBackupTests {
        private string _folder;

        [TestInitialize]
        public void SetUp() {
            _folder = Path.Combine(Path.GetTempPath(), "ctw-app-token-backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            Settings.SettingsDirectoryOverride = _folder;
            Settings.Load();
        }

        [TestCleanup]
        public void TearDown() {
            Settings.Load(Path.Combine(_folder, "missing-settings.txt"));
            Settings.SettingsDirectoryOverride = null;
            Directory.Delete(_folder, true);
        }

        private string FilePath(string name) {
            return Path.Combine(_folder, name);
        }

        [TestMethod]
        public void NoBackupOrKeptCopyHoldsTheTokenFile() {
            new ApiTokenStore(_folder).Generate();
            // The hex digits alone, so a copy in any other form is found too
            string hash = File.ReadAllText(FilePath(ApiTokenStore.FileName)).TrimEnd('\n').Substring("sha256:".Length);
            File.WriteAllLines(FilePath(Settings.SettingsFileName), new[] { "CheckForUpdates=0" });
            File.WriteAllLines(FilePath(Settings.ThreadsFileName), new[] { ThreadListFile.CurrentVersion.ToString(CultureInfo.InvariantCulture) });
            File.WriteAllLines(FilePath(Settings.ApiThreadsFileName), new[] { "1" });
            Settings.Load();

            General.BackupThreadList();
            // A thread list and an api-threads.txt that do not load are kept aside when they are loaded
            File.WriteAllLines(FilePath(Settings.ThreadsFileName), new[] { "not a thread list" });
            File.WriteAllLines(FilePath(Settings.ApiThreadsFileName), new[] { "not a list of marks" });
            WatchSession session = new WatchSession(work => work(), work => work(), _folder);
            session.LoadThreadList();
            session.SaveThreadList();
            Settings.Save();

            string[] files = Directory.GetFiles(_folder).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Assert.IsTrue(files.Contains(Settings.ThreadsFileName + ".bak"), String.Join(", ", files));
            Assert.IsTrue(files.Contains(Settings.ApiThreadsFileName + ".bak"), String.Join(", ", files));
            Assert.AreEqual(1, Directory.GetFiles(_folder, TextFile.GetCopySearchPattern(FilePath(Settings.ThreadsFileName))).Length, String.Join(", ", files));
            Assert.AreEqual(1, Directory.GetFiles(_folder, TextFile.GetCopySearchPattern(FilePath(Settings.ApiThreadsFileName))).Length, String.Join(", ", files));
            CollectionAssert.AreEqual(new[] { ApiTokenStore.FileName }, files.Where(name => name.StartsWith("api-token", StringComparison.OrdinalIgnoreCase)).ToArray());
            CollectionAssert.AreEqual(new[] { ApiTokenStore.FileName }, files.Where(name => File.ReadAllText(FilePath(name)).Contains(hash, StringComparison.Ordinal)).ToArray());
        }
    }
}
