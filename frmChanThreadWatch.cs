using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using JDP.Properties;

namespace JDP {
    public partial class frmChanThreadWatch : Form {
        private Dictionary<long, DownloadProgressInfo> _downloadProgresses = new Dictionary<long, DownloadProgressInfo>();
        private frmDownloads _downloadForm;
        private object _startupPromptSync = new object();
        private bool _isExiting;
        private bool _saveThreadList;
        private int _itemAreaY;
        private int[] _columnWidths;
        private object _cboCheckEveryLastValue;
        private bool _isLoadingThreadsFromFile;
        private static Dictionary<string, int> _categories = new Dictionary<string, int>();
        private static Dictionary<string, ThreadWatcher> _watchers = new Dictionary<string, ThreadWatcher>();
        private static HashSet<string> _blacklist = new HashSet<string>();
        private static readonly Dictionary<StopReason, string> _stopReasonTexts = new Dictionary<StopReason, string> {
            { StopReason.UserRequest, "User requested" },
            { StopReason.Exiting, "Exiting" },
            { StopReason.PageNotFound, "Page not found" },
            { StopReason.DownloadComplete, "Download complete" },
            { StopReason.IOError, "Error writing to disk" }
        };

        // ReleaseDate property and version in AssemblyInfo.cs should be updated for each release.

        public frmChanThreadWatch() {
            InitializeComponent();
            Icon = Resources.ChanThreadWatchIcon;
            niTrayIcon.Icon = Resources.ChanThreadWatchIcon;
            Settings.Load();
            EnsureLogFileExists();
            int initialWidth = ClientSize.Width;
            GUI.SetFontAndScaling(this);
            float scaleFactorX = (float)ClientSize.Width / initialWidth;
            RestoreClientSize();
            RestoreColumns(scaleFactorX);
            GUI.EnableDoubleBuffering(lvThreads);

            BindCheckEveryList();
            BuildCheckEverySubMenu();
            BuildColumnHeaderMenu();

            EnsureDownloadFolderExists();
            if (Settings.CheckEvery == 1) {
                Settings.CheckEvery = 0;
            }

            LoadAuthSettings();
            LoadDownloadOptionSettings();
            LoadCheckEverySetting();
            OnThreadDoubleClick = Settings.OnThreadDoubleClick ?? ThreadDoubleClickAction.OpenFolder;

            if (IsUpdateCheckDue()) {
                CheckForUpdates();
            }
            niTrayIcon.Visible = Settings.MinimizeToTray ?? false;
        }

        private static void EnsureLogFileExists() {
            string logPath = Path.Combine(Settings.GetSettingsDirectory(), Settings.LogFileName);
            if (!File.Exists(logPath)) {
                try { File.Create(logPath); }
                catch { }
            }
        }

        private void RestoreClientSize() {
            if (Settings.ClientSize == null) return;
            Size newSize = Settings.ClientSize.Value + Size - ClientSize;
            if (newSize.Width >= MinimumSize.Width && newSize.Height >= MinimumSize.Height) {
                ClientSize = Settings.ClientSize.Value;
            }
        }

        private void RestoreColumns(float scaleFactorX) {
            _columnWidths = new int[lvThreads.Columns.Count];
            for (int iColumn = 0; iColumn < lvThreads.Columns.Count; iColumn++) {
                ColumnHeader column = lvThreads.Columns[iColumn];
                RestoreColumnWidth(column, iColumn, scaleFactorX);
                _columnWidths[iColumn] = column.Width != 0 ? column.Width : Settings.DefaultColumnWidths[iColumn];
                if (IsValidSavedColumnIndex(iColumn)) {
                    column.DisplayIndex = Settings.ColumnIndices[iColumn];
                }
            }
        }

        private static void RestoreColumnWidth(ColumnHeader column, int iColumn, float scaleFactorX) {
            if (iColumn < Settings.ColumnWidths.Length) {
                column.Width = Settings.ColumnWidths[iColumn] > 0 ? Settings.ColumnWidths[iColumn] : 0;
            }
            else {
                column.Width = Convert.ToInt32(column.Width * scaleFactorX);
            }
        }

        private bool IsValidSavedColumnIndex(int iColumn) {
            return iColumn < Settings.ColumnIndices.Length && Settings.ColumnIndices[iColumn] > 0 && Settings.ColumnIndices[iColumn] < lvThreads.Columns.Count;
        }

        private static void EnsureDownloadFolderExists() {
            if ((Settings.DownloadFolder == null) || !Directory.Exists(Settings.AbsoluteDownloadDirectory)) {
                Settings.DownloadFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Watched Threads");
                Settings.DownloadFolderIsRelative = false;
            }
        }

        private void LoadAuthSettings() {
            chkPageAuth.Checked = Settings.UsePageAuth ?? false;
            txtPageAuth.Text = Settings.PageAuth ?? String.Empty;
            chkImageAuth.Checked = Settings.UseImageAuth ?? false;
            txtImageAuth.Text = Settings.ImageAuth ?? String.Empty;
        }

        private void LoadDownloadOptionSettings() {
            chkOneTime.Checked = Settings.OneTimeDownload ?? false;
            chkAutoFollow.Checked = Settings.AutoFollow ?? false;
        }

        private void LoadCheckEverySetting() {
            if (Settings.CheckEvery == null) {
                cboCheckEvery.SelectedValue = 3;
                return;
            }
            foreach (ListItemInt32 item in cboCheckEvery.Items) {
                if (item.Value != Settings.CheckEvery) continue;
                cboCheckEvery.SelectedValue = Settings.CheckEvery;
                break;
            }
            if ((int)cboCheckEvery.SelectedValue != Settings.CheckEvery) txtCheckEvery.Text = Settings.CheckEvery.ToString();
        }

        private static bool IsUpdateCheckDue() {
            return (Settings.CheckForUpdates == true) && (Settings.LastUpdateCheck ?? DateTime.MinValue) < DateTime.Now.Date;
        }

        public Dictionary<long, DownloadProgressInfo> DownloadProgresses {
            get { return _downloadProgresses; }
        }

        private ThreadDoubleClickAction OnThreadDoubleClick {
            get {
                if (rbEdit.Checked)
                    return ThreadDoubleClickAction.Edit;
                else if (rbOpenURL.Checked)
                    return ThreadDoubleClickAction.OpenURL;
                else
                    return ThreadDoubleClickAction.OpenFolder;
            }
            set {
                if (value == ThreadDoubleClickAction.Edit)
                    rbEdit.Checked = true;
                else if (value == ThreadDoubleClickAction.OpenURL)
                    rbOpenURL.Checked = true;
                else
                    rbOpenFolder.Checked = true;
            }
        }

        private void frmChanThreadWatch_Shown(object sender, EventArgs e) {
            UseWaitCursor = true;
            btnAdd.Enabled = false;
            btnAddFromClipboard.Enabled = false;
            btnRemoveCompleted.Enabled = false;
            btnDownloads.Enabled = false;
            btnSettings.Enabled = false;
            btnAbout.Enabled = false;
            btnHelp.Enabled = false;
            lvThreads.Enabled = false;
            Application.DoEvents();

            lvThreads.Items.Add(new ListViewItem());
            _itemAreaY = lvThreads.GetItemRect(0).Y;
            lvThreads.Items.RemoveAt(0);
            
            Thread thread = new Thread(() => {
                LoadThreadList();
                LoadBlacklist();

                Invoke(() => {
                    UseWaitCursor = false;
                    btnAdd.Enabled = true;
                    btnAddFromClipboard.Enabled = true;
                    btnRemoveCompleted.Enabled = true;
                    btnDownloads.Enabled = true;
                    btnSettings.Enabled = true;
                    btnAbout.Enabled = true;
                    btnHelp.Enabled = true;
                    lvThreads.Enabled = true;

                    lvThreads.ListViewItemSorter = new ListViewItemSorter(Settings.SortColumn ?? (int)ColumnIndex.AddedOn) { Ascending = Settings.SortAscending ?? true };
                    lvThreads.Sort();
                    FocusLastThread();
                });
            });
            thread.Start();
        }

        private void frmChanThreadWatch_FormClosed(object sender, FormClosedEventArgs e) {
            if (IsDisposed) return;
            SaveControlSettings();
            SaveColumnSettings();

            Settings.Save();

            foreach (ThreadWatcher watcher in ThreadWatchers) {
                watcher.Stop(StopReason.Exiting);
            }

            // Save before waiting in addition to after in case the wait hangs or is interrupted
            SaveThreadList();

            _isExiting = true;
            WaitForThreadWatchersToStop();

            SaveThreadList();

            Program.ReleaseMutex();
        }

        private void SaveControlSettings() {
            Settings.UsePageAuth = chkPageAuth.Checked;
            Settings.PageAuth = txtPageAuth.Text;
            Settings.UseImageAuth = chkImageAuth.Checked;
            Settings.ImageAuth = txtImageAuth.Text;
            Settings.OneTimeDownload = chkOneTime.Checked;
            Settings.AutoFollow = chkAutoFollow.Checked;
            Settings.CheckEvery = pnlCheckEvery.Enabled ? GetCheckEveryMinutes() : 0;
            Settings.OnThreadDoubleClick = OnThreadDoubleClick;
            if (WindowState == FormWindowState.Normal) {
                Settings.ClientSize = ClientSize;
            }
        }

        private void SaveColumnSettings() {
            int[] columnWidths = new int[lvThreads.Columns.Count];
            int[] columnIndices = new int[lvThreads.Columns.Count];
            for (int i = 0; i < lvThreads.Columns.Count; i++) {
                columnWidths[i] = lvThreads.Columns[i].Width;
                columnIndices[i] = lvThreads.Columns[i].DisplayIndex;
            }
            Settings.ColumnWidths = columnWidths;
            Settings.ColumnIndices = columnIndices;

            ListViewItemSorter sorter = (ListViewItemSorter)lvThreads.ListViewItemSorter;
            if (sorter != null) {
                Settings.SortColumn = sorter.Column;
                Settings.SortAscending = sorter.Ascending;
            }
        }

        private void WaitForThreadWatchersToStop() {
            foreach (ThreadWatcher watcher in ThreadWatchers) {
                while (!watcher.WaitUntilStopped(10) || !watcher.WaitReparse(10)) {
                    Application.DoEvents();
                }
            }
        }

        private int GetCheckEveryMinutes() {
            return cboCheckEvery.Enabled ? (int)cboCheckEvery.SelectedValue : Int32.Parse(txtCheckEvery.Text);
        }

        private void frmChanThreadWatch_DragEnter(object sender, DragEventArgs e) {
            if (e.Data.GetDataPresent("UniformResourceLocatorW") ||
                e.Data.GetDataPresent("UniformResourceLocator"))
            {
                if ((e.AllowedEffect & DragDropEffects.Copy) != 0) {
                    e.Effect = DragDropEffects.Copy;
                }
                else if ((e.AllowedEffect & DragDropEffects.Link) != 0) {
                    e.Effect = DragDropEffects.Link;
                }
            }
        }

        private void frmChanThreadWatch_DragDrop(object sender, DragEventArgs e) {
            if (_isExiting) return;
            string url = null;
            if (e.Data.GetDataPresent("UniformResourceLocatorW")) {
                byte[] data = ((MemoryStream)e.Data.GetData("UniformResourceLocatorW")).ToArray();
                url = Encoding.Unicode.GetString(data, 0, General.StrLenW(data) * 2);
            }
            else if (e.Data.GetDataPresent("UniformResourceLocator")) {
                byte[] data = ((MemoryStream)e.Data.GetData("UniformResourceLocator")).ToArray();
                url = Encoding.Default.GetString(data, 0, General.StrLen(data));
            }
            url = General.CleanPageURL(url);
            if (url != null) {
                AddThread(url);
                FocusThread(url);
                _saveThreadList = true;
            }
        }

        private void frmChanThreadWatch_Resize(object sender, EventArgs e) {
            if (WindowState == FormWindowState.Minimized && Settings.MinimizeToTray == true) {
                Hide();
            }
        }
        
        private void frmChanThreadWatch_ResizeEnd(object sender, EventArgs e) {
            if (WindowState == FormWindowState.Normal) {
                Settings.ClientSize = ClientSize;
            }
        }

        private void txtPageURL_KeyDown(object sender, KeyEventArgs e) {
            if (e.KeyCode == Keys.Enter) {
                btnAdd_Click(txtPageURL, null);
                e.SuppressKeyPress = true;
            }
        }

        private void btnAdd_Click(object sender, EventArgs e) {
            if (_isExiting) return;
            if (txtPageURL.Text.Trim().Length == 0) return;
            string pageURL = General.CleanPageURL(txtPageURL.Text);
            if (pageURL == null) {
                MessageBox.Show(this, "The specified URL is invalid.", "Invalid URL", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!AddThread(pageURL)) {
                MessageBox.Show(this, "The same thread is already being watched, downloaded or has been blacklisted.", "Cannot Add Thread", MessageBoxButtons.OK, MessageBoxIcon.Error);
                txtPageURL.Clear();
                FocusThread(pageURL);
                return;
            }
            FocusThread(pageURL);
            txtPageURL.Clear();
            txtPageURL.Focus();
            _saveThreadList = true;
        }

        private void btnAddFromClipboard_Click(object sender, EventArgs e) {
            if (_isExiting) return;
            string text;
            try {
                text = Clipboard.GetText();
            }
            catch {
                return;
            }
            string[] urls = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (urls.Length > 0) {
                lvThreads.SelectedItems.Clear();
                lvThreads.Select();
            }
            AddThreadsFromURLs(urls);
            _saveThreadList = true;
        }

        private void AddThreadsFromURLs(string[] urls) {
            for (int iURL = 0; iURL < urls.Length; iURL++) {
                string url = General.CleanPageURL(urls[iURL]);
                if (url == null) continue;
                AddThread(url);
                if (urls.Length == 1) {
                    FocusThread(url);
                }
                else {
                    SelectThread(url);
                }
            }
        }

        private static void SelectThread(string pageURL) {
            SiteHelper siteHelper = SiteHelpers.GetInstance((new Uri(pageURL)).Host);
            siteHelper.SetURL(pageURL);
            ThreadWatcher watcher;
            if (_watchers.TryGetValue(siteHelper.GetPageID(), out watcher)) {
                (((WatcherExtraData)watcher.Tag).ListViewItem).Selected = true;
            }
        }

        private void btnRemoveCompleted_Click(object sender, EventArgs e) {
            if (Settings.MoveToCompletedFolder != true) {
                RemoveThreads(true, false);
            }
            else {
                if (!Directory.Exists(Settings.AbsoluteCompletedDirectory)) {
                    Settings.CompletedFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Completed Threads");
                    Settings.CompletedFolderIsRelative = false;
                }
                RemoveThreads(true, false, MoveThreadToCompletedFolder);
            }
        }

        private static void MoveThreadToCompletedFolder(ThreadWatcher watcher) {
            string destDir = Path.Combine(Settings.AbsoluteCompletedDirectory,
                General.GetRelativeDirectoryPath(watcher.ThreadDownloadDirectory, watcher.MainDownloadDirectory));
            if (Directory.Exists(watcher.ThreadDownloadDirectory)) {
                if (Directory.Exists(destDir)) {
                    Directory.Delete(destDir);
                }
                if (watcher.Category.Length != 0) {
                    Directory.CreateDirectory(General.RemoveLastDirectory(destDir));
                }
                Directory.Move(watcher.ThreadDownloadDirectory, destDir);
            }
            DeleteCategoryFolderIfEmpty(watcher);
        }

        private static void DeleteCategoryFolderIfEmpty(ThreadWatcher watcher) {
            string categoryPath = General.RemoveLastDirectory(watcher.ThreadDownloadDirectory);
            if (categoryPath != watcher.MainDownloadDirectory && Directory.GetFiles(categoryPath).Length == 0 && Directory.GetDirectories(categoryPath).Length == 0) {
                Directory.Delete(categoryPath);
            }
        }

        private void miStop_Click(object sender, EventArgs e) {
            foreach (ThreadWatcher watcher in SelectedThreadWatchers) {
                watcher.Stop(StopReason.UserRequest);
            }
            _saveThreadList = true;
        }

        private void miStart_Click(object sender, EventArgs e) {
            if (_isExiting) return;
            foreach (ThreadWatcher watcher in SelectedThreadWatchers) {
                if (!watcher.IsRunning && !watcher.IsReparsing) {
                    watcher.Start();
                }
            }
            _saveThreadList = true;
        }

        private void miEdit_Click(object sender, EventArgs e) {
            if (_isExiting) return;
            var selectedThreadWatchers = new List<ThreadWatcher>(SelectedThreadWatchers);
            if (selectedThreadWatchers.Count == 0) return;

            using (frmThreadEdit editForm = new frmThreadEdit(selectedThreadWatchers, _categories)) {
                if (editForm.ShowDialog(this) == DialogResult.OK && editForm.IsDirty) {
                    foreach (ThreadWatcher watcher in selectedThreadWatchers) {
                        ApplyThreadEdit(editForm, watcher);
                        DisplayData(watcher);
                    }
                    _saveThreadList = true;
                }
            }
        }

        private void ApplyThreadEdit(frmThreadEdit editForm, ThreadWatcher watcher) {
            if (editForm.Description.IsDirty) {
                watcher.Description = editForm.Description.Value;
            }
            if (editForm.Category.IsDirty) {
                UpdateCategories(watcher.Category, true);
                UpdateCategories(editForm.Category.Value);
                watcher.Category = editForm.Category.Value;
            }
            if (editForm.CheckIntervalSeconds.IsDirty) {
                watcher.CheckIntervalSeconds = editForm.CheckIntervalSeconds.Value;
            }
            if (!watcher.IsRunning) {
                ApplyStoppedThreadEdit(editForm, watcher);
            }
        }

        private static void ApplyStoppedThreadEdit(frmThreadEdit editForm, ThreadWatcher watcher) {
            if (editForm.PageAuth.IsDirty) {
                watcher.PageAuth = editForm.PageAuth.Value;
            }
            if (editForm.ImageAuth.IsDirty) {
                watcher.ImageAuth = editForm.ImageAuth.Value;
            }
            if (editForm.OneTimeDownload.IsDirty) {
                watcher.OneTimeDownload = editForm.OneTimeDownload.Value;
            }
            if (editForm.AutoFollow.IsDirty) {
                watcher.AutoFollow = editForm.AutoFollow.Value;
            }
        }

        private void miOpenFolder_Click(object sender, EventArgs e) {
            int selectedCount = lvThreads.SelectedItems.Count;
            if (selectedCount > 5 && MessageBox.Show(this, "Do you want to open the folders of all " + selectedCount + " selected items?",
                "Open Folders", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }
            foreach (ThreadWatcher watcher in SelectedThreadWatchers) {
                string dir = watcher.ThreadDownloadDirectory;
                ThreadWatcher tmpWatcher = watcher;
                ThreadPool.QueueUserWorkItem((s) => {
                    try {
                        if (!Directory.Exists(dir)) {
                            tmpWatcher.Stop(StopReason.Other);
                            BeginInvoke(() => {
                                MessageBox.Show(this, "The folder " + dir + " does not exists. The watcher has been stopped to let you fix this, in case of an unwanted deletion or rename. If the thread file cannot be found for the next check, it won't include possible deleted posts.",
                                    "Folder Not Found", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            });
                        }
                        else {
                            Process.Start(dir);
                        }
                    }
                    catch (Exception ex) {
                        Logger.Log(ex.ToString());
                    }
                });
            }
        }

        private void miOpenURL_Click(object sender, EventArgs e) {
            int selectedCount = lvThreads.SelectedItems.Count;
            if (selectedCount > 5 && MessageBox.Show(this, "Do you want to open the URLs of all " + selectedCount + " selected items?",
                "Open URLs", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }
            foreach (ThreadWatcher watcher in SelectedThreadWatchers) {
                string url = watcher.PageURL;
                ThreadPool.QueueUserWorkItem((s) => {
                    try {
                        Process.Start(url);
                    }
                    catch (Exception ex) {
                        Logger.Log(ex.ToString());
                    }
                });
            }
        }

        private void miCopyURL_Click(object sender, EventArgs e) {
            StringBuilder sb = new StringBuilder();
            foreach (ThreadWatcher watcher in SelectedThreadWatchers) {
                if (sb.Length != 0) sb.Append(Environment.NewLine);
                sb.Append(watcher.PageURL);
            }
            try {
                Clipboard.Clear();
                Clipboard.SetText(sb.ToString());
            }
            catch (Exception ex) {
                MessageBox.Show(this, "Unable to copy to clipboard: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void miRemove_Click(object sender, EventArgs e) {
            RemoveThreads(false, true);
        }

        private void miRemoveAndDeleteFolder_Click(object sender, EventArgs e) {
            if (MessageBox.Show(this, "Are you sure you want to delete the selected threads and all associated files from disk?",
                "Delete From Disk", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }
            RemoveThreads(false, true, DeleteThreadFolder);
        }

        private static void DeleteThreadFolder(ThreadWatcher watcher) {
            if (Directory.Exists(watcher.ThreadDownloadDirectory)) Directory.Delete(watcher.ThreadDownloadDirectory, true);
            DeleteCategoryFolderIfEmpty(watcher);
        }

        private void miBlacklist_Click(object sender, EventArgs e) {
            if (_isExiting) return;
            List<string> lines = new List<string>();
            foreach (string rule in _blacklist) {
                lines.Add(rule);
            }
            HashSet<string> blacklist = new HashSet<string>();
            foreach (ThreadWatcher watcher in SelectedThreadWatchers) {
                if (!_blacklist.Contains(watcher.PageID) && blacklist.Add(watcher.PageID)) {
                    lines.Add(watcher.PageID);
                }
            }
            try {
                string path = Path.Combine(Settings.GetSettingsDirectory(), Settings.BlacklistFileName);
                File.WriteAllLines(path, lines.ToArray());
                foreach (string pageID in blacklist) {
                    _blacklist.Add(pageID);
                }
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
        }

        private void miCheckNow_Click(object sender, EventArgs e) {
            foreach (ThreadWatcher watcher in SelectedThreadWatchers) {
                watcher.MillisecondsUntilNextCheck = 0;
            }
        }

        private void miCheckEvery_Click(object sender, EventArgs e) {
            MenuItem menuItem = sender as MenuItem;
            if (menuItem != null) {
                int checkIntervalSeconds = Convert.ToInt32(menuItem.Tag) * 60;
                foreach (ThreadWatcher watcher in SelectedThreadWatchers) {
                    watcher.CheckIntervalSeconds = checkIntervalSeconds;
                }
                UpdateWaitingWatcherStatuses();
            }
            _saveThreadList = true;
        }

        private void miReparse_Click(object sender, EventArgs e) {
            if (_isExiting) return;
            foreach (ThreadWatcher watcher in SelectedThreadWatchers) {
                if (!watcher.IsRunning && !watcher.IsReparsing && Settings.SaveThumbnails != false) {
                    watcher.BeginReparse();
                }
            }
            _saveThreadList = true;
        }

        private void btnDownloads_Click(object sender, EventArgs e) {
            if (_downloadForm != null && !_downloadForm.IsDisposed) {
                _downloadForm.Activate();
            }
            else {
                _downloadForm = new frmDownloads(this);
                GUI.CenterChildForm(this, _downloadForm);
                _downloadForm.Show(this);
            }
        }

        private void btnSettings_Click(object sender, EventArgs e) {
            if (_isExiting) return;
            using (frmSettings settingsForm = new frmSettings()) {
                GUI.CenterChildForm(this, settingsForm);
                settingsForm.ShowDialog(this);
            }
            niTrayIcon.Visible = Settings.MinimizeToTray ?? false;
            tmrBackupThreadList.Interval = (Settings.BackupEvery ?? 1) * 60 * 1000;
            UpdateWindowTitle(GetMonitoringInfo());
        }

        private void btnAbout_Click(object sender, EventArgs e) {
            MessageBox.Show(this, String.Format("Chan Thread Watch{0}Version {1} ({2}){0}{0}Original Author: JDP (jart1126@yahoo.com){0}http://sites.google.com/site/chanthreadwatch/" +
                                                "{0}{0}Maintained by: SuperGouge (https://github.com/SuperGouge){0}{3}",
                Environment.NewLine, General.Version, General.ReleaseDate, General.ProgramURL), "About",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        
        private void btnHelp_Click(object sender, EventArgs e) {
            Process.Start(General.WikiURL);
        }

        private void lvThreads_KeyDown(object sender, KeyEventArgs e) {
            if (e.KeyCode == Keys.Delete) {
                RemoveThreads(false, true);
                return;
            }
            if (!e.Control) return;
            if (e.KeyCode == Keys.A) {
                foreach (ListViewItem item in lvThreads.Items) {
                    item.Selected = true;
                }
            }
            else if (e.KeyCode == Keys.I) {
                foreach (ListViewItem item in lvThreads.Items) {
                    item.Selected = !item.Selected;
                }
            }
        }

        private void lvThreads_MouseClick(object sender, MouseEventArgs e) {
            if (e.Button != MouseButtons.Right) return;
            int selectedCount = lvThreads.SelectedItems.Count;
            if (selectedCount == 0) return;
            bool anyRunning = false;
            bool anyStopped = false;
            bool anyNotReparsing = false;
            foreach (ThreadWatcher watcher in SelectedThreadWatchers) {
                bool isRunning = watcher.IsRunning;
                anyRunning |= isRunning;
                anyStopped |= !isRunning;
                anyNotReparsing |= !watcher.IsReparsing;
            }
            bool anyStoppedAndNotReparsing = anyStopped && anyNotReparsing;
            miStop.Visible = anyRunning;
            miStart.Visible = anyStoppedAndNotReparsing;
            miCheckNow.Visible = anyRunning;
            miCheckEvery.Visible = anyRunning;
            miRemove.Visible = anyStoppedAndNotReparsing;
            miRemoveAndDeleteFolder.Visible = anyStoppedAndNotReparsing;
            miReparse.Visible = anyStoppedAndNotReparsing;
            cmThreads.Show(lvThreads, e.Location);
        }

        private void lvThreads_MouseDoubleClick(object sender, MouseEventArgs e) {
            if (OnThreadDoubleClick == ThreadDoubleClickAction.Edit) {
                miEdit_Click(null, null);
            }
            else if (OnThreadDoubleClick == ThreadDoubleClickAction.OpenFolder) {
                miOpenFolder_Click(null, null);
            }
            else {
                miOpenURL_Click(null, null);
            }
        }

        private void lvThreads_ColumnClick(object sender, ColumnClickEventArgs e) {
            ListViewItemSorter sorter = (ListViewItemSorter)lvThreads.ListViewItemSorter;
            if (sorter == null) {
                sorter = new ListViewItemSorter(e.Column);
                lvThreads.ListViewItemSorter = sorter;
            }
            else if (e.Column != sorter.Column) {
                sorter.Column = e.Column;
                sorter.Ascending = true;
            }
            else {
                sorter.Ascending = !sorter.Ascending;
            }
            lvThreads.Sort();
        }

        private void chkOneTime_CheckedChanged(object sender, EventArgs e) {
            pnlCheckEvery.Enabled = !chkOneTime.Checked;
        }

        private void chkPageAuth_CheckedChanged(object sender, EventArgs e) {
            txtPageAuth.Enabled = chkPageAuth.Checked;
        }

        private void chkImageAuth_CheckedChanged(object sender, EventArgs e) {
            txtImageAuth.Enabled = chkImageAuth.Checked;
        }

        private void txtCheckEvery_TextChanged(object sender, EventArgs e) {
            int checkEvery;
            if (Int32.TryParse(txtCheckEvery.Text, out checkEvery)) {
                cboCheckEvery.SelectedIndex = -1;
                cboCheckEvery.Enabled = false;
            }
            else {
                if (cboCheckEvery.SelectedIndex == -1) cboCheckEvery.SelectedValue = _cboCheckEveryLastValue;
                cboCheckEvery.Enabled = true;
            }
        }

        private void cboCheckEvery_SelectedIndexChanged(object sender, EventArgs e) {
            if (cboCheckEvery.SelectedIndex == -1) return;
            if (cboCheckEvery.Focused) txtCheckEvery.Clear();
            if (ShouldDefaultCheckEveryLastValue()) {
                _cboCheckEveryLastValue = 3;
            }
            else {
                _cboCheckEveryLastValue = cboCheckEvery.SelectedValue;
            }
        }

        private bool ShouldDefaultCheckEveryLastValue() {
            return _cboCheckEveryLastValue == null && (int)cboCheckEvery.SelectedValue == 0 && (int)cboCheckEvery.SelectedValue != Settings.CheckEvery;
        }

        private void tmrSaveThreadList_Tick(object sender, EventArgs e) {
            if (_saveThreadList && !_isExiting) {
                SaveThreadList();
                _saveThreadList = false;
            }
        }

        private void tmrUpdateWaitStatus_Tick(object sender, EventArgs e) {
            UpdateWaitingWatcherStatuses();
        }

        private void tmrMaintenance_Tick(object sender, EventArgs e) {
            lock (_downloadProgresses) {
                if (_downloadProgresses.Count == 0) return;
                List<long> oldDownloadIDs = new List<long>();
                long ticksNow = TickCount.Now;
                foreach (DownloadProgressInfo info in _downloadProgresses.Values) {
                    if (info.EndTicks != null && ticksNow - info.EndTicks.Value > 5000) {
                        oldDownloadIDs.Add(info.DownloadID);
                    }
                }
                foreach (long downloadID in oldDownloadIDs) {
                    _downloadProgresses.Remove(downloadID);
                }
            }
        }
        
        private void tmrMonitor_Tick(object sender, EventArgs e) {
            MonitoringInfo monitoringInfo = GetMonitoringInfo();
            UpdateWindowTitle(monitoringInfo);
            miMonitorTotal.Text = String.Format("Watching {0} thread{1}", monitoringInfo.TotalThreads, monitoringInfo.TotalThreads != 1 ? "s" : String.Empty);
            miMonitorRunning.Text = String.Format("    {0} running", monitoringInfo.RunningThreads);
            miMonitorDead.Text = String.Format("    {0} dead", monitoringInfo.DeadThreads);
            miMonitorStopped.Text = String.Format("    {0} stopped", monitoringInfo.StoppedThreads);
        }

        private void tmrBackupThreadList_Tick(object sender, EventArgs e) {
            if (Settings.BackupThreadList == true) {
                General.BackupThreadList(Settings.BackupCheckSize ?? false);
            }
        }

        private void niTrayIcon_Click(object sender, EventArgs e) {
            // Nothing for now
        }

        private void niTrayIcon_DoubleClick(object sender, EventArgs e) {
            Show();
            WindowState = FormWindowState.Normal;
        }

        private void miExit_Click(object sender, EventArgs e) {
            Close();
        }

        private void ThreadWatcher_DownloadStatus(ThreadWatcher watcher, DownloadStatusEventArgs args) {
            WatcherExtraData extraData = (WatcherExtraData)watcher.Tag;
            bool isInitialPageDownload = TrackPageDownload(extraData, args.DownloadType);
            bool isFirstImageUpdate = TrackImageDownload(extraData, args.DownloadType);
            BeginInvoke(() => {
                SetDownloadStatus(watcher, args.DownloadType, args.CompleteCount, args.TotalCount);
                if (isInitialPageDownload) {
                    DisplayDescription(watcher);
                    _saveThreadList = true;
                }
                if (isFirstImageUpdate) {
                    DisplayLastImageOn(watcher);
                    _saveThreadList = true;
                }
                SetupWaitTimer();
            });
        }

        // Returns true if this is the first page download for the watcher.
        private static bool TrackPageDownload(WatcherExtraData extraData, DownloadType downloadType) {
            if (downloadType != DownloadType.Page) return false;
            bool isInitialPageDownload = false;
            if (!extraData.HasDownloadedPage) {
                extraData.HasDownloadedPage = true;
                isInitialPageDownload = true;
            }
            extraData.PreviousDownloadWasPage = true;
            return isInitialPageDownload;
        }

        // Returns true if this is the first image download since the last page download.
        private static bool TrackImageDownload(WatcherExtraData extraData, DownloadType downloadType) {
            if (downloadType != DownloadType.Image || !extraData.PreviousDownloadWasPage) return false;
            extraData.LastImageOn = DateTime.Now;
            extraData.PreviousDownloadWasPage = false;
            return true;
        }

        private void ThreadWatcher_WaitStatus(ThreadWatcher watcher, EventArgs args) {
            BeginInvoke(() => {
                SetWaitStatus(watcher);
                SetupWaitTimer();
            });
        }

        private void ThreadWatcher_StopStatus(ThreadWatcher watcher, StopStatusEventArgs args) {
            BeginInvoke(() => {
                SetStopStatus(watcher, args.StopReason);
                SetupWaitTimer();
                if (args.StopReason != StopReason.UserRequest && args.StopReason != StopReason.Exiting) {
                    _saveThreadList = true;
                }
            });
        }

        private void ThreadWatcher_ReparseStatus(ThreadWatcher watcher, ReparseStatusEventArgs args) {
            BeginInvoke(() => {
                SetReparseStatus(watcher, args.ReparseType, args.CompleteCount, args.TotalCount);
                SetupWaitTimer();
            });
        }

        private void ThreadWatcher_ThreadDownloadDirectoryRename(ThreadWatcher watcher, EventArgs args) {
            BeginInvoke(() => {
                _saveThreadList = true;
            });
        }

        private void ThreadWatcher_DownloadStart(ThreadWatcher watcher, DownloadStartEventArgs args) {
            DownloadProgressInfo info = new DownloadProgressInfo();
            info.DownloadID = args.DownloadID;
            info.URL = args.URL;
            info.TryNumber = args.TryNumber;
            info.StartTicks = TickCount.Now;
            info.TotalSize = args.TotalSize;
            lock (_downloadProgresses) {
                _downloadProgresses[args.DownloadID] = info;
            }
        }

        private void ThreadWatcher_DownloadProgress(ThreadWatcher watcher, DownloadProgressEventArgs args) {
            lock (_downloadProgresses) {
                DownloadProgressInfo info;
                if (!_downloadProgresses.TryGetValue(args.DownloadID, out info)) return;
                info.DownloadedSize = args.DownloadedSize;
                _downloadProgresses[args.DownloadID] = info;
            }
        }

        private void ThreadWatcher_DownloadEnd(ThreadWatcher watcher, DownloadEndEventArgs args) {
            lock (_downloadProgresses) {
                DownloadProgressInfo info;
                if (!_downloadProgresses.TryGetValue(args.DownloadID, out info)) return;
                info.EndTicks = TickCount.Now;
                info.DownloadedSize = args.DownloadedSize;
                info.TotalSize = args.DownloadedSize;
                _downloadProgresses[args.DownloadID] = info;
            }
        }

        private void ThreadWatcher_AddThread(ThreadWatcher watcher, AddThreadEventArgs args) {
            BeginInvoke(() => {
                ThreadInfo thread = new ThreadInfo {
                    URL = args.PageURL,
                    PageAuth = watcher.PageAuth,
                    ImageAuth = watcher.ImageAuth,
                    CheckIntervalSeconds = watcher.CheckIntervalSeconds,
                    OneTimeDownload = watcher.OneTimeDownload,
                    SaveDir = null,
                    Description = String.Empty,
                    StopReason = null,
                    ExtraData = new WatcherExtraData {
                        AddedOn = DateTime.Now,
                        AddedFrom = watcher.PageID
                    },
                    Category = watcher.Category,
                    AutoFollow = Settings.RecursiveAutoFollow != false
                };
                SiteHelper siteHelper = SiteHelpers.GetInstance((new Uri(thread.URL)).Host);
                siteHelper.SetURL(thread.URL);
                if (_watchers.ContainsKey(siteHelper.GetPageID())) return;
                if (AddThread(thread)) {
                    _saveThreadList = true;
                }
            });
        }

        private bool AddThread(string pageURL) {
            ThreadInfo thread = new ThreadInfo {
                URL = pageURL,
                PageAuth = GetAuthText(chkPageAuth, txtPageAuth),
                ImageAuth = GetAuthText(chkImageAuth, txtImageAuth),
                CheckIntervalSeconds = pnlCheckEvery.Enabled ? GetCheckEveryMinutes() * 60 : 0,
                OneTimeDownload = chkOneTime.Checked,
                SaveDir = null,
                Description = String.Empty,
                StopReason = null,
                ExtraData = null,
                Category = cboCategory.Text,
                AutoFollow = chkAutoFollow.Checked
            };
            return AddThread(thread);
        }

        private static string GetAuthText(CheckBox chkAuth, TextBox txtAuth) {
            return (chkAuth.Checked && (txtAuth.Text.IndexOf(':') != -1)) ? txtAuth.Text : String.Empty;
        }

        private bool AddThread(ThreadInfo thread) {
            ThreadWatcher watcher = null;
            ThreadWatcher parentThread = null;
            ListViewItem newListViewItem = null;
            SiteHelper siteHelper = SiteHelpers.GetInstance((new Uri(thread.URL)).Host);
            siteHelper.SetURL(thread.URL);
            string pageID = siteHelper.GetPageID();
            if (IsBlacklisted(pageID)) return false;

            if (_watchers.ContainsKey(pageID)) {
                watcher = _watchers[pageID];
                if (watcher.IsRunning) return false;
            }

            if (watcher == null) {
                watcher = CreateThreadWatcher(thread, out parentThread);
                newListViewItem = AddThreadListViewItem(watcher);
                UpdateCategories(watcher.Category);
            }

            ApplyThreadInfo(watcher, thread);
            AttachExtraData(watcher, thread, newListViewItem);
            RegisterThreadWatcher(watcher, parentThread);
            DisplayData(watcher);

            StartOrStopAddedThread(watcher, thread);
            return true;
        }

        private ThreadWatcher CreateThreadWatcher(ThreadInfo thread, out ThreadWatcher parentThread) {
            parentThread = null;
            ThreadWatcher watcher = new ThreadWatcher(thread.URL);
            watcher.ThreadDownloadDirectory = thread.SaveDir;
            watcher.Description = thread.Description;
            if (_isLoadingThreadsFromFile) watcher.DoNotRename = true;
            watcher.Category = thread.Category;
            watcher.DoNotRename = false;
            if (thread.ExtraData != null && !String.IsNullOrEmpty(thread.ExtraData.AddedFrom)) {
                _watchers.TryGetValue(thread.ExtraData.AddedFrom, out parentThread);
                watcher.ParentThread = parentThread;
            }
            SubscribeThreadWatcherEvents(watcher);
            return watcher;
        }

        private void SubscribeThreadWatcherEvents(ThreadWatcher watcher) {
            watcher.DownloadStatus += ThreadWatcher_DownloadStatus;
            watcher.WaitStatus += ThreadWatcher_WaitStatus;
            watcher.StopStatus += ThreadWatcher_StopStatus;
            watcher.ReparseStatus += ThreadWatcher_ReparseStatus;
            watcher.ThreadDownloadDirectoryRename += ThreadWatcher_ThreadDownloadDirectoryRename;
            watcher.DownloadStart += ThreadWatcher_DownloadStart;
            watcher.DownloadProgress += ThreadWatcher_DownloadProgress;
            watcher.DownloadEnd += ThreadWatcher_DownloadEnd;
            watcher.AddThread += ThreadWatcher_AddThread;
        }

        private ListViewItem AddThreadListViewItem(ThreadWatcher watcher) {
            ListViewItem newListViewItem = new ListViewItem(String.Empty);
            for (int i = 1; i < lvThreads.Columns.Count; i++) {
                newListViewItem.SubItems.Add(String.Empty);
            }
            newListViewItem.Tag = watcher;
            lvThreads.Items.Add(newListViewItem);
            lvThreads.Sort();
            return newListViewItem;
        }

        private static void ApplyThreadInfo(ThreadWatcher watcher, ThreadInfo thread) {
            watcher.PageAuth = thread.PageAuth;
            watcher.ImageAuth = thread.ImageAuth;
            watcher.CheckIntervalSeconds = thread.CheckIntervalSeconds;
            watcher.OneTimeDownload = thread.OneTimeDownload;
            watcher.AutoFollow = thread.AutoFollow;
        }

        private static void AttachExtraData(ThreadWatcher watcher, ThreadInfo thread, ListViewItem newListViewItem) {
            if (thread.ExtraData == null) {
                thread.ExtraData = watcher.Tag as WatcherExtraData ?? new WatcherExtraData { AddedOn = DateTime.Now };
            }
            if (newListViewItem != null) {
                thread.ExtraData.ListViewItem = newListViewItem;
            }
            watcher.Tag = thread.ExtraData;
        }

        private static void RegisterThreadWatcher(ThreadWatcher watcher, ThreadWatcher parentThread) {
            if (parentThread != null) parentThread.ChildThreads.Add(watcher.PageID, watcher);
            if (!_watchers.ContainsKey(watcher.PageID)) {
                _watchers.Add(watcher.PageID, watcher);
            }
            else {
                _watchers[watcher.PageID] = watcher;
            }
        }

        private void StartOrStopAddedThread(ThreadWatcher watcher, ThreadInfo thread) {
            if (thread.StopReason == null && !_isLoadingThreadsFromFile) {
                watcher.Start();
            }
            else if (thread.StopReason != null) {
                watcher.Stop(thread.StopReason.Value);
            }
        }

        private void RemoveThreads(bool removeCompleted, bool removeSelected) {
            RemoveThreads(removeCompleted, removeSelected, null);
        }

        private void RemoveThreads(bool removeCompleted, bool removeSelected, Action<ThreadWatcher> preRemoveAction) {
            int i = 0;
            while (i < lvThreads.Items.Count) {
                ListViewItem item = lvThreads.Items[i];
                ThreadWatcher watcher = (ThreadWatcher)item.Tag;
                if (ShouldRemoveThread(watcher, item, removeCompleted, removeSelected)) {
                    RunPreRemoveAction(preRemoveAction, watcher);
                    UpdateCategories(watcher.Category, true);
                    lvThreads.Items.RemoveAt(i);
                    _watchers.Remove(watcher.PageID);
                }
                else {
                    i++;
                }
            }
            _saveThreadList = true;
        }

        private static bool ShouldRemoveThread(ThreadWatcher watcher, ListViewItem item, bool removeCompleted, bool removeSelected) {
            return (removeCompleted || (removeSelected && item.Selected)) && !watcher.IsRunning && !watcher.IsReparsing;
        }

        private static void RunPreRemoveAction(Action<ThreadWatcher> preRemoveAction, ThreadWatcher watcher) {
            if (preRemoveAction == null) return;
            try { preRemoveAction(watcher); }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
        }

        private void BindCheckEveryList() {
            cboCheckEvery.ValueMember = "Value";
            cboCheckEvery.DisplayMember = "Text";
            cboCheckEvery.DataSource = new[] {
                new ListItemInt32(0, "1 or <"),
                new ListItemInt32(2, "2"),
                new ListItemInt32(3, "3"),
                new ListItemInt32(5, "5"),
                new ListItemInt32(10, "10"),
                new ListItemInt32(60, "60")
            };
        }

        private void BuildCheckEverySubMenu() {
            for (int i = 0; i < cboCheckEvery.Items.Count; i++) {
                int minutes = ((ListItemInt32)cboCheckEvery.Items[i]).Value;
                MenuItem menuItem = new MenuItem {
                    Index = i,
                    Tag = minutes,
                    Text = minutes > 0 ? minutes + " Minutes" : "1 Minute or <"
                };
                menuItem.Click += miCheckEvery_Click;
                miCheckEvery.MenuItems.Add(menuItem);
            }
        }

        private void BuildColumnHeaderMenu() {
            ContextMenu contextMenu = new ContextMenu();
            contextMenu.Popup += (s, e) => {
                for (int i = 0; i < lvThreads.Columns.Count; i++) {
                    contextMenu.MenuItems[i].Checked = lvThreads.Columns[i].Width != 0;
                }
            };
            for (int i = 0; i < lvThreads.Columns.Count; i++) {
                MenuItem menuItem = new MenuItem {
                    Index = i,
                    Tag = i,
                    Text = lvThreads.Columns[i].Text
                };
                menuItem.Click += (s, e) => {
                    int iColumn = (int)((MenuItem)s).Tag;
                    ColumnHeader column = lvThreads.Columns[iColumn];
                    if (column.Width != 0) {
                        _columnWidths[iColumn] = column.Width;
                        column.Width = 0;
                    }
                    else {
                        column.Width = _columnWidths[iColumn];
                    }
                };
                contextMenu.MenuItems.Add(menuItem);
            }
            ContextMenuStrip contextMenuStrip = new ContextMenuStrip();
            contextMenuStrip.Opening += (s, e) => {
                e.Cancel = true;
                Point pos = lvThreads.PointToClient(Control.MousePosition);
                if (pos.Y >= _itemAreaY) return;
                contextMenu.Show(lvThreads, pos);
            };
            lvThreads.ContextMenuStrip = contextMenuStrip;
        }

        private void SetupWaitTimer() {
            bool anyWaiting = AnyThreadWatcherWaiting();
            if (!tmrUpdateWaitStatus.Enabled && anyWaiting) {
                tmrUpdateWaitStatus.Start();
            }
            else if (tmrUpdateWaitStatus.Enabled && !anyWaiting) {
                tmrUpdateWaitStatus.Stop();
            }
        }

        private bool AnyThreadWatcherWaiting() {
            foreach (ThreadWatcher watcher in ThreadWatchers) {
                if (watcher.IsWaiting) return true;
            }
            return false;
        }

        private void UpdateWaitingWatcherStatuses() {
            foreach (ThreadWatcher watcher in ThreadWatchers) {
                if (watcher.IsWaiting) {
                    SetWaitStatus(watcher);
                }
            }
        }

        private void SetSubItemText(ThreadWatcher watcher, ColumnIndex columnIndex, string text) {
            ListViewItem item = ((WatcherExtraData)watcher.Tag).ListViewItem;
            var subItem = item.SubItems[(int)columnIndex];
            if (subItem.Text != text) {
                subItem.Text = text;
                if (!_isLoadingThreadsFromFile) lvThreads.Sort();
            }
        }

        private void DisplayDescription(ThreadWatcher watcher) {
            SetSubItemText(watcher, ColumnIndex.Description, watcher.Description);
        }

        private void DisplayStatus(ThreadWatcher watcher, string status) {
            SetSubItemText(watcher, ColumnIndex.Status, status);
        }

        private void DisplayAddedOn(ThreadWatcher watcher) {
            DateTime time = ((WatcherExtraData)watcher.Tag).AddedOn;
            SetSubItemText(watcher, ColumnIndex.AddedOn, time.ToString("yyyy/MM/dd HH:mm:ss"));
        }

        private void DisplayLastImageOn(ThreadWatcher watcher) {
            DateTime? time = ((WatcherExtraData)watcher.Tag).LastImageOn;
            SetSubItemText(watcher, ColumnIndex.LastImageOn, time != null ? time.Value.ToString("yyyy/MM/dd HH:mm:ss") : String.Empty);
        }

        private void DisplayAddedFrom(ThreadWatcher watcher) {
            ThreadWatcher fromWatcher;
            _watchers.TryGetValue(((WatcherExtraData)watcher.Tag).AddedFrom ?? String.Empty, out fromWatcher);
            SetSubItemText(watcher, ColumnIndex.AddedFrom, fromWatcher != null ? fromWatcher.Description : String.Empty);
        }

        private void DisplayCategory(ThreadWatcher watcher) {
            SetSubItemText(watcher, ColumnIndex.Category, watcher.Category);
        }

        private void DisplayData(ThreadWatcher watcher) {
            DisplayDescription(watcher);
            DisplayAddedOn(watcher);
            DisplayLastImageOn(watcher);
            if (!_isLoadingThreadsFromFile) DisplayAddedFrom(watcher);
            DisplayCategory(watcher);
        }

        private void SetDownloadStatus(ThreadWatcher watcher, DownloadType downloadType, int completeCount, int totalCount) {
            string type;
            bool hideDetail = false;
            switch (downloadType) {
                case DownloadType.Page:
                    type = totalCount == 1 ? "page" : "pages";
                    hideDetail = totalCount == 1;
                    break;
                case DownloadType.Image:
                    type = "images";
                    break;
                case DownloadType.Thumbnail:
                    type = "thumbnails";
                    break;
                default:
                    return;
            }
            DisplayProgressStatus(watcher, "Downloading", type, hideDetail, completeCount, totalCount);
        }

        private void DisplayProgressStatus(ThreadWatcher watcher, string action, string type, bool hideDetail, int completeCount, int totalCount) {
            string status = hideDetail ? action + " " + type :
                String.Format("{0} {1}: {2} of {3} completed", action, type, completeCount, totalCount);
            DisplayStatus(watcher, status);
        }

        private void SetWaitStatus(ThreadWatcher watcher) {
            int remainingSeconds = (watcher.MillisecondsUntilNextCheck + 999) / 1000;
            DisplayStatus(watcher, String.Format("Waiting {0} seconds", remainingSeconds));
        }

        private void SetStopStatus(ThreadWatcher watcher, StopReason stopReason) {
            string reasonText;
            if (!_stopReasonTexts.TryGetValue(stopReason, out reasonText)) {
                reasonText = "Unknown error";
            }
            DisplayStatus(watcher, "Stopped: " + reasonText);
        }

        private void SetReparseStatus(ThreadWatcher watcher, ReparseType reparseType, int completeCount, int totalCount) {
            string type;
            bool hideDetail = false;
            switch (reparseType) {
                case ReparseType.Page:
                    type = totalCount == 1 ? "page" : "pages";
                    hideDetail = totalCount == 1;
                    break;
                case ReparseType.Image:
                    type = "images";
                    break;
                default:
                    return;
            }
            DisplayProgressStatus(watcher, "Reparsing", type, hideDetail, completeCount, totalCount);
        }

        private void SaveThreadList() {
            if (_isLoadingThreadsFromFile) return;
            try {
                // Prepare lines before writing file so that an exception can't result
                // in a partially written file.
                List<string> lines = new List<string>();
                lines.Add("4"); // File version
                foreach (ThreadWatcher watcher in ThreadWatchers) {
                    AddThreadLines(lines, watcher);
                }
                string path = Path.Combine(Settings.GetSettingsDirectory(), Settings.ThreadsFileName);
                File.WriteAllLines(path, lines.ToArray());
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
        }

        private static void AddThreadLines(List<string> lines, ThreadWatcher watcher) {
            WatcherExtraData extraData = (WatcherExtraData)watcher.Tag;
            lines.Add(watcher.PageURL);
            lines.Add(watcher.PageAuth);
            lines.Add(watcher.ImageAuth);
            lines.Add(watcher.CheckIntervalSeconds.ToString());
            lines.Add(watcher.OneTimeDownload ? "1" : "0");
            lines.Add(GetSaveDirLine(watcher));
            lines.Add(GetStopReasonLine(watcher));
            lines.Add(watcher.Description);
            lines.Add(extraData.AddedOn.ToUniversalTime().Ticks.ToString());
            lines.Add(GetLastImageOnLine(extraData));
            lines.Add(extraData.AddedFrom);
            lines.Add(watcher.Category);
            lines.Add(watcher.AutoFollow ? "1" : "0");
        }

        private static string GetSaveDirLine(ThreadWatcher watcher) {
            return watcher.ThreadDownloadDirectory != null ? General.GetRelativeDirectoryPath(watcher.ThreadDownloadDirectory, watcher.MainDownloadDirectory) : String.Empty;
        }

        private static string GetStopReasonLine(ThreadWatcher watcher) {
            return (watcher.IsStopping && watcher.StopReason != StopReason.Exiting) ? ((int)watcher.StopReason).ToString() : String.Empty;
        }

        private static string GetLastImageOnLine(WatcherExtraData extraData) {
            return extraData.LastImageOn != null ? extraData.LastImageOn.Value.ToUniversalTime().Ticks.ToString() : String.Empty;
        }

        private void LoadThreadList() {
            try {
                string[] lines = ReadThreadListLines();
                if (lines == null) return;
                int fileVersion = Int32.Parse(lines[0]);
                int linesPerThread = GetLinesPerThread(fileVersion);
                if (linesPerThread == 0) return;
                if (lines.Length < (1 + linesPerThread)) return;
                _isLoadingThreadsFromFile = true;
                Invoke(() => {
                    UpdateCategories(String.Empty);
                });
                AddThreadsFromLines(lines, fileVersion, linesPerThread);
                LinkLoadedThreadsToParents();
                MigrateChildThreadsToNewFormat();
                _isLoadingThreadsFromFile = false;
            }
            catch (Exception ex) {
                _isLoadingThreadsFromFile = false;
                Logger.Log(ex.ToString());
            }
        }

        // Returns null if the thread list file doesn't exist or is empty.
        private static string[] ReadThreadListLines() {
            string path = Path.Combine(Settings.GetSettingsDirectory(), Settings.ThreadsFileName);
            if (!File.Exists(path)) return null;
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 1) return null;
            return lines;
        }

        // Returns 0 for unsupported file versions.
        private static int GetLinesPerThread(int fileVersion) {
            switch (fileVersion) {
                case 1: return 6;
                case 2: return 7;
                case 3: return 10;
                case 4: return 13;
                default: return 0;
            }
        }

        private void AddThreadsFromLines(string[] lines, int fileVersion, int linesPerThread) {
            int i = 1;
            while (i <= lines.Length - linesPerThread) {
                ThreadInfo thread = ParseThreadInfo(lines, ref i, fileVersion);
                Invoke(() => {
                    AddThread(thread);
                });
            }
        }

        private static ThreadInfo ParseThreadInfo(string[] lines, ref int i, int fileVersion) {
            ThreadInfo thread = new ThreadInfo { ExtraData = new WatcherExtraData() };
            thread.URL = lines[i++];
            thread.PageAuth = lines[i++];
            thread.ImageAuth = lines[i++];
            thread.CheckIntervalSeconds = Int32.Parse(lines[i++]);
            thread.OneTimeDownload = lines[i++] == "1";
            thread.SaveDir = lines[i++];
            thread.SaveDir = thread.SaveDir.Length != 0 ? General.GetAbsoluteDirectoryPath(thread.SaveDir, Settings.AbsoluteDownloadDirectory) : null;
            if (fileVersion >= 2) {
                ParseStopReason(thread, lines[i++]);
            }
            if (fileVersion >= 3) {
                ParseDescriptionAndDates(thread, lines, ref i);
            }
            else {
                thread.Description = String.Empty;
                thread.ExtraData.AddedOn = DateTime.Now;
            }
            if (fileVersion >= 4) {
                thread.ExtraData.AddedFrom = lines[i++];
                thread.Category = lines[i++];
                thread.AutoFollow = lines[i++] == "1";
            }
            else {
                thread.ExtraData.AddedFrom = String.Empty;
                thread.Category = String.Empty;
            }
            return thread;
        }

        private static void ParseStopReason(ThreadInfo thread, string stopReasonLine) {
            if (stopReasonLine.Length != 0) {
                thread.StopReason = (StopReason)Int32.Parse(stopReasonLine);
            }
        }

        private static void ParseDescriptionAndDates(ThreadInfo thread, string[] lines, ref int i) {
            thread.Description = lines[i++];
            thread.ExtraData.AddedOn = new DateTime(Int64.Parse(lines[i++]), DateTimeKind.Utc).ToLocalTime();
            string lastImageOn = lines[i++];
            if (lastImageOn.Length != 0) {
                thread.ExtraData.LastImageOn = new DateTime(Int64.Parse(lastImageOn), DateTimeKind.Utc).ToLocalTime();
            }
        }

        private void LinkLoadedThreadsToParents() {
            foreach (ThreadWatcher threadWatcher in ThreadWatchers) {
                LinkToParentThread(threadWatcher);
                ThreadWatcher watcher = threadWatcher;
                Invoke(() => {
                    DisplayAddedFrom(watcher);
                });
                if (Settings.ChildThreadsAreNewFormat == true && IsRestartableAfterLoad(threadWatcher)) {
                    threadWatcher.Start();
                }
            }
        }

        private static void LinkToParentThread(ThreadWatcher threadWatcher) {
            ThreadWatcher parentThread;
            _watchers.TryGetValue(((WatcherExtraData)threadWatcher.Tag).AddedFrom, out parentThread);
            threadWatcher.ParentThread = parentThread;
            if (parentThread != null && !parentThread.ChildThreads.ContainsKey(threadWatcher.PageID) && !parentThread.ChildThreads.ContainsKey(parentThread.PageID)) {
                parentThread.ChildThreads.Add(threadWatcher.PageID, threadWatcher);
            }
        }

        private static bool IsRestartableAfterLoad(ThreadWatcher threadWatcher) {
            return threadWatcher.StopReason != StopReason.PageNotFound && threadWatcher.StopReason != StopReason.UserRequest;
        }

        private void MigrateChildThreadsToNewFormat() {
            if (Settings.ChildThreadsAreNewFormat == true) return;
            foreach (ThreadWatcher threadWatcher in ThreadWatchers) {
                if (threadWatcher.ChildThreads.Count == 0 || threadWatcher.ParentThread != null) continue;
                foreach (ThreadWatcher descendantThread in threadWatcher.DescendantThreads.Values) {
                    MoveDescendantThreadDirectory(threadWatcher, descendantThread);
                }
            }
            Settings.ChildThreadsAreNewFormat = true;
            Settings.Save();

            foreach (ThreadWatcher threadWatcher in ThreadWatchers) {
                if (IsRestartableAfterLoad(threadWatcher)) threadWatcher.Start();
            }
        }

        private static void MoveDescendantThreadDirectory(ThreadWatcher threadWatcher, ThreadWatcher descendantThread) {
            descendantThread.DoNotRename = true;
            string sourceDir = descendantThread.ThreadDownloadDirectory;
            string destDir = GetDescendantThreadDestDir(threadWatcher, descendantThread, sourceDir);
            if (String.Equals(destDir, sourceDir, StringComparison.Ordinal) || !Directory.Exists(sourceDir)) return;
            try {
                MoveThreadDirectory(sourceDir, destDir);
                descendantThread.ThreadDownloadDirectory = destDir;
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
            descendantThread.DoNotRename = false;
        }

        private static string GetDescendantThreadDestDir(ThreadWatcher threadWatcher, ThreadWatcher descendantThread, string sourceDir) {
            if (General.RemoveLastDirectory(sourceDir) == descendantThread.MainDownloadDirectory) {
                return Path.Combine(descendantThread.MainDownloadDirectory, General.RemoveLastDirectory(sourceDir));
            }
            return Path.Combine(General.RemoveLastDirectory(threadWatcher.ThreadDownloadDirectory),
                General.GetRelativeDirectoryPath(descendantThread.ThreadDownloadDirectory, threadWatcher.ThreadDownloadDirectory));
        }

        private static void MoveThreadDirectory(string sourceDir, string destDir) {
            if (String.Equals(destDir, sourceDir, StringComparison.OrdinalIgnoreCase)) {
                Directory.Move(sourceDir, destDir + " Temp");
                sourceDir = destDir + " Temp";
            }
            if (!Directory.Exists(General.RemoveLastDirectory(destDir))) Directory.CreateDirectory(General.RemoveLastDirectory(destDir));
            Directory.Move(sourceDir, destDir);
        }

        private void LoadBlacklist() {
            try {
                string path = Path.Combine(Settings.GetSettingsDirectory(), Settings.BlacklistFileName);
                if (!File.Exists(path)) return;
                string[] lines = File.ReadAllLines(path);
                if (lines.Length < 1) return;
                AddBlacklistRules(lines);
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
        }

        private static void AddBlacklistRules(string[] lines) {
            for (int i = 0; i < lines.Length; i++) {
                string rule = lines[i];
                if (rule.Split('/').Length == 3) {
                    _blacklist.Add(rule);
                }
            }
        }

        private void CheckForUpdates() {
            Thread thread = new Thread(CheckForUpdateThread);
            thread.IsBackground = true;
            thread.Start();
        }

        private void CheckForUpdateThread() {
            string html;
            try {
                html = General.DownloadPageToString(General.ProgramURL);
            }
            catch {
                return;
            }
            Settings.LastUpdateCheck = DateTime.Now.Date;
            string latestStr = ParseLatestVersionString(html);
            if (latestStr == null) return;
            int latest = General.ParseVersionNumber(latestStr);
            if (latest == -1) return;
            int current = GetCurrentVersionNumber();
            if (latest > current) {
                PromptForUpdate(latestStr);
            }
        }

        // Returns null if the latest release version can't be found in the page.
        private static string ParseLatestVersionString(string html) {
            var htmlParser = new HTMLParser(html);
            HTMLTagRange labelLatestDivTagRange = htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                htmlParser.FindStartTags("div"), t => HTMLParser.ClassAttributeValueHas(t, "label-latest"))));
            if (labelLatestDivTagRange == null) return null;
            HTMLTagRange versionSpanTagRange = htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                htmlParser.FindStartTags(labelLatestDivTagRange, "span"), t => HTMLParser.ClassAttributeValueHas(t, "css-truncate-target"))));
            if (versionSpanTagRange == null) return null;
            return htmlParser.GetInnerHTML(versionSpanTagRange).Replace("v", "");
        }

        private static int GetCurrentVersionNumber() {
            int current = General.ParseVersionNumber(General.Version);
            if (!String.IsNullOrEmpty(Settings.LatestUpdateVersion)) {
                current = Math.Max(current, General.ParseVersionNumber(Settings.LatestUpdateVersion));
            }
            return current;
        }

        private void PromptForUpdate(string latestStr) {
            lock (_startupPromptSync) {
                if (IsDisposed) return;
                Settings.LatestUpdateVersion = latestStr;
                Invoke(() => {
                    if (MessageBox.Show(this, "A newer version of Chan Thread Watch is available.  Would you like to open the Chan Thread Watch website?",
                        "Newer Version Found", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    {
                        Process.Start(General.ProgramURL);
                    }
                });
            }
        }

        private IAsyncResult BeginInvoke(MethodInvoker method) {
            return BeginInvoke((Delegate)method);
        }

        private object Invoke(MethodInvoker method) {
            return Invoke((Delegate)method);
        }

        private IEnumerable<ThreadWatcher> ThreadWatchers {
            get { return _watchers.Values; }
        }

        private IEnumerable<ThreadWatcher> SelectedThreadWatchers {
            get {
                foreach (ListViewItem item in lvThreads.SelectedItems) {
                    yield return (ThreadWatcher)item.Tag;
                }
            }
        }

        private enum ColumnIndex {
            Description = 0,
            Status = 1,
            LastImageOn = 2,
            AddedOn = 3,
            AddedFrom = 4,
            Category = 5
        }

        private void UpdateCategories(string key, bool remove = false) {
            key = key ?? String.Empty;
            bool hasKey = _categories.TryGetValue(key, out int count);
            int newCount = Math.Max(0, remove ? count - 1 : count + 1);
            _categories[key] = newCount;

            if (newCount == 0) {
                RemoveCategory(key);
            }
            else if (!hasKey) {
                cboCategory.Items.Add(key);
            }
        }

        // The empty category is kept even when unused.
        private void RemoveCategory(string key) {
            if (String.IsNullOrEmpty(key)) return;
            _categories.Remove(key);
            cboCategory.Items.Remove(key);
        }
        
        private void FocusThread(string pageURL) {
            SiteHelper siteHelper = SiteHelpers.GetInstance((new Uri(pageURL)).Host);
            siteHelper.SetURL(pageURL);
            ThreadWatcher watcher;
            if (_watchers.TryGetValue(siteHelper.GetPageID(), out watcher)) {
                FocusThread(watcher);
            }
        }

        private void FocusThread(ThreadWatcher watcher) {
            ListViewItem item = ((WatcherExtraData)watcher.Tag).ListViewItem;
            lvThreads.SelectedItems.Clear();
            lvThreads.Select();
            item.Selected = true;
            item.EnsureVisible();
        }

        private void FocusLastThread() {
            if (lvThreads.Items.Count > 0) {
                FocusThread((ThreadWatcher)lvThreads.Items[lvThreads.Items.Count - 1].Tag);
            }
        }

        private bool IsBlacklisted(string pageID) {
            if (_blacklist.Contains(pageID)) return true;
            if (Settings.BlacklistWildcards != true) return false;
            string[] pageIDSplit = pageID.Split('/');
            if (pageIDSplit.Length != 3) return false;
            foreach (string rule in _blacklist) {
                if (MatchesWildcardRule(rule, pageIDSplit)) return true;
            }
            return false;
        }

        private static bool MatchesWildcardRule(string rule, string[] pageIDSplit) {
            string[] ruleSplit = rule.Split('/');
            if (ruleSplit.Length != 3) return false;
            for (int i = 0; i < 3; i++) {
                if (!MatchesWildcardRulePart(ruleSplit[i], pageIDSplit[i])) return false;
            }
            return true;
        }

        private static bool MatchesWildcardRulePart(string rulePart, string pageIDPart) {
            return rulePart == "*" || rulePart == pageIDPart;
        }

        private MonitoringInfo GetMonitoringInfo() {
            int running = 0;
            int dead = 0;
            int stopped = 0;
            foreach (ThreadWatcher watcher in ThreadWatchers) {
                if (watcher.IsRunning || watcher.IsWaiting) {
                    running++;
                }
                else if (watcher.StopReason == StopReason.PageNotFound) {
                    dead++;
                }
                else {
                    stopped++;
                }
            }
            return new MonitoringInfo {
                TotalThreads = _watchers.Count,
                RunningThreads = running,
                DeadThreads = dead,
                StoppedThreads = stopped
            };
        }

        private void UpdateWindowTitle(MonitoringInfo monitoringInfo) {
            Text = (Settings.WindowTitle ?? String.Format("{{{0}}}", WindowTitleMacro.ApplicationName))
                .Replace(String.Format("{{{0}}}", WindowTitleMacro.ApplicationName), Settings.ApplicationName)
                .Replace(String.Format("{{{0}}}", WindowTitleMacro.TotalThreads), monitoringInfo.TotalThreads.ToString())
                .Replace(String.Format("{{{0}}}", WindowTitleMacro.RunningThreads), monitoringInfo.RunningThreads.ToString())
                .Replace(String.Format("{{{0}}}", WindowTitleMacro.DeadThreads), monitoringInfo.DeadThreads.ToString())
                .Replace(String.Format("{{{0}}}", WindowTitleMacro.StoppedThreads), monitoringInfo.StoppedThreads.ToString());
        }
    }
}