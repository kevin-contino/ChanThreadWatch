using System;
using System.IO;
using JDP.Api;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JDP.Tests {
    // Moving the settings folder (frmSettings): every file is copied before any old one is deleted, so a copy that
    // fails leaves the old folder whole, api-threads.txt moves with threads.txt, the local API's api-token.txt and
    // api-clients.txt move with owner-only access, and a pending pairing code (api-pairing.txt) is deleted, not moved
    [TestClass]
    public class SettingsFolderMoveTests {
        private const string ChromeOrigin = ApiPairing.ChromeScheme + ApiPairing.ChromeExtensionId;
        private const string FirefoxOrigin = "moz-extension://0d4f2a8c-5b1e-4c7a-9f3d-2e6b8a1c4d5f";
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
            ApiTokenStore.OpeningForTesting = null;
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

        // The paired browsers file and a pending code that can't be deleted after the move are logged too: a token or
        // a code there would still pass
        [TestMethod]
        public void AnOldPairedBrowsersFileThatCannotBeDeletedIsLogged() {
            new ApiClientStore(_old).Pair(ChromeOrigin, DateTimeOffset.UtcNow);
            AssertReadOnlyOldFileIsLogged(OldClientsPath);
        }

        [TestMethod]
        public void AnOldPairingCodeThatCannotBeDeletedIsLogged() {
            new ApiPairingFile(_old).Create(DateTimeOffset.UtcNow);
            AssertReadOnlyOldFileIsLogged(Path.Combine(_old, ApiPairingFile.FileName));
        }

        private void AssertReadOnlyOldFileIsLogged(string oldPath) {
            File.SetAttributes(oldPath, FileAttributes.ReadOnly);
            try {
                frmSettings.MoveSettingsFiles(_old, _new);

                Assert.IsTrue(File.Exists(oldPath));
                StringAssert.Contains(ReadLog(), "Local API: " + oldPath + " could not be deleted after the settings folder move: System.UnauthorizedAccessException");
            }
            finally {
                File.SetAttributes(oldPath, FileAttributes.Normal);
            }
        }

        private string OldClientsPath {
            get { return Path.Combine(_old, ApiClientStore.FileName); }
        }

        private string NewClientsPath {
            get { return Path.Combine(_new, ApiClientStore.FileName); }
        }

        // The paired browsers are written again in the new folder, owner-only, with the same lines, so their tokens
        // still pass there; the old file goes with the others
        [TestMethod]
        public void ThePairedBrowsersMoveWithOwnerOnlyAccess() {
            new ApiTokenStore(_old).Generate();
            new ApiClientStore(_old).Pair(ChromeOrigin, new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
            new ApiClientStore(_old).Pair(FirefoxOrigin, new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero));
            string lines = File.ReadAllText(OldClientsPath);

            frmSettings.MoveSettingsFiles(_old, _new);

            Assert.AreEqual(lines, File.ReadAllText(NewClientsPath));
            Assert.HasCount(2, new ApiClientStore(_new).Read());
            using (FileStream stream = new FileStream(NewClientsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                Assert.IsTrue(OwnerOnlyFile.IsOwnerOnly(stream));
            }
            Assert.IsFalse(File.Exists(OldClientsPath));
        }

        // A pending pairing code is not moved: its file is deleted in the old folder, and none is made in the new one
        [TestMethod]
        public void APendingPairingCodeIsDeletedNotMoved() {
            new ApiPairingFile(_old).Create(DateTimeOffset.UtcNow);

            frmSettings.MoveSettingsFiles(_old, _new);

            Assert.IsFalse(File.Exists(Path.Combine(_old, ApiPairingFile.FileName)));
            Assert.IsFalse(File.Exists(Path.Combine(_new, ApiPairingFile.FileName)));
            foreach (string name in FileNames) {
                Assert.AreEqual(name, File.ReadAllText(Path.Combine(_new, name)), name);
            }
        }

        // A failed move keeps the pending code where it is: the program goes on with the old folder
        [TestMethod]
        public void AFailedMoveKeepsThePendingPairingCode() {
            new ApiPairingFile(_old).Create(DateTimeOffset.UtcNow);
            Directory.CreateDirectory(Path.Combine(_new, Settings.ThreadsFileName));

            Assert.Throws<Exception>(() => frmSettings.MoveSettingsFiles(_old, _new));

            Assert.IsTrue(File.Exists(Path.Combine(_old, ApiPairingFile.FileName)));
        }

        // A paired browsers file that is not trusted is neither copied nor deleted, and one already in the new folder
        // goes, as for the token file
        [TestMethod]
        public void AnUntrustedPairedBrowsersFileIsNotCopiedAndAStaleOneGoes() {
            new ApiClientStore(_new).Pair(ChromeOrigin, DateTimeOffset.UtcNow);
            File.WriteAllText(OldClientsPath, "1\nnot a line\n");

            frmSettings.MoveSettingsFiles(_old, _new);

            Assert.IsFalse(File.Exists(NewClientsPath));
            Assert.AreEqual("1\nnot a line\n", File.ReadAllText(OldClientsPath));
        }

        // A new folder that can't keep owner-only access fails the move for the paired browsers too (here without a
        // token file), with the drive's own reason, and leaves no copy; the old folder is whole
        [TestMethod]
        public void APairedBrowsersFileThatCannotBeOwnerOnlyFailsTheMove() {
            new ApiClientStore(_old).Pair(ChromeOrigin, DateTimeOffset.UtcNow);
            string lines = File.ReadAllText(OldClientsPath);
            OwnerOnlyFile.NewFileCheckForTesting = stream => false;

            ApiTokenException refused = Assert.ThrowsExactly<ApiTokenException>(() => frmSettings.MoveSettingsFiles(_old, _new));

            StringAssert.StartsWith(frmSettings.GetMoveFailureMessage(refused), "The settings files were not moved: on that drive");
            CollectionAssert.AreEqual(new string[0], Directory.GetFiles(_new));
            Assert.AreEqual(lines, File.ReadAllText(OldClientsPath));
            foreach (string name in FileNames) {
                Assert.AreEqual(name, File.ReadAllText(Path.Combine(_old, name)), name);
            }
        }

        // A copy that fails after the token files were copied: both copies go, and the old folder is whole
        [TestMethod]
        public void AFailedCopyAfterThePairedBrowsersRemovesTheirCopy() {
            string token = new ApiTokenStore(_old).Generate();
            new ApiClientStore(_old).Pair(FirefoxOrigin, DateTimeOffset.UtcNow);
            Directory.CreateDirectory(Path.Combine(_new, Settings.ThreadsFileName));

            Assert.Throws<Exception>(() => frmSettings.MoveSettingsFiles(_old, _new));

            Assert.IsFalse(File.Exists(NewTokenPath));
            Assert.IsFalse(File.Exists(NewClientsPath));
            Assert.IsTrue(new ApiTokenStore(_old).Verify(token));
            Assert.HasCount(1, new ApiClientStore(_old).Read());
        }

        // A paired browsers file that can't be read just now fails the move, so a passing failure never drops the
        // paired browsers; the token's copy goes again
        [TestMethod]
        public void APairedBrowsersFileThatCannotBeReadFailsTheMove() {
            new ApiTokenStore(_old).Generate();
            new ApiClientStore(_old).Pair(FirefoxOrigin, DateTimeOffset.UtcNow);
            string oldClientsPath = OldClientsPath;
            ApiTokenStore.OpeningForTesting = path => {
                if (path == oldClientsPath) throw new IOException("test: access denied", unchecked((int)0x80070005));
            };

            ApiTokenException error;
            try {
                error = Assert.ThrowsExactly<ApiTokenException>(() => frmSettings.MoveSettingsFiles(_old, _new));
            }
            finally {
                ApiTokenStore.OpeningForTesting = null;
            }

            Assert.AreEqual("api-clients.txt (the paired browsers) could not be read just now, so the settings files were not moved. Try again.",
                frmSettings.GetMoveFailureMessage(error));
            CollectionAssert.AreEqual(new string[0], Directory.GetFiles(_new));
            Assert.HasCount(1, new ApiClientStore(_old).Read());
        }

        // The Settings window's message: only the refusal of a drive without owner-only access gets its own reason; any
        // other failure, of the token file's copy too (access denied, a sharing violation), the general one
        [TestMethod]
        public void TheMoveFailureMessageNamesADriveWithoutOwnerOnlyAccessOnly() {
            const string driveReason = "The settings files were not moved: on that drive (for example a FAT or exFAT drive), the local API's token files api-token.txt " +
                "and api-clients.txt can't be protected so that only your user can read them.";
            new ApiTokenStore(_old).Generate();
            OwnerOnlyFile.NewFileCheckForTesting = stream => false;
            ApiTokenException refused = Assert.ThrowsExactly<ApiTokenException>(() => frmSettings.MoveSettingsFiles(_old, _new));

            StringAssert.StartsWith(frmSettings.GetMoveFailureMessage(refused), driveReason);
            Assert.AreEqual("Unable to move the settings files.", frmSettings.GetMoveFailureMessage(new ApiTokenException("x", new UnauthorizedAccessException())));
            Assert.AreEqual("Unable to move the settings files.", frmSettings.GetMoveFailureMessage(new ApiTokenException("x", new IOException("sharing violation"))));
            Assert.AreEqual("Unable to move the settings files.", frmSettings.GetMoveFailureMessage(new IOException("x")));
            Assert.AreEqual("api-clients.txt (the paired browsers) could not be read just now, so the settings files were not moved. Try again.",
                frmSettings.GetMoveFailureMessage(new ApiTokenException("x", null, false, true)));
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
