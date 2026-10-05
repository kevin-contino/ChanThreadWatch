using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Cli.Tests {
    // Which settings folder ctw uses, from the folder it runs from. Only temporary folders are looked at; the
    // application data folder is only named, never opened or created.
    [TestClass]
    public class SettingsFolderTests {
        private string _appFolder;
        private string _ctwFolder;

        [TestInitialize]
        public void CreateFolders() {
            _appFolder = Path.Combine(Path.GetTempPath(), "ctw-folder-" + Guid.NewGuid().ToString("N"));
            _ctwFolder = Path.Combine(_appFolder, "ctw");
            Directory.CreateDirectory(_ctwFolder);
        }

        [TestCleanup]
        public void DeleteFolders() {
            Directory.Delete(_appFolder, true);
        }

        [TestMethod]
        public void SettingsFileInCtwsFolder_IsPortable() {
            File.WriteAllText(Path.Combine(_ctwFolder, Settings.SettingsFileName), "");
            File.WriteAllText(Path.Combine(_appFolder, Settings.SettingsFileName), "");

            SettingsFolder folder = SettingsFolder.Find(_ctwFolder);

            Assert.AreEqual(_ctwFolder, folder.Path);
            Assert.IsFalse(folder.IsAppData);
        }

        // The zip's ctw folder unzipped next to a portable ChanThreadWatch.exe
        [TestMethod]
        public void SettingsFileInTheParentFolder_IsThatPortableFolder() {
            File.WriteAllText(Path.Combine(_appFolder, Settings.SettingsFileName), "");

            SettingsFolder folder = SettingsFolder.Find(_ctwFolder);

            Assert.AreEqual(_appFolder, folder.Path);
            Assert.IsFalse(folder.IsAppData);
            StringAssert.EndsWith(folder.Describe(), " (portable)");
        }

        [TestMethod]
        public void NoSettingsFile_IsTheApplicationDataFolder() {
            SettingsFolder folder = SettingsFolder.Find(_ctwFolder);

            Assert.AreEqual(Settings.AppDataDirectory, folder.Path);
            Assert.IsTrue(folder.IsAppData);
        }

        // The window's mutex name comes from the folder's path, so a portable folder found through ctw's parent
        // folder is written as the app writes its own folder (Settings.ExeDirectory: no separator at the end)
        [TestMethod]
        public void ParentFolderHasNoSeparatorAtTheEnd() {
            File.WriteAllText(Path.Combine(_appFolder, Settings.SettingsFileName), "");

            SettingsFolder folder = SettingsFolder.Find(_ctwFolder + Path.DirectorySeparatorChar);

            Assert.AreEqual(_appFolder, folder.Path);
            Assert.AreEqual(SettingsFolderLock.GetAppMutexName(_appFolder), SettingsFolderLock.GetAppMutexName(folder.Path));
        }
    }
}
