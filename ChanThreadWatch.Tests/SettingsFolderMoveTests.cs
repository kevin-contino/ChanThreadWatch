using System;
using System.IO;
using JDP.Api;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // Moving the settings folder (frmSettings): every file is copied before any old one is deleted, so a copy that
    // fails leaves the old folder whole, api-threads.txt moves with threads.txt, and the local API's api-token.txt
    // moves with owner-only access
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
            OwnerOnlyFile.NewFileCheckForTesting = null;
            Directory.Delete(Path.GetDirectoryName(_old), true);
        }

        private string OldTokenPath {
            get { return Path.Combine(_old, ApiTokenStore.FileName); }
        }

        private string NewTokenPath {
            get { return Path.Combine(_new, ApiTokenStore.FileName); }
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

        // The token file is written again in the new folder, owner-only, so the same token still passes there; the old
        // file goes with the others
        [TestMethod]
        public void TheApiTokenMovesWithOwnerOnlyAccessAndStillPasses() {
            string token = new ApiTokenStore(_old).Generate();

            frmSettings.MoveSettingsFiles(_old, _new);

            Assert.IsTrue(new ApiTokenStore(_new).Verify(token));
            using (FileStream stream = new FileStream(NewTokenPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                Assert.IsTrue(OwnerOnlyFile.IsOwnerOnly(stream));
            }
            Assert.IsFalse(File.Exists(OldTokenPath));
            foreach (string name in FileNames) {
                Assert.IsFalse(File.Exists(Path.Combine(_old, name)), name);
            }
        }

        // A new folder that can't keep owner-only access (FAT): the move fails as for any copy that fails, no readable
        // copy of the token file is left in the new folder, and the old folder is whole
        [TestMethod]
        public void ATokenFileThatCannotBeOwnerOnlyFailsTheMoveAndLeavesNoCopy() {
            string token = new ApiTokenStore(_old).Generate();
            OwnerOnlyFile.NewFileCheckForTesting = stream => false;

            Assert.Throws<ApiTokenException>(() => frmSettings.MoveSettingsFiles(_old, _new));

            CollectionAssert.AreEqual(new string[0], Directory.GetFiles(_new));
            OwnerOnlyFile.NewFileCheckForTesting = null;
            Assert.IsTrue(new ApiTokenStore(_old).Verify(token));
            foreach (string name in FileNames) {
                Assert.AreEqual(name, File.ReadAllText(Path.Combine(_old, name)), name);
            }
        }

        // A token file already in the new folder (from an earlier move, or made there by hand) never becomes the active
        // one: when the old folder has no token file, it goes
        [TestMethod]
        public void AStaleTokenFileInTheNewFolderIsDeletedWhenTheOldFolderHasNone() {
            string staleToken = new ApiTokenStore(_new).Generate();

            frmSettings.MoveSettingsFiles(_old, _new);

            Assert.IsFalse(File.Exists(NewTokenPath));
            Assert.IsFalse(new ApiTokenStore(_new).Verify(staleToken));
        }

        // A copy that fails after the token file was copied (a folder is where threads.txt goes): the token's copy goes
        // too, so it can't become active later, and the old folder is whole
        [TestMethod]
        public void AFailedCopyAfterTheTokenRemovesTheTokenCopy() {
            string token = new ApiTokenStore(_old).Generate();
            Directory.CreateDirectory(Path.Combine(_new, Settings.ThreadsFileName));

            Assert.Throws<Exception>(() => frmSettings.MoveSettingsFiles(_old, _new));

            Assert.IsFalse(File.Exists(NewTokenPath));
            Assert.IsTrue(new ApiTokenStore(_old).Verify(token));
        }

        // An old token file that can't be deleted after the move is logged, not passed over in silence
        [TestMethod]
        public void AnOldTokenFileThatCannotBeDeletedIsLogged() {
            new ApiTokenStore(_old).Generate();
            File.SetAttributes(OldTokenPath, FileAttributes.ReadOnly);
            try {
                frmSettings.MoveSettingsFiles(_old, _new);

                Assert.IsTrue(File.Exists(OldTokenPath));
                StringAssert.Contains(ReadLog(), "Local API: " + OldTokenPath + " could not be deleted after the settings folder move: System.UnauthorizedAccessException");
            }
            finally {
                File.SetAttributes(OldTokenPath, FileAttributes.Normal);
            }
        }

        // The Settings window's message: only the refusal of a drive without owner-only access gets its own reason; any
        // other failure, of the token file's copy too (access denied, a sharing violation), the general one
        [TestMethod]
        public void TheMoveFailureMessageNamesADriveWithoutOwnerOnlyAccessOnly() {
            const string driveReason = "The settings files were not moved: on that drive (for example a FAT or exFAT drive), the local API's token file api-token.txt can't be protected";
            new ApiTokenStore(_old).Generate();
            OwnerOnlyFile.NewFileCheckForTesting = stream => false;
            ApiTokenException refused = Assert.ThrowsExactly<ApiTokenException>(() => frmSettings.MoveSettingsFiles(_old, _new));

            StringAssert.StartsWith(frmSettings.GetMoveFailureMessage(refused), driveReason);
            Assert.AreEqual("Unable to move the settings files.", frmSettings.GetMoveFailureMessage(new ApiTokenException("x", new UnauthorizedAccessException())));
            Assert.AreEqual("Unable to move the settings files.", frmSettings.GetMoveFailureMessage(new ApiTokenException("x", new IOException("sharing violation"))));
            Assert.AreEqual("Unable to move the settings files.", frmSettings.GetMoveFailureMessage(new IOException("x")));
        }

        private static string ReadLog() {
            using (FileStream stream = new FileStream(Path.Combine(TestAssemblySetup.LogFolder, Settings.LogFileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(stream)) {
                return reader.ReadToEnd();
            }
        }

        // A token file that is not trusted (here it has the folder's access, as a plain copy would) is neither copied
        // nor deleted: its token passes in neither folder
        [TestMethod]
        public void AnUntrustedTokenFileIsNotCopied() {
            string token = new ApiTokenStore(_old).Generate();
            string line = File.ReadAllText(OldTokenPath);
            File.Delete(OldTokenPath);
            File.WriteAllText(OldTokenPath, line);
            Assert.IsFalse(new ApiTokenStore(_old).IsConfigured(), "The rewritten file is still trusted.");

            frmSettings.MoveSettingsFiles(_old, _new);

            Assert.IsFalse(File.Exists(NewTokenPath));
            Assert.AreEqual(line, File.ReadAllText(OldTokenPath));
            Assert.IsFalse(new ApiTokenStore(_new).Verify(token));
            foreach (string name in FileNames) {
                Assert.AreEqual(name, File.ReadAllText(Path.Combine(_new, name)), name);
            }
        }
    }
}
