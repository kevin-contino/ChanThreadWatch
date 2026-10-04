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
        private int _itemAreaY;
        private int[] _columnWidths;
        private object _cboCheckEveryLastValue;
        private readonly WatchSession _session;
        private static Dictionary<string, int> _categories = new Dictionary<string, int>();
        // Each watcher's row in lvThreads; only touched on the UI thread
        private static Dictionary<ThreadWatcher, ListViewItem> _listViewItems = new Dictionary<ThreadWatcher, ListViewItem>();

        // ReleaseDate property and version in AssemblyInfo.cs should be updated for each release.

        public frmChanThreadWatch() {
            // Created before InitializeComponent so control events raised during it never see a null session.
            // MethodInvoker keeps an exception thrown on the UI thread unwrapped for the caller.
            _session = new WatchSession(a => Invoke(new MethodInvoker(a)), a => BeginInvoke(new MethodInvoker(a)));
            _session.ThreadWatcherCreated += Session_ThreadWatcherCreated;
            _session.ThreadWatcherAdded += DisplayData;
            _session.ThreadListLoadStarting += () => UpdateCategories(String.Empty);
            _session.AddedFromChanged += DisplayAddedFrom;
            _session.ThreadWatcherRemoved += Session_ThreadWatcherRemoved;
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

            WatchSession.EnsureDownloadFolderExists();
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
                try { File.Create(logPath).Dispose(); }
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
                _session.LoadThreadList();
                _session.LoadBlacklist();

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
            SaveExitSettings();

            Settings.Save();

            foreach (ThreadWatcher watcher in _session.ThreadWatchers) {
                watcher.Stop(StopReason.Exiting);
            }

            // Save before waiting in addition to after in case the wait hangs or is interrupted
            _session.SaveThreadList();

            _isExiting = true;
            WaitForThreadWatchersToStop();

            _session.SaveThreadList();

            Program.ReleaseMutex();
        }

        // A failure here must not skip the settings and thread list saves that follow on exit.
        private void SaveExitSettings() {
            try {
                SaveControlSettings();
                SaveColumnSettings();
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
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
            foreach (ThreadWatcher watcher in _session.ThreadWatchers) {
                while (!watcher.WaitUntilStopped(10) || !watcher.WaitReparse(10)) {
                    Application.DoEvents();
                }
            }
        }

        // Falls back to the saved setting (or 3 minutes) instead of throwing when neither
        // control holds a usable value.
        private int GetCheckEveryMinutes() {
            object selectedValue = cboCheckEvery.Enabled ? cboCheckEvery.SelectedValue : null;
            if (selectedValue != null) return (int)selectedValue;
            int minutes;
            return Int32.TryParse(txtCheckEvery.Text, out minutes) ? minutes : (Settings.CheckEvery ?? 3);
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
                // The system ANSI code page, which Encoding.Default was on .NET Framework (it is UTF-8 on .NET 10)
                url = Encoding.GetEncoding(0).GetString(data, 0, General.StrLen(data));
            }
            url = General.CleanPageURL(url);
            if (url != null) {
                AddThread(url);
                FocusThread(url);
                _session.SaveThreadListPending = true;
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
            _session.SaveThreadListPending = true;
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
            _session.SaveThreadListPending = true;
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

        private void SelectThread(string pageURL) {
            SiteHelper siteHelper = SiteHelpers.GetInstance((new Uri(pageURL)).Host);
            siteHelper.SetURL(pageURL);
            ThreadWatcher watcher;
            if (_session.TryGetThreadWatcher(siteHelper.GetPageID(), out watcher)) {
                _listViewItems[watcher].Selected = true;
            }
        }

        private void btnRemoveCompleted_Click(object sender, EventArgs e) {
            _session.RemoveCompletedThreads(ListedThreadWatchers);
        }

        private void miStop_Click(object sender, EventArgs e) {
            foreach (ThreadWatcher watcher in SelectedThreadWatchers) {
                watcher.Stop(StopReason.UserRequest);
            }
            _session.SaveThreadListPending = true;
        }

        private void miStart_Click(object sender, EventArgs e) {
            if (_isExiting) return;
            foreach (ThreadWatcher watcher in SelectedThreadWatchers) {
                if (!watcher.IsRunning && !watcher.IsReparsing) {
                    watcher.Start();
                }
            }
            _session.SaveThreadListPending = true;
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
                    _session.SaveThreadListPending = true;
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
            // A login set here, even an empty one, replaces a saved login that couldn't be decrypted
            if (editForm.PageAuth.IsDirty) {
                watcher.PageAuth = editForm.PageAuth.Value;
                ((WatcherExtraData)watcher.Tag).UndecryptablePageAuth = null;
            }
            if (editForm.ImageAuth.IsDirty) {
                watcher.ImageAuth = editForm.ImageAuth.Value;
                ((WatcherExtraData)watcher.Tag).UndecryptableImageAuth = null;
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
                            Shell.Open(dir);
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
                        Shell.Open(url);
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
            RemoveSelectedThreads();
        }

        private void miRemoveAndDeleteFolder_Click(object sender, EventArgs e) {
            if (MessageBox.Show(this, "Are you sure you want to delete the selected threads and all associated files from disk?",
                "Delete From Disk", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }
            RemoveSelectedThreads(WatchSession.DeleteThreadFolder);
        }

        private void miBlacklist_Click(object sender, EventArgs e) {
            if (_isExiting) return;
            _session.AddToBlacklist(SelectedThreadWatchers);
        }

        private void miCheckNow_Click(object sender, EventArgs e) {
            foreach (ThreadWatcher watcher in SelectedThreadWatchers) {
                watcher.MillisecondsUntilNextCheck = 0;
            }
        }

        private void miCheckEvery_Click(object sender, EventArgs e) {
            ToolStripMenuItem menuItem = sender as ToolStripMenuItem;
            if (menuItem != null) {
                int checkIntervalSeconds = Convert.ToInt32(menuItem.Tag) * 60;
                foreach (ThreadWatcher watcher in SelectedThreadWatchers) {
                    watcher.CheckIntervalSeconds = checkIntervalSeconds;
                }
                UpdateWaitingWatcherStatuses();
            }
            _session.SaveThreadListPending = true;
        }

        private void miReparse_Click(object sender, EventArgs e) {
            if (_isExiting) return;
            foreach (ThreadWatcher watcher in SelectedThreadWatchers) {
                if (!watcher.IsRunning && !watcher.IsReparsing && Settings.SaveThumbnails != false) {
                    watcher.BeginReparse();
                }
            }
            _session.SaveThreadListPending = true;
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
            UpdateWindowTitle(_session.GetMonitoringInfo());
        }

        private void btnAbout_Click(object sender, EventArgs e) {
            MessageBox.Show(this, String.Format("Chan Thread Watch{0}Version {1} ({2}){0}{0}Original Author: JDP (jart1126@yahoo.com){0}http://sites.google.com/site/chanthreadwatch/" +
                                                "{0}{0}Previously maintained by: SuperGouge (https://github.com/SuperGouge)" +
                                                "{0}Maintained by: kevin-contino (https://github.com/kevin-contino){0}{3}",
                Environment.NewLine, General.Version, General.ReleaseDate, General.ProgramURL), "About",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        
        private void btnHelp_Click(object sender, EventArgs e) {
            Shell.Open(General.WikiURL);
        }

        private void lvThreads_KeyDown(object sender, KeyEventArgs e) {
            if (e.KeyCode == Keys.Delete) {
                RemoveSelectedThreads();
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
            miStop.Available = anyRunning;
            miStart.Available = anyStoppedAndNotReparsing;
            miCheckNow.Available = anyRunning;
            miCheckEvery.Available = anyRunning;
            miRemove.Available = anyStoppedAndNotReparsing;
            miRemoveAndDeleteFolder.Available = anyStoppedAndNotReparsing;
            miReparse.Available = anyStoppedAndNotReparsing;
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
            if (_session.SaveThreadListPending && !_isExiting) {
                _session.SaveThreadList();
                _session.SaveThreadListPending = false;
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
            MonitoringInfo monitoringInfo = _session.GetMonitoringInfo();
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

        private void ThreadWatcher_DownloadStatus(object sender, DownloadStatusEventArgs args) {
            ThreadWatcher watcher = (ThreadWatcher)sender;
            WatcherExtraData extraData = (WatcherExtraData)watcher.Tag;
            bool isInitialPageDownload = WatchSession.TrackPageDownload(extraData, args.DownloadType);
            bool isFirstImageUpdate = WatchSession.TrackImageDownload(extraData, args.DownloadType);
            BeginInvoke(() => {
                SetDownloadStatus(watcher, args.DownloadType, args.CompleteCount, args.TotalCount);
                if (isInitialPageDownload) {
                    DisplayDescription(watcher);
                    _session.SaveThreadListPending = true;
                }
                if (isFirstImageUpdate) {
                    DisplayLastImageOn(watcher);
                    _session.SaveThreadListPending = true;
                }
                SetupWaitTimer();
            });
        }

        private void ThreadWatcher_WaitStatus(object sender, EventArgs args) {
            ThreadWatcher watcher = (ThreadWatcher)sender;
            BeginInvoke(() => {
                SetWaitStatus(watcher);
                SetupWaitTimer();
            });
        }

        private void ThreadWatcher_StopStatus(object sender, StopStatusEventArgs args) {
            ThreadWatcher watcher = (ThreadWatcher)sender;
            // Read on the watcher's thread, before a restart could reset them
            string stopError = watcher.StopError;
            int failedFileCount = watcher.FailedFileCount;
            string reparseError = watcher.ReparseError;
            BeginInvoke(() => {
                DisplayStatus(watcher, WatcherStatusText.AppendReparseError(WatcherStatusText.FormatStopStatus(args.StopReason, stopError, failedFileCount), reparseError));
                SetupWaitTimer();
                if (args.StopReason != StopReason.UserRequest && args.StopReason != StopReason.Exiting) {
                    _session.SaveThreadListPending = true;
                }
            });
        }

        private void ThreadWatcher_ReparseStatus(object sender, ReparseStatusEventArgs args) {
            ThreadWatcher watcher = (ThreadWatcher)sender;
            BeginInvoke(() => {
                SetReparseStatus(watcher, args.ReparseType, args.CompleteCount, args.TotalCount);
                SetupWaitTimer();
            });
        }

        private void ThreadWatcher_ThreadDownloadDirectoryRename(object sender, EventArgs args) {
            BeginInvoke(() => {
                _session.SaveThreadListPending = true;
            });
        }

        private void ThreadWatcher_DownloadStart(object sender, DownloadStartEventArgs args) {
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

        private void ThreadWatcher_DownloadProgress(object sender, DownloadProgressEventArgs args) {
            lock (_downloadProgresses) {
                DownloadProgressInfo info;
                if (!_downloadProgresses.TryGetValue(args.DownloadID, out info)) return;
                info.DownloadedSize = args.DownloadedSize;
                _downloadProgresses[args.DownloadID] = info;
            }
        }

        private void ThreadWatcher_DownloadEnd(object sender, DownloadEndEventArgs args) {
            lock (_downloadProgresses) {
                DownloadProgressInfo info;
                if (!_downloadProgresses.TryGetValue(args.DownloadID, out info)) return;
                info.EndTicks = TickCount.Now;
                info.DownloadedSize = args.DownloadedSize;
                info.TotalSize = args.DownloadedSize;
                _downloadProgresses[args.DownloadID] = info;
            }
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
            return _session.AddThread(thread);
        }

        private static string GetAuthText(CheckBox chkAuth, TextBox txtAuth) {
            return (chkAuth.Checked && (txtAuth.Text.IndexOf(':') != -1)) ? txtAuth.Text : String.Empty;
        }

        // The session created a watcher; it is not registered yet
        private void Session_ThreadWatcherCreated(ThreadWatcher watcher) {
            SubscribeThreadWatcherEvents(watcher);
            AddThreadListViewItem(watcher);
            UpdateCategories(watcher.Category);
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
        }

        private void AddThreadListViewItem(ThreadWatcher watcher) {
            ListViewItem newListViewItem = new ListViewItem(String.Empty);
            for (int i = 1; i < lvThreads.Columns.Count; i++) {
                newListViewItem.SubItems.Add(String.Empty);
            }
            newListViewItem.Tag = watcher;
            lvThreads.Items.Add(newListViewItem);
            _listViewItems[watcher] = newListViewItem;
            lvThreads.Sort();
        }

        // Removes the selected threads, in list order (SelectedItems enumerates by index)
        private void RemoveSelectedThreads(Action<ThreadWatcher> preRemoveAction = null) {
            _session.RemoveThreads(new List<ThreadWatcher>(SelectedThreadWatchers), preRemoveAction);
        }

        // The session is removing the watcher, after its pre-remove action and before unregistering it
        private void Session_ThreadWatcherRemoved(ThreadWatcher watcher) {
            UpdateCategories(watcher.Category, true);
            lvThreads.Items.Remove(_listViewItems[watcher]);
            _listViewItems.Remove(watcher);
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
                ToolStripMenuItem menuItem = new ToolStripMenuItem {
                    Tag = minutes,
                    Text = minutes > 0 ? minutes + " Minutes" : "1 Minute or <"
                };
                menuItem.Click += miCheckEvery_Click;
                miCheckEvery.DropDownItems.Add(menuItem);
            }
        }

        private void BuildColumnHeaderMenu() {
            ContextMenuStrip contextMenu = new ContextMenuStrip { RenderMode = ToolStripRenderMode.System };
            ToolStripMenuItem[] columnItems = new ToolStripMenuItem[lvThreads.Columns.Count];
            contextMenu.Opening += (s, e) => {
                for (int i = 0; i < columnItems.Length; i++) {
                    columnItems[i].Checked = lvThreads.Columns[i].Width != 0;
                }
            };
            for (int i = 0; i < columnItems.Length; i++) {
                ToolStripMenuItem menuItem = new ToolStripMenuItem {
                    Tag = i,
                    Text = lvThreads.Columns[i].Text
                };
                menuItem.Click += (s, e) => {
                    int iColumn = (int)((ToolStripMenuItem)s).Tag;
                    ColumnHeader column = lvThreads.Columns[iColumn];
                    if (column.Width != 0) {
                        _columnWidths[iColumn] = column.Width;
                        column.Width = 0;
                    }
                    else {
                        column.Width = _columnWidths[iColumn];
                    }
                };
                columnItems[i] = menuItem;
            }
            contextMenu.Items.AddRange(columnItems);
            ContextMenuStrip contextMenuStrip = new ContextMenuStrip { RenderMode = ToolStripRenderMode.System };
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
            foreach (ThreadWatcher watcher in _session.ThreadWatchers) {
                if (watcher.IsWaiting) return true;
            }
            return false;
        }

        private void UpdateWaitingWatcherStatuses() {
            foreach (ThreadWatcher watcher in _session.ThreadWatchers) {
                if (watcher.IsWaiting) {
                    SetWaitStatus(watcher);
                }
            }
        }

        private void SetSubItemText(ThreadWatcher watcher, ColumnIndex columnIndex, string text) {
            ListViewItem item;
            // A status callback queued before the thread was removed has no row to update
            if (!_listViewItems.TryGetValue(watcher, out item)) return;
            var subItem = item.SubItems[(int)columnIndex];
            if (subItem.Text != text) {
                subItem.Text = text;
                if (!_session.IsLoadingThreadsFromFile) lvThreads.Sort();
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
            _session.TryGetThreadWatcher(((WatcherExtraData)watcher.Tag).AddedFrom ?? String.Empty, out fromWatcher);
            SetSubItemText(watcher, ColumnIndex.AddedFrom, fromWatcher != null ? fromWatcher.Description : String.Empty);
        }

        private void DisplayCategory(ThreadWatcher watcher) {
            SetSubItemText(watcher, ColumnIndex.Category, watcher.Category);
        }

        private void DisplayData(ThreadWatcher watcher) {
            DisplayDescription(watcher);
            DisplayAddedOn(watcher);
            DisplayLastImageOn(watcher);
            if (!_session.IsLoadingThreadsFromFile) DisplayAddedFrom(watcher);
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
            DisplayStatus(watcher, WatcherStatusText.FormatWaitStatus(remainingSeconds, watcher.CheckError, watcher.FailedFileCount, watcher.RateLimitPausedHost, watcher.RateLimitResumeTime));
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

        private void CheckForUpdates() {
            Thread thread = new Thread(CheckForUpdateThread);
            thread.IsBackground = true;
            thread.Start();
        }

        private void CheckForUpdateThread() {
            string json;
            try {
                json = General.DownloadPageToString(GetLatestReleaseAPIURL());
            }
            catch {
                return;
            }
            string latestStr = General.NormalizeUpdateVersion(General.ParseReleaseTagName(json), General.Version);
            if (latestStr == null) return;
            Settings.LastUpdateCheck = DateTime.Now.Date;
            if (General.ParseVersionNumber(latestStr) > GetCurrentVersionNumber()) {
                PromptForUpdate(latestStr);
            }
        }

        // Test seam: UI tests point the update check at a loopback server through this variable. Any
        // other value is ignored, so it cannot send the check anywhere but this computer.
        internal const string TestUpdateURLVariable = "CTW_TEST_UPDATE_URL";

        internal static string GetLatestReleaseAPIURL() {
            string testURL = Environment.GetEnvironmentVariable(TestUpdateURLVariable);
            return Uri.TryCreate(testURL, UriKind.Absolute, out Uri uri) && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback
                ? testURL
                : General.LatestReleaseAPIURL;
        }

        private static int GetCurrentVersionNumber() {
            int current = General.ParseVersionNumber(General.Version);
            // Ignore a stored version that is not plausible (e.g. a forged tag saved by an older build)
            string latestKnown = General.NormalizeUpdateVersion(Settings.LatestUpdateVersion, General.Version);
            if (latestKnown != null) {
                current = Math.Max(current, General.ParseVersionNumber(latestKnown));
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
                        Shell.Open(General.ProgramURL);
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

        private IEnumerable<ThreadWatcher> SelectedThreadWatchers {
            get {
                foreach (ListViewItem item in lvThreads.SelectedItems) {
                    yield return (ThreadWatcher)item.Tag;
                }
            }
        }

        // Every row's watcher, in list order; a copy, so rows can be removed while it is enumerated
        private List<ThreadWatcher> ListedThreadWatchers {
            get {
                List<ThreadWatcher> watchers = new List<ThreadWatcher>();
                foreach (ListViewItem item in lvThreads.Items) {
                    watchers.Add((ThreadWatcher)item.Tag);
                }
                return watchers;
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
            if (_session.TryGetThreadWatcher(siteHelper.GetPageID(), out watcher)) {
                FocusThread(watcher);
            }
        }

        private void FocusThread(ThreadWatcher watcher) {
            ListViewItem item = _listViewItems[watcher];
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