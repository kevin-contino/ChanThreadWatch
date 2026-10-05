using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // The settings dialog's check of a typed folder, which runs before anything is saved. No window is created.
    [TestClass]
    public class SettingsFolderValidationTests {
        // "/x" on Windows is a Unix path: it is rejected with the setting name, and no C:\x folder is created
        [TestMethod]
        [DataRow("download", "DownloadFolder")]
        [DataRow("completed", "CompletedFolder")]
        public void AUnixStyleFolderIsRejectedBeforeItIsCreated(string folderKind, string settingName) {
            string name = "ctw-unix-style-" + Guid.NewGuid().ToString("N");
            string folder = "/" + name;

            FormatException ex = Assert.ThrowsExactly<FormatException>(() => frmSettings.EnsureFolderExists(folder, folderKind, settingName));

            Assert.Contains(settingName, ex.Message);
            Assert.IsFalse(Directory.Exists(Path.GetFullPath(folder)));
        }

        [TestMethod]
        public void AWindowsFolderIsCreated() {
            string folder = Path.Combine(Path.GetTempPath(), "ctw-folder-" + Guid.NewGuid().ToString("N"));
            try {
                frmSettings.EnsureFolderExists(folder, "download", "DownloadFolder");

                Assert.IsTrue(Directory.Exists(folder));
            }
            finally {
                if (Directory.Exists(folder)) Directory.Delete(folder);
            }
        }
    }
}
