using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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

        // ctw watch uses the settings folder it found, and bases relative folders on a portable settings folder
        [TestMethod]
        public void OverridesSetTheSettingsFolderAndTheBaseOfRelativeFolders() {
            Settings.Load(_path);
            Settings.DownloadFolder = "rel";
            Settings.DownloadFolderIsRelative = true;
            StringAssert.StartsWith(Settings.AbsoluteDownloadDirectory, Path.Combine(Settings.ExeDirectory, "rel"));
            try {
                Settings.SettingsDirectoryOverride = _dir;
                Settings.RelativeFolderBaseOverride = _dir;

                Assert.AreEqual(_dir, Settings.GetSettingsDirectory());
                StringAssert.StartsWith(Settings.AbsoluteDownloadDirectory, Path.Combine(_dir, "rel"));
            }
            finally {
                Settings.SettingsDirectoryOverride = null;
                Settings.RelativeFolderBaseOverride = null;
            }
            Assert.AreNotEqual(_dir, Settings.GetSettingsDirectory());
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
        public void ApiSettingsDefaultToOffAndPort47710WhenMissing() {
            Settings.Load(_path);

            Assert.IsFalse(Settings.ApiEnabled);
            Assert.AreEqual(47710, Settings.ApiPort);
            Assert.IsFalse(Settings.ApiAllowUnknownHosts);
        }

        [TestMethod]
        public void ApiSettingsRoundTripAndKeepUnknownKeys() {
            File.WriteAllLines(_path, new[] { "SomeFutureSetting=x=y", "CheckEvery=5" });
            Settings.Load(_path);
            Settings.ApiEnabled = true;
            Settings.ApiPort = 50000;
            Settings.ApiAllowUnknownHosts = true;

            Settings.Save(_path);
            CollectionAssert.AreEquivalent(new[] { "SomeFutureSetting=x=y", "CheckEvery=5", "ApiEnabled=1", "ApiPort=50000", "ApiAllowUnknownHosts=1" }, File.ReadAllLines(_path));
            Settings.Load(Path.Combine(_dir, "missing.txt"));
            Settings.Load(_path);

            Assert.IsTrue(Settings.ApiEnabled);
            Assert.AreEqual(50000, Settings.ApiPort);
            Assert.IsTrue(Settings.ApiAllowUnknownHosts);

            Settings.ApiEnabled = false;
            Settings.ApiAllowUnknownHosts = false;
            Settings.ApiPort = null;
            Settings.Save(_path);
            Settings.Load(_path);

            Assert.IsFalse(Settings.ApiEnabled);
            Assert.IsFalse(Settings.ApiAllowUnknownHosts);
            Assert.AreEqual(47710, Settings.ApiPort);
            CollectionAssert.AreEquivalent(new[] { "SomeFutureSetting=x=y", "CheckEvery=5", "ApiEnabled=0", "ApiAllowUnknownHosts=0" }, File.ReadAllLines(_path));
        }

        [TestMethod]
        [DataRow("1024", 1024)]
        [DataRow("65535", 65535)]
        [DataRow("47711", 47711)]
        [DataRow("1023", 47710)]
        [DataRow("0", 47710)]
        [DataRow("-1", 47710)]
        [DataRow("65536", 47710)]
        [DataRow("99999999999", 47710, DisplayName = "not an int")]
        [DataRow("abc", 47710)]
        [DataRow("", 47710)]
        [DataRow(" 8080", 8080, DisplayName = "leading space, as Int32.TryParse reads it")]
        public void ApiPortOutsideTheValidRangeIsTheDefault(string saved, int expected) {
            File.WriteAllLines(_path, new[] { "ApiPort=" + saved });
            Settings.Load(_path);

            Assert.AreEqual(expected, Settings.ApiPort);
        }

        [TestMethod]
        public void ApiPortSetOutsideTheValidRangeReadsAsTheDefault() {
            Settings.Load(_path);
            Settings.ApiPort = 80;

            Assert.AreEqual(47710, Settings.ApiPort);
            Assert.IsFalse(Settings.IsValidApiPort(80));
            Assert.IsFalse(Settings.IsValidApiPort(null));
            Assert.IsTrue(Settings.IsValidApiPort(1024));
        }

        // Security opt-ins: only the exact value "1" is on, unlike the other on/off settings (anything but "0")
        [TestMethod]
        [DataRow("1", true)]
        [DataRow("", false)]
        [DataRow("yes", false)]
        [DataRow("true", false)]
        [DataRow("0", false)]
        [DataRow("01", false)]
        [DataRow(" 1", false)]
        [DataRow("1 ", false)]
        public void ApiOnOffSettingsAreOnOnlyForExactly1(string saved, bool expected) {
            File.WriteAllLines(_path, new[] { "ApiEnabled=" + saved, "ApiAllowUnknownHosts=" + saved, "UseSlug=" + saved });
            Settings.Load(_path);

            Assert.AreEqual(expected, Settings.ApiEnabled);
            Assert.AreEqual(expected, Settings.ApiAllowUnknownHosts);
            Assert.AreEqual(saved != "0", Settings.UseSlug, "the other on/off settings keep their reading");
        }

        [TestMethod]
        public void ApiOnOffSettingNamesIgnoreCase() {
            File.WriteAllLines(_path, new[] { "apienabled=1", "APIALLOWUNKNOWNHOSTS=1" });
            Settings.Load(_path);

            Assert.IsTrue(Settings.ApiEnabled);
            Assert.IsTrue(Settings.ApiAllowUnknownHosts);
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

        // Every line for a login setting is blanked (Load uses the first), matched as Load
        // matches names; the name and '=' stay, and everything else is kept byte for byte
        [TestMethod]
        [DataRow("CheckEvery=5\r\nPageAuth=user:pass\r\nUseSlug=1", "CheckEvery=5\r\nPageAuth=\r\nUseSlug=1", DisplayName = "page login")]
        [DataRow("imageauth=a=b\nPAGEAUTH=x\rPageAuth=y\n", "imageauth=\nPAGEAUTH=\rPageAuth=\n", DisplayName = "any case, every line, mixed breaks")]
        [DataRow("\uFEFFPageAuth=user:pass\r\n", "\uFEFFPageAuth=\r\n", DisplayName = "byte order mark")]
        [DataRow("PageAuth=dpapi:AAAA\r\nPageAuth=\r\n", "PageAuth=dpapi:AAAA\r\nPageAuth=\r\n", DisplayName = "encrypted or empty kept")]
        [DataRow("PageAuth\r\nPageAuth =x\r\nTitle=PageAuth=x\r\n", "PageAuth\r\nPageAuth =x\r\nTitle=PageAuth=x\r\n", DisplayName = "not a login setting")]
        public void PlaintextLoginsAreBlankedInASettingsCopy(string content, string expected) {
            byte[] blanked = Settings.BlankPlaintextAuth(Encoding.UTF8.GetBytes(content));

            Assert.AreEqual(expected, Encoding.UTF8.GetString(blanked));
        }

        [TestMethod]
        public void SettingsCopyInUtf16IsBlankedInItsEncoding() {
            Encoding utf16 = new UnicodeEncoding(false, true);
            byte[] content = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(utf16.GetPreamble(), utf16.GetBytes("PageAuth=user:pass\r\nUseSlug=1\r\n")));
            byte[] expected = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(utf16.GetPreamble(), utf16.GetBytes("PageAuth=\r\nUseSlug=1\r\n")));

            CollectionAssert.AreEqual(expected, Settings.BlankPlaintextAuth(content));
        }

        private static readonly string[] OldCopyLines = { "CheckEvery=5", "PageAuth=user:pass", "ImageAuth=img:pass", "WindowTitle=user:pass" };
        private static readonly string[] BlankedOldCopyLines = { "CheckEvery=5", "PageAuth=", "ImageAuth=", "WindowTitle=user:pass" };

        // The logger keeps the log file open for appending
        private static string ReadSharedFile(string path) {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader sr = new StreamReader(fs)) {
                return sr.ReadToEnd();
            }
        }

        // Once after each load: a copy written again later in the session is left alone
        [TestMethod]
        public void FirstSaveBlanksPlaintextLoginsInExistingSettingsCopiesOnce() {
            string copy = _path + ".corrupt-20200101-000000-000";
            string encryptedCopy = _path + ".corrupt-20200102-000000-000";
            // Only its form matters here (copies keep encrypted values), so it needs no backend
            string[] encryptedLines = { "PageAuth=" + StoredAuth.Prefix + "ZmFrZSBlbmNyeXB0ZWQgbG9naW4=", "UseSlug=1" };
            string threadListCopy = Path.Combine(_dir, "threads.txt.corrupt-20200101-000000-000");
            File.WriteAllLines(copy, OldCopyLines);
            File.WriteAllLines(encryptedCopy, encryptedLines);
            File.WriteAllLines(threadListCopy, OldCopyLines);
            Settings.Load(_path);
            string logPath = Path.Combine(Settings.GetSettingsDirectory(), Settings.LogFileName);
            Logger.Log("SettingsPersistenceTests marker");
            int start = ReadSharedFile(logPath).Length;

            Settings.Save(_path);

            CollectionAssert.AreEqual(BlankedOldCopyLines, File.ReadAllLines(copy));
            CollectionAssert.AreEqual(encryptedLines, File.ReadAllLines(encryptedCopy));
            CollectionAssert.AreEqual(OldCopyLines, File.ReadAllLines(threadListCopy));
            string log = ReadSharedFile(logPath).Substring(start);
            Assert.AreEqual(1, log.Split(new[] { "Plaintext logins were removed from " + Path.GetFileName(copy) + Environment.NewLine }, StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("user:pass", log);

            File.WriteAllLines(copy, OldCopyLines);
            Settings.Save(_path);
            CollectionAssert.AreEqual(OldCopyLines, File.ReadAllLines(copy));

            Settings.Load(_path);
            Settings.Save(_path);
            CollectionAssert.AreEqual(BlankedOldCopyLines, File.ReadAllLines(copy));
        }

        // On Windows the reader's sharing mode stops the replace; on Unix its lock stops the
        // rewrite (TextFile locks the copy first, since an open file doesn't stop a rename)
        [TestMethod]
        public void SettingsCopyThatCannotBeReplacedIsLeftUnchangedUntilTheNextLoad() {
            string copy = _path + ".corrupt-20200101-000000-000";
            File.WriteAllLines(copy, OldCopyLines);
            byte[] original = File.ReadAllBytes(copy);
            Settings.Load(_path);

            using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                Settings.Save(_path);
            }

            CollectionAssert.AreEqual(original, File.ReadAllBytes(copy));
            CollectionAssert.AreEquivalent(new[] { _path, copy }, Directory.GetFiles(_dir));
            Settings.Load(_path);
            Settings.Save(_path);
            CollectionAssert.AreEqual(BlankedOldCopyLines, File.ReadAllLines(copy));
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
