using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using JDP.Api;

namespace JDP {
    public partial class frmSettings : Form {
        // The local API's files in the settings folder, whose failed deletes after a move are logged
        private static readonly string[] _apiFileNames = { ApiTokenStore.FileName, ApiClientStore.FileName, ApiPairingFile.FileName };

        private readonly LocalApiHost _localApi;

        internal frmSettings(LocalApiHost localApi) {
            _localApi = localApi ?? throw new ArgumentNullException(nameof(localApi));
            InitializeComponent();
            GUI.SetFontAndScaling(this);
        }

        private void frmSettings_Load(object sender, EventArgs e) {
            LoadFolderSettings();
            LoadUserAgentAndThumbnailSettings();
            LoadFolderRenameSettings();
            LoadSortAndAutoFollowSettings();
            LoadFileNameSettings();
            LoadMiscSettings();
            LoadBackupSettings();
            LoadSpeedAndWindowTitleSettings();
            LoadSettingsLocation();
        }

        private void LoadFolderSettings() {
            txtDownloadFolder.Text = Settings.DownloadFolder;
            chkDownloadFolderRelative.Checked = Settings.DownloadFolderIsRelative ?? false;
            chkCompletedFolder.Checked = Settings.MoveToCompletedFolder ?? false;
            txtCompletedFolder.Enabled = btnCompletedFolder.Enabled = chkCompletedFolderRelative.Enabled = chkCompletedFolder.Checked;
            txtCompletedFolder.Text = Settings.CompletedFolder;
            chkCompletedFolderRelative.Checked = Settings.CompletedFolderIsRelative ?? false;
        }

        private void LoadUserAgentAndThumbnailSettings() {
            chkCustomUserAgent.Checked = Settings.UseCustomUserAgent ?? false;
            txtCustomUserAgent.Text = Settings.CustomUserAgent ?? String.Empty;
            chkSaveThumbnails.Checked = Settings.SaveThumbnails ?? true;
        }

        private void LoadFolderRenameSettings() {
            chkRenameDownloadFolderWithDescription.Checked = Settings.RenameDownloadFolderWithDescription ?? false;
            chkRenameDownloadFolderWithCategory.Checked = Settings.RenameDownloadFolderWithCategory ?? false;
            chkRenameDownloadFolderWithParentThreadDescription.Checked = Settings.RenameDownloadFolderWithParentThreadDescription ?? false;
            pnlParentThreadDescriptionFormat.Enabled = chkRenameDownloadFolderWithParentThreadDescription.Checked;
            txtParentThreadDescriptionFormat.Text = Settings.ParentThreadDescriptionFormat ?? Settings.DefaultParentThreadDescriptionFormat;
        }

        private void LoadSortAndAutoFollowSettings() {
            chkSortImagesByPoster.Checked = Settings.SortImagesByPoster ?? false;
            chkRecursiveAutoFollow.Checked = Settings.RecursiveAutoFollow ?? true;
            chkInterBoardAutoFollow.Checked = Settings.InterBoardAutoFollow ?? true;
        }

        private void LoadFileNameSettings() {
            chkUseOriginalFileNames.Checked = Settings.UseOriginalFileNames ?? false;
            chkVerifyImageHashes.Checked = Settings.VerifyImageHashes ?? true;
            chkUseSlug.Checked = Settings.UseSlug ?? false;
            pnlSlug.Enabled = chkUseSlug.Checked;
            rbSlugFirst.Checked = Settings.SlugType == SlugType.First;
            rbSlugLast.Checked = Settings.SlugType == SlugType.Last;
            rbSlugOnly.Checked = Settings.SlugType == SlugType.Only;
        }

        private void LoadMiscSettings() {
            chkCheckForUpdates.Checked = Settings.CheckForUpdates ?? false;
            chkBlacklistWildcards.Checked = Settings.BlacklistWildcards ?? false;
            chkMinimizeToTray.Checked = Settings.MinimizeToTray ?? false;
        }

        private void LoadBackupSettings() {
            chkBackupThreadList.Checked = Settings.BackupThreadList ?? false;
            pnlBackupEvery.Enabled = chkBackupThreadList.Checked;
            txtBackupEvery.Text = (Settings.BackupEvery ?? 1).ToString();
            chkBackupCheckSize.Enabled = chkBackupThreadList.Checked;
            chkBackupCheckSize.Checked = Settings.BackupCheckSize ?? false;
        }

        private void LoadSpeedAndWindowTitleSettings() {
            txtMaximumKilobytesPerSecond.Text = ((Settings.MaximumBytesPerSecond ?? 0) / 1024).ToString();
            txtWindowTitle.Text = Settings.WindowTitle ?? String.Format("{{{0}}}", WindowTitleMacro.ApplicationName);
            txtWindowTitle.SelectionStart = txtWindowTitle.Text.Length;
            cboWindowTitle.DataSource = Enum.GetValues(typeof(WindowTitleMacro));
        }

        private void LoadSettingsLocation() {
            if (Settings.UseExeDirectoryForSettings == true) {
                rbSettingsInExeFolder.Checked = true;
            }
            else {
                rbSettingsInAppDataFolder.Checked = true;
            }
        }

        private void btnOK_Click(object sender, EventArgs e) {
            try {
                string downloadFolder = txtDownloadFolder.Text.Trim();

                EnsureFolderExists(downloadFolder, "download", "DownloadFolder");

                string completedFolder = txtCompletedFolder.Text.Trim();

                if (chkCompletedFolder.Checked) {
                    EnsureFolderExists(completedFolder, "completed", "CompletedFolder");
                }

                if (!TryChangeSettingsFolder()) {
                    return;
                }

                string oldAbsoluteDownloadFolder = Settings.AbsoluteDownloadDirectory;

                SaveSettingsFromControls(downloadFolder, completedFolder);

                Settings.Save();

                if (!String.Equals(Settings.AbsoluteDownloadDirectory, oldAbsoluteDownloadFolder, StringComparison.OrdinalIgnoreCase)) {
                    MessageBox.Show(this, "The new download folder will not affect threads currently being watched until the program is restarted.  " +
                                          "If you are still watching the threads at next run, make sure you have moved their download folders into the new download folder.",
                        "Download Folder Changed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                DialogResult = DialogResult.OK;
            }
            catch (Exception ex) {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Throws an exception with a user-facing message if the folder is empty, is an absolute path
        // of another OS (for example "/Threads" on Windows), or cannot be created. This runs before
        // anything is saved, so a rejected folder leaves the setting unchanged.
        internal static void EnsureFolderExists(string folder, string folderKind, string settingName) {
            if (folder.Length == 0) {
                throw new Exception("You must enter a " + folderKind + " folder.");
            }
            General.ToLocalDirectoryPath(folder, settingName);
            if (Directory.Exists(folder)) {
                return;
            }
            try {
                Directory.CreateDirectory(folder);
            }
            catch {
                throw new Exception("Unable to create the " + folderKind + " folder.");
            }
        }

        // Moves the settings files if the settings location changed. Returns false
        // (after showing an error) if the move cannot be done.
        private bool TryChangeSettingsFolder() {
            string oldSettingsFolder = Settings.GetSettingsDirectory();
            string newSettingsFolder = Settings.GetSettingsDirectory(rbSettingsInExeFolder.Checked);
            if (String.Equals(newSettingsFolder, oldSettingsFolder, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
            if (!Program.ObtainMutex(newSettingsFolder)) {
                MessageBox.Show(this, "Another instance of this program is using the same settings folder.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            // The old folder's lock is kept until the files are moved, so a failure leaves this
            // program holding it
            SettingsFolderLock newLock;
            if (!Program.TryLockSettingsFolder(this, newSettingsFolder, false, out newLock)) {
                Program.ObtainMutex(oldSettingsFolder);
                return false;
            }
            try {
                MoveSettingsFiles(oldSettingsFolder, newSettingsFolder);
            }
            catch (Exception ex) {
                // Settings stay in the old folder, so take back its mutex (this releases the new one).
                // If another instance took the old folder in the meantime, the new mutex is kept.
                ReleaseNewFolderLock(newLock);
                Program.ObtainMutex(oldSettingsFolder);
                MessageBox.Show(this, GetMoveFailureMessage(ex),
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            // Null if the user chose to use the new folder without its lock
            Program.ReplaceSettingsFolderLock(newLock);
            return true;
        }

        // Null when the user chose to use the new folder without its lock
        private static void ReleaseNewFolderLock(SettingsFolderLock newLock) {
            if (newLock != null) newLock.Dispose();
        }

        // A drive that can't keep owner-only access, and a paired browsers file that could not be read (in use, access
        // denied), get their own reasons; any other failure (of the token files' copies too) the general one
        internal static string GetMoveFailureMessage(Exception ex) {
            ApiTokenException tokenError = ex as ApiTokenException;
            if (tokenError != null && tokenError.ClientsUnreadable) {
                return ApiClientStore.FileName + " (the paired browsers) could not be read just now, so the settings files were not moved. Try again.";
            }
            if (tokenError == null || !tokenError.OwnerOnlyNotSupported) return "Unable to move the settings files.";
            return "The settings files were not moved: on that drive (for example a FAT or exFAT drive), the local API's token files " +
                ApiTokenStore.FileName + " and " + ApiClientStore.FileName + " can't be protected so that only your user can read them. Nothing was copied or deleted. " +
                "Choose a folder on an NTFS drive.";
        }

        // Every file is copied before any old one is deleted, so a copy that fails leaves the old folder whole (the
        // program keeps using it). The marks of the threads added through the local API move with the thread list, and
        // the local API's token files (the scripts' token, then the paired browsers) move first, so a failure to write
        // them owner-only copies nothing. The new folder never keeps a token file that is not the old folder's: one
        // already there goes when the old folder has none (or none that is trusted), and the copy goes again when a
        // later copy fails. A pending pairing code (api-pairing.txt) is never moved: it ends with the move.
        internal static void MoveSettingsFiles(string oldSettingsFolder, string newSettingsFolder) {
            List<string> copied = new List<string>();
            KeepOrDropCopy(CopyApiTokenFile(oldSettingsFolder, newSettingsFolder), ApiTokenStore.FileName, oldSettingsFolder, newSettingsFolder, copied);
            try {
                KeepOrDropCopy(CopyApiClientsFile(oldSettingsFolder, newSettingsFolder), ApiClientStore.FileName, oldSettingsFolder, newSettingsFolder, copied);
                CopySettingsFiles(oldSettingsFolder, newSettingsFolder, copied);
            }
            catch {
                TryDeleteLogged(Path.Combine(newSettingsFolder, ApiTokenStore.FileName));
                TryDeleteLogged(Path.Combine(newSettingsFolder, ApiClientStore.FileName));
                throw;
            }
            foreach (string oldPath in copied) {
                TryDeleteLogged(oldPath);
            }
            // Only the old folder's code is deleted. An api-pairing.txt already in the new folder is left alone on
            // purpose: it is that folder's own pending code (ctw api-pair run there), not one this move made.
            TryDeleteLogged(Path.Combine(oldSettingsFolder, ApiPairingFile.FileName));
        }

        // A token file that was copied is deleted in the old folder later; one that was not leaves no file of that name
        // in the new folder
        private static void KeepOrDropCopy(bool wasCopied, string fileName, string oldSettingsFolder, string newSettingsFolder, List<string> copied) {
            if (wasCopied) copied.Add(Path.Combine(oldSettingsFolder, fileName));
            else File.Delete(Path.Combine(newSettingsFolder, fileName));
        }

        private static void CopySettingsFiles(string oldSettingsFolder, string newSettingsFolder, List<string> copied) {
            foreach (string fileName in new[] { Settings.SettingsFileName, Settings.ApiThreadsFileName, Settings.ThreadsFileName }) {
                string oldPath = Path.Combine(oldSettingsFolder, fileName);
                if (!File.Exists(oldPath)) continue;
                File.WriteAllBytes(Path.Combine(newSettingsFolder, fileName), File.ReadAllBytes(oldPath));
                copied.Add(oldPath);
            }
        }

        // A file that can't be deleted is left; only the local API's files are logged, since a token or a pairing code
        // would still pass there
        private static void TryDeleteLogged(string path) {
            try {
                File.Delete(path);
            }
            catch (Exception ex) {
                if (Array.IndexOf(_apiFileNames, Path.GetFileName(path)) >= 0) Logger.Log("Local API: " + path + " could not be deleted after the settings folder move: " + ex.GetType().FullName);
            }
        }

        // api-token.txt is written again in the new folder with owner-only access (ApiTokenStore), never copied byte for
        // byte, which would give the copy the new folder's access. Returns false, and leaves the old file, when it is
        // missing or not trusted (its token would not pass in either folder). Throws ApiTokenException, leaving no copy,
        // when the new folder can't keep owner-only access.
        private static bool CopyApiTokenFile(string oldSettingsFolder, string newSettingsFolder) {
            return new ApiTokenStore(oldSettingsFolder).CopyTo(new ApiTokenStore(newSettingsFolder));
        }

        // api-clients.txt (the paired browsers) is written again the same way. Returns false, and leaves the old file,
        // when it is missing, not trusted or lists no browser. Throws ApiTokenException, leaving no copy, when it could
        // not be read or the new folder can't keep owner-only access.
        private static bool CopyApiClientsFile(string oldSettingsFolder, string newSettingsFolder) {
            return new ApiClientStore(oldSettingsFolder).CopyTo(new ApiClientStore(newSettingsFolder));
        }

        private void btnLocalApi_Click(object sender, EventArgs e) {
            using (frmLocalApi localApiForm = new frmLocalApi(_localApi)) {
                localApiForm.ShowDialog(this);
            }
        }

        private void SaveSettingsFromControls(string downloadFolder, string completedFolder) {
            Settings.DownloadFolder = downloadFolder;
            Settings.DownloadFolderIsRelative = chkDownloadFolderRelative.Checked;
            Settings.MoveToCompletedFolder = chkCompletedFolder.Checked;
            Settings.CompletedFolder = completedFolder;
            Settings.CompletedFolderIsRelative = chkCompletedFolderRelative.Checked;
            Settings.UseCustomUserAgent = chkCustomUserAgent.Checked;
            Settings.CustomUserAgent = txtCustomUserAgent.Text;
            Settings.SaveThumbnails = chkSaveThumbnails.Checked;
            Settings.RenameDownloadFolderWithDescription = chkRenameDownloadFolderWithDescription.Checked;
            Settings.RenameDownloadFolderWithCategory = chkRenameDownloadFolderWithCategory.Checked;
            Settings.RenameDownloadFolderWithParentThreadDescription = chkRenameDownloadFolderWithParentThreadDescription.Checked;
            Settings.ParentThreadDescriptionFormat = txtParentThreadDescriptionFormat.Text;
            Settings.SortImagesByPoster = chkSortImagesByPoster.Checked;
            Settings.RecursiveAutoFollow = chkRecursiveAutoFollow.Checked;
            Settings.InterBoardAutoFollow = chkInterBoardAutoFollow.Checked;
            Settings.UseOriginalFileNames = chkUseOriginalFileNames.Checked;
            Settings.VerifyImageHashes = chkVerifyImageHashes.Checked;
            Settings.UseSlug = chkUseSlug.Checked;
            Settings.SlugType = GetSelectedSlugType();
            Settings.CheckForUpdates = chkCheckForUpdates.Checked;
            Settings.BlacklistWildcards = chkBlacklistWildcards.Checked;
            Settings.MinimizeToTray = chkMinimizeToTray.Checked;
            Settings.BackupThreadList = chkBackupThreadList.Checked;
            Settings.BackupEvery = Int32.Parse(txtBackupEvery.Text);
            Settings.BackupCheckSize = chkBackupCheckSize.Checked;
            Settings.MaximumBytesPerSecond = Int64.Parse(txtMaximumKilobytesPerSecond.Text) * 1024;
            Settings.WindowTitle = txtWindowTitle.Text;
            Settings.UseExeDirectoryForSettings = rbSettingsInExeFolder.Checked;
        }

        private SlugType GetSelectedSlugType() {
            if (rbSlugFirst.Checked) {
                return SlugType.First;
            }
            if (rbSlugOnly.Checked) {
                return SlugType.Only;
            }
            return SlugType.Last;
        }

        private void btnDownloadFolder_Click(object sender, EventArgs e) {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog()) {
                dialog.Description = "Select the download location.";
                dialog.ShowNewFolderButton = true;
                if (dialog.ShowDialog(this) == DialogResult.OK) {
                    SetDownloadFolderTextBox(dialog.SelectedPath);
                }
            }
        }

        private void btnCompletedFolder_Click(object sender, EventArgs e) {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog()) {
                dialog.Description = "Select the download location.";
                dialog.ShowNewFolderButton = true;
                if (dialog.ShowDialog(this) == DialogResult.OK) {
                    SetCompletedFolderTextBox(dialog.SelectedPath);
                }
            }
        }

        private void btnBackupThreadList_Click(object sender, EventArgs e) {
            General.BackupThreadList();
        }
        
        private void chkDownloadFolderRelative_CheckedChanged(object sender, EventArgs e) {
            SetDownloadFolderTextBox(txtDownloadFolder.Text.Trim());
        }

        private void chkCompletedFolderRelative_CheckedChanged(object sender, EventArgs e) {
            SetCompletedFolderTextBox(txtCompletedFolder.Text.Trim());
        }

        private void chkCompletedFolder_CheckedChanged(object sender, EventArgs e) {
            txtCompletedFolder.Enabled = btnCompletedFolder.Enabled = chkCompletedFolderRelative.Enabled = chkCompletedFolder.Checked;
        }

        private void chkCustomUserAgent_CheckedChanged(object sender, EventArgs e) {
            txtCustomUserAgent.Enabled = chkCustomUserAgent.Checked;
        }

        private void chkUseSlug_CheckedChanged(object sender, EventArgs e) {
            pnlSlug.Enabled = chkUseSlug.Checked;
        }

        private void chkRenameDownloadFolderWithParentThreadDescription_CheckedChanged(object sender, EventArgs e) {
            pnlParentThreadDescriptionFormat.Enabled = chkRenameDownloadFolderWithParentThreadDescription.Checked;
        }

        private void chkBackupThreadList_CheckedChanged(object sender, EventArgs e) {
            pnlBackupEvery.Enabled = chkBackupThreadList.Checked;
            chkBackupCheckSize.Enabled = chkBackupThreadList.Checked;
        }

        private void txtBackupEvery_Leave(object sender, EventArgs e) {
            int minutes;
            if (!Int32.TryParse(txtBackupEvery.Text, out minutes) || minutes < 1) {
                txtBackupEvery.Text = "1";
            }
        }

        private void txtMaximumKilobytesPerSecond_Leave(object sender, EventArgs e) {
            long kbps;
            if (!Int64.TryParse(txtMaximumKilobytesPerSecond.Text, out kbps) || kbps < 0 || kbps > Int64.MaxValue / 1024) {
                txtMaximumKilobytesPerSecond.Text = "0";
            }
        }

        private void btnWindowTitle_Click(object sender, EventArgs e) {
            int selectionStart = txtWindowTitle.SelectionStart;
            string macro = String.Format("{{{0}}}", cboWindowTitle.SelectedValue);
            txtWindowTitle.Text = txtWindowTitle.Text.Insert(selectionStart, macro);
            txtWindowTitle.SelectionStart = selectionStart + macro.Length;
        }

        private void SetDownloadFolderTextBox(string path) {
            txtDownloadFolder.Text = chkDownloadFolderRelative.Checked ?
                General.GetRelativeDirectoryPath(path, Settings.ExeDirectory) :
                General.GetAbsoluteDirectoryPath(path, Settings.ExeDirectory);
        }

        private void SetCompletedFolderTextBox(string path) {
            txtCompletedFolder.Text = chkCompletedFolderRelative.Checked ?
                General.GetRelativeDirectoryPath(path, Settings.ExeDirectory) :
                General.GetAbsoluteDirectoryPath(path, Settings.ExeDirectory);
        }
    }
}