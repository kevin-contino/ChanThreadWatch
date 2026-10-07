using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The Local API dialog's settings (LocalApiSettings): the port check before anything is saved, and a save that sets
    // only the values that changed, under ctw's keys
    [TestClass]
    public class LocalApiSettingsTests {
        private string _folder;

        private string SettingsPath {
            get { return Path.Combine(_folder, Settings.SettingsFileName); }
        }

        [TestInitialize]
        public void SetUp() {
            _folder = Path.Combine(Path.GetTempPath(), "ctw-app-api-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            Settings.SettingsDirectoryOverride = _folder;
        }

        [TestCleanup]
        public void TearDown() {
            Settings.Load(Path.Combine(_folder, "missing-settings.txt"));
            Settings.SettingsDirectoryOverride = null;
            Directory.Delete(_folder, true);
        }

        private void WriteSettingsAndLoad(params string[] lines) {
            File.WriteAllLines(SettingsPath, lines);
            Settings.Load();
        }

        [TestMethod]
        [DataRow("1024", 1024)]
        [DataRow("65535", 65535)]
        [DataRow(" 47710 ", 47710)]
        public void ParsePort_AcceptsAPortFrom1024To65535(string text, int expected) {
            int port;
            Assert.IsNull(LocalApiSettings.ParsePort(text, out port));
            Assert.AreEqual(expected, port);
        }

        // Shown next to the box; the dialog stays open and saves nothing
        [TestMethod]
        [DataRow("")]
        [DataRow(null)]
        [DataRow("80")]
        [DataRow("1023")]
        [DataRow("65536")]
        [DataRow("-5000")]
        [DataRow("+5000")]
        [DataRow("5 000")]
        [DataRow("abc")]
        [DataRow("99999999999")]
        public void ParsePort_RefusesAnythingElseWithAMessage(string text) {
            int port;
            Assert.AreEqual("Enter a port from 1024 to 65535.", LocalApiSettings.ParsePort(text, out port));
            Assert.AreEqual(0, port);
        }

        // Missing keys show the defaults: off, port 47710, known sites only
        [TestMethod]
        public void Load_ShowsTheDefaultsForMissingKeys() {
            WriteSettingsAndLoad("CheckForUpdates=0");

            LocalApiSettings loaded = LocalApiSettings.Load();

            Assert.IsFalse(loaded.Enabled);
            Assert.AreEqual(Settings.DefaultApiPort, loaded.Port);
            Assert.IsFalse(loaded.AllowUnknownHosts);
        }

        // OK without a change writes nothing
        [TestMethod]
        public void SaveChanges_WithoutAChangeWritesNothing() {
            WriteSettingsAndLoad("CheckForUpdates=0");
            LocalApiSettings loaded = LocalApiSettings.Load();

            Assert.AreEqual(LocalApiSaveResult.Unchanged, new LocalApiSettings { Enabled = loaded.Enabled, Port = loaded.Port, AllowUnknownHosts = loaded.AllowUnknownHosts }.SaveChanges(loaded));

            CollectionAssert.AreEqual(new[] { "CheckForUpdates=0" }, File.ReadAllLines(SettingsPath));
        }

        // Only the changed value is set and saved, in ctw's form (ApiEnabled=1)
        [TestMethod]
        public void SaveChanges_WritesOnlyTheChangedValues() {
            WriteSettingsAndLoad("CheckForUpdates=0");
            LocalApiSettings loaded = LocalApiSettings.Load();

            Assert.AreEqual(LocalApiSaveResult.Saved, new LocalApiSettings { Enabled = true, Port = loaded.Port, AllowUnknownHosts = false }.SaveChanges(loaded));

            CollectionAssert.AreEqual(new[] { "CheckForUpdates=0", "ApiEnabled=1" }, File.ReadAllLines(SettingsPath));
            Assert.IsTrue(Settings.ApiEnabled == true);
        }

        [TestMethod]
        public void SaveChanges_WritesAChangedPortAndUnknownSites() {
            WriteSettingsAndLoad("ApiEnabled=1", "ApiPort=50000");
            LocalApiSettings loaded = LocalApiSettings.Load();

            Assert.AreEqual(LocalApiSaveResult.Saved, new LocalApiSettings { Enabled = true, Port = 50001, AllowUnknownHosts = true }.SaveChanges(loaded));

            CollectionAssert.AreEqual(new[] { "ApiEnabled=1", "ApiPort=50001", "ApiAllowUnknownHosts=1" }, File.ReadAllLines(SettingsPath));
        }

        // A port that is not valid in the file shows as the default one; OK writes that one, so the file holds what the
        // dialog showed
        [TestMethod]
        public void SaveChanges_ReplacesAPortThatIsNotValidInTheFile() {
            WriteSettingsAndLoad("ApiPort=80");
            LocalApiSettings loaded = LocalApiSettings.Load();
            Assert.AreEqual(Settings.DefaultApiPort, loaded.Port);

            Assert.AreEqual(LocalApiSaveResult.Saved, new LocalApiSettings { Enabled = false, Port = loaded.Port, AllowUnknownHosts = false }.SaveChanges(loaded));

            CollectionAssert.AreEqual(new[] { "ApiPort=47710" }, File.ReadAllLines(SettingsPath));
        }

        // The window saves every setting on exit (Settings.Save): what the dialog set, and the API's own mark of
        // api-threads.txt, are kept
        [TestMethod]
        public void TheExitSaveKeepsTheApiSettings() {
            WriteSettingsAndLoad("CheckForUpdates=0", "ApiThreadsFileWritten=1");
            LocalApiSettings loaded = LocalApiSettings.Load();
            new LocalApiSettings { Enabled = true, Port = 50002, AllowUnknownHosts = true }.SaveChanges(loaded);

            Settings.CheckForUpdates = true;
            Settings.Save();
            Settings.Load();

            Assert.IsTrue(Settings.ApiEnabled == true);
            Assert.AreEqual(50002, Settings.ApiPort);
            Assert.IsTrue(Settings.ApiAllowUnknownHosts == true);
            Assert.IsTrue(Settings.ApiThreadsFileWritten == true);
        }

        // ApiEnabled that is not valid in the file reads as off; OK writes the dialog's value even when it is unchanged
        // (off), so the file no longer holds the typo
        [TestMethod]
        public void SaveChanges_ReplacesAnApiEnabledThatIsNotValidInTheFile() {
            WriteSettingsAndLoad("ApiEnabled=true");
            LocalApiSettings loaded = LocalApiSettings.Load();
            Assert.IsFalse(loaded.Enabled);

            Assert.AreEqual(LocalApiSaveResult.Saved, new LocalApiSettings { Enabled = false, Port = loaded.Port, AllowUnknownHosts = false }.SaveChanges(loaded));

            CollectionAssert.AreEqual(new[] { "ApiEnabled=0" }, File.ReadAllLines(SettingsPath));
        }

        // settings.txt can't be written (a folder is in its place): the result says so, and the values are set for
        // this session
        [TestMethod]
        public void SaveChanges_SaysWhenTheSettingsCannotBeSaved() {
            Settings.Load();
            LocalApiSettings loaded = LocalApiSettings.Load();
            Directory.CreateDirectory(SettingsPath);

            Assert.AreEqual(LocalApiSaveResult.NotSaved, new LocalApiSettings { Enabled = true, Port = 50003, AllowUnknownHosts = false }.SaveChanges(loaded));

            Assert.IsTrue(Settings.ApiEnabled == true);
            Assert.AreEqual(50003, Settings.ApiPort);
        }

        // After OK: wait while the start runs (up to 3 seconds), close when the API listens or is off, stay open when
        // the start failed, and never close once a token was shown after OK
        [TestMethod]
        [DataRow((int)LocalApiState.Starting, 0, false, (int)LocalApiOkAction.Wait)]
        [DataRow((int)LocalApiState.Starting, 2900, false, (int)LocalApiOkAction.Wait)]
        [DataRow((int)LocalApiState.Starting, 3000, false, (int)LocalApiOkAction.Close)]
        [DataRow((int)LocalApiState.Listening, 0, false, (int)LocalApiOkAction.Close)]
        [DataRow((int)LocalApiState.Off, 0, false, (int)LocalApiOkAction.Close)]
        [DataRow((int)LocalApiState.Failed, 0, false, (int)LocalApiOkAction.StayOpen)]
        [DataRow((int)LocalApiState.Failed, 5000, false, (int)LocalApiOkAction.StayOpen)]
        // A token shown after OK was pressed is never closed away
        [DataRow((int)LocalApiState.Listening, 0, true, (int)LocalApiOkAction.StayOpen)]
        [DataRow((int)LocalApiState.Off, 0, true, (int)LocalApiOkAction.StayOpen)]
        [DataRow((int)LocalApiState.Starting, 3000, true, (int)LocalApiOkAction.StayOpen)]
        // The state and the action are passed as numbers: the test method is public, the types are internal
        public void OkWait_DecidesByTheStatus(int state, int waitedMilliseconds, bool tokenShownSinceOk, int expected) {
            Assert.AreEqual((LocalApiOkAction)expected, LocalApiOkWait.Decide((LocalApiState)state, TimeSpan.FromMilliseconds(waitedMilliseconds), tokenShownSinceOk));
        }
    }
}
