using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The defaults of a thread added by ctw add and the local API, as the app's Add button sets them. Settings is
    // static, so every test loads its own temp file and the cleanup resets the store to empty.
    [TestClass]
    public class NewThreadTests {
        private const string Url = "https://boards.4chan.org/a/thread/111";
        private string _dir;
        private string _path;

        [TestInitialize]
        public void CreateTempDirectory() {
            _dir = Path.Combine(Path.GetTempPath(), "ctw-newthread-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "settings.txt");
        }

        [TestCleanup]
        public void ResetSettings() {
            Settings.Load(Path.Combine(_dir, "missing.txt"));
            Directory.Delete(_dir, true);
        }

        [TestMethod]
        public void WithoutSettings_ChecksEvery3MinutesWithoutLoginFolderOrAutoFollow() {
            Settings.Load(_path);
            DateTime before = DateTime.Now;

            ThreadInfo thread = NewThread.Create(Url, "Desc", "Cat");

            Assert.AreEqual(Url, thread.URL);
            Assert.AreEqual(String.Empty, thread.PageAuth);
            Assert.AreEqual(String.Empty, thread.ImageAuth);
            Assert.AreEqual(3 * 60, thread.CheckIntervalSeconds);
            Assert.IsFalse(thread.OneTimeDownload);
            Assert.AreEqual(String.Empty, thread.SaveDir);
            Assert.AreEqual("Desc", thread.Description);
            Assert.IsNull(thread.StopReason);
            Assert.AreEqual("Cat", thread.Category);
            Assert.IsFalse(thread.AutoFollow);
            Assert.AreEqual(String.Empty, thread.ExtraData.AddedFrom);
            Assert.IsNull(thread.ExtraData.LastImageOn);
            Assert.IsTrue(thread.ExtraData.AddedOn >= before && thread.ExtraData.AddedOn <= DateTime.Now, thread.ExtraData.AddedOn.ToString("o"));
        }

        [TestMethod]
        public void NullDescriptionAndCategoryAreEmpty() {
            Settings.Load(_path);

            ThreadInfo thread = NewThread.Create(Url, null, null);

            Assert.AreEqual(String.Empty, thread.Description);
            Assert.AreEqual(String.Empty, thread.Category);
        }

        // A saved login never reaches a new thread
        [TestMethod]
        [DataRow("CheckEvery=10\r\nAutoFollow=1\r\n", 600, false, true)]
        [DataRow("CheckEvery=1\r\n", 0, false, false, DisplayName = "1 is shown and used as 0")]
        [DataRow("CheckEvery=0\r\n", 0, false, false)]
        [DataRow("CheckEvery=10\r\nOneTimeDownload=1\r\n", 0, true, false)]
        [DataRow("CheckEvery=x\r\nAutoFollow=0\r\nOneTimeDownload=0\r\n", 180, false, false, DisplayName = "unreadable interval")]
        [DataRow("UsePageAuth=1\r\nPageAuth=user:pass\r\nUseImageAuth=1\r\nImageAuth=user:pass\r\n", 180, false, false, DisplayName = "saved login")]
        public void UsesTheAppDefaultsFromTheSettings(string settings, int checkIntervalSeconds, bool oneTimeDownload, bool autoFollow) {
            File.WriteAllText(_path, settings);
            Settings.Load(_path);

            ThreadInfo thread = NewThread.Create(Url, String.Empty, String.Empty);

            Assert.AreEqual(checkIntervalSeconds, thread.CheckIntervalSeconds);
            Assert.AreEqual(oneTimeDownload, thread.OneTimeDownload);
            Assert.AreEqual(autoFollow, thread.AutoFollow);
            Assert.AreEqual(String.Empty, thread.PageAuth);
            Assert.AreEqual(String.Empty, thread.ImageAuth);
        }
    }
}
