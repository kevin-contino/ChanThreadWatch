using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;

namespace JDP {
    public sealed partial class frmThreadEdit : Form {
        private object _cboCheckEveryLastValue;
        private readonly IList<ThreadWatcher> _watchers;

        private static readonly int[] _checkEveryPresetMinutes = { 0, 2, 3, 5, 10, 60 };

        public frmThreadEdit(IList<ThreadWatcher> watchers, Dictionary<string, int> categories) {
            InitializeComponent();
            GUI.SetFontAndScaling(this);

            _watchers = watchers;

            if (watchers.Count > 1) {
                Text = $"Edit {watchers.Count} threads";
            }

            foreach (string key in categories.Keys) {
                cboCategory.Items.Add(key);
            }

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

            bool anyRunning = LoadWatchers(watchers);

            if (anyRunning) {
                DisableRunningThreadControls();
            }

            Description = new EditField<string>(() => txtDescription.Text.Trim(), txtDescription);
            Category = new EditField<string>(() => cboCategory.Text.Trim(), cboCategory);
            PageAuth = new EditField<string>(() => GetAuthValue(chkPageAuth, txtPageAuth), chkPageAuth, txtPageAuth);
            ImageAuth = new EditField<string>(() => GetAuthValue(chkImageAuth, txtImageAuth), chkImageAuth, txtImageAuth);
            OneTimeDownload = new EditField<bool>(() => chkOneTime.Checked, chkOneTime);
            AutoFollow = new EditField<bool>(() => chkAutoFollow.Checked, chkAutoFollow);
            CheckIntervalSeconds = new EditField<int>(() => GetCheckEverySeconds(), cboCheckEvery, txtCheckEvery);
        }

        // Loads the first watcher's values into the controls, then blanks out each
        // control whose value differs in any of the other watchers. Returns whether
        // any watcher is running.
        private bool LoadWatchers(IList<ThreadWatcher> watchers) {
            bool anyRunning = false;
            bool multiDescription = false;
            bool multiCategory = false;
            bool multiPageAuth = false;
            bool multiImageAuth = false;
            bool multiOneTime = false;
            bool multiAutoFollow = false;
            bool multiCheckEvery = false;

            for (int i = 0; i < watchers.Count; i++) {
                var watcher = watchers[i];
                if (i == 0) {
                    LoadFirstWatcher(watcher);
                }
                else {
                    MergeDescription(watcher, ref multiDescription);
                    MergeCategory(watcher, ref multiCategory);
                    MergePageAuth(watcher, ref multiPageAuth);
                    MergeImageAuth(watcher, ref multiImageAuth);
                    MergeOneTime(watcher, ref multiOneTime);
                    MergeAutoFollow(watcher, ref multiAutoFollow);
                    MergeCheckEvery(watcher, ref multiCheckEvery);
                }

                anyRunning |= watcher.IsRunning;
            }

            return anyRunning;
        }

        private void LoadFirstWatcher(ThreadWatcher watcher) {
            txtDescription.Text = watcher.Description;
            cboCategory.Text = watcher.Category;

            chkPageAuth.Checked = !String.IsNullOrEmpty(watcher.PageAuth);
            txtPageAuth.Text = watcher.PageAuth;
            chkPageAuth.Checked = !String.IsNullOrEmpty(watcher.ImageAuth);
            txtImageAuth.Text = watcher.ImageAuth;

            chkOneTime.Checked = watcher.OneTimeDownload;
            chkAutoFollow.Checked = watcher.AutoFollow;

            LoadCheckEvery(watcher.CheckIntervalSeconds / 60);
        }

        private void LoadCheckEvery(int checkIntervalMinutes) {
            if (checkIntervalMinutes == 1) {
                cboCheckEvery.SelectedValue = 0;
            }
            else if (Array.IndexOf(_checkEveryPresetMinutes, checkIntervalMinutes) != -1) {
                cboCheckEvery.SelectedValue = checkIntervalMinutes;
            }
            else {
                txtCheckEvery.Text = checkIntervalMinutes.ToString(CultureInfo.InvariantCulture);
            }
        }

        private void MergeDescription(ThreadWatcher watcher, ref bool isMulti) {
            if (isMulti || txtDescription.Text == watcher.Description) return;
            txtDescription.Text = String.Empty;
            isMulti = true;
        }

        private void MergeCategory(ThreadWatcher watcher, ref bool isMulti) {
            if (isMulti || cboCategory.Text == watcher.Category) return;
            cboCategory.Text = String.Empty;
            isMulti = true;
        }

        private void MergePageAuth(ThreadWatcher watcher, ref bool isMulti) {
            if (isMulti || AuthMatches(chkPageAuth, txtPageAuth, watcher.PageAuth)) return;
            chkPageAuth.CheckState = CheckState.Indeterminate;
            txtPageAuth.Text = String.Empty;
            txtPageAuth.Enabled = false;
            isMulti = true;
        }

        private void MergeImageAuth(ThreadWatcher watcher, ref bool isMulti) {
            if (isMulti || AuthMatches(chkImageAuth, txtImageAuth, watcher.ImageAuth)) return;
            chkImageAuth.CheckState = CheckState.Indeterminate;
            txtImageAuth.Text = String.Empty;
            txtImageAuth.Enabled = false;
            isMulti = true;
        }

        private void MergeOneTime(ThreadWatcher watcher, ref bool isMulti) {
            if (isMulti || chkOneTime.Checked == watcher.OneTimeDownload) return;
            chkOneTime.CheckState = CheckState.Indeterminate;
            isMulti = true;
        }

        private void MergeAutoFollow(ThreadWatcher watcher, ref bool isMulti) {
            if (isMulti || chkAutoFollow.Checked == watcher.AutoFollow) return;
            chkAutoFollow.CheckState = CheckState.Indeterminate;
            isMulti = true;
        }

        private void MergeCheckEvery(ThreadWatcher watcher, ref bool isMulti) {
            if (isMulti || GetCheckEverySeconds() == watcher.CheckIntervalSeconds) return;
            txtCheckEvery.Text = String.Empty;
            cboCheckEvery.SelectedIndex = -1;
            cboCheckEvery.Enabled = true;
            _cboCheckEveryLastValue = 0;
            isMulti = true;
        }

        private static bool AuthMatches(CheckBox chk, TextBox txt, string auth) {
            return chk.Checked == !String.IsNullOrEmpty(auth) && txt.Text == auth;
        }

        private static string GetAuthValue(CheckBox chk, TextBox txt) {
            if (chk.Checked && txt.Text.IndexOf(':') != -1) {
                return txt.Text;
            }
            return String.Empty;
        }

        private int GetCheckEverySeconds() {
            if (!pnlCheckEvery.Enabled) return 0;
            if (!cboCheckEvery.Enabled) return Int32.Parse(txtCheckEvery.Text) * 60;
            return (int)cboCheckEvery.SelectedValue * 60;
        }

        private void DisableRunningThreadControls() {
            chkPageAuth.Enabled = false;
            txtPageAuth.Enabled = false;
            chkImageAuth.Enabled = false;
            txtImageAuth.Enabled = false;
            chkOneTime.Enabled = false;
            chkAutoFollow.Enabled = false;
        }

        public EditField<string> Description { get; }

        public EditField<string> Category { get; }

        public EditField<string> PageAuth { get; }

        public EditField<string> ImageAuth { get; }

        public EditField<bool> OneTimeDownload { get; }

        public EditField<bool> AutoFollow { get; }

        public EditField<int> CheckIntervalSeconds { get; }

        public bool IsDirty => Description.IsDirty
                               || Category.IsDirty
                               || PageAuth.IsDirty
                               || ImageAuth.IsDirty
                               || OneTimeDownload.IsDirty
                               || AutoFollow.IsDirty
                               || CheckIntervalSeconds.IsDirty;

        private void btnOK_Click(object sender, EventArgs e) {
            if (!ConfirmMultiThreadChanges()) {
                return;
            }

            if (Description.IsDirty && Description.Value.Length == 0) {
                MessageBox.Show(this, "Description cannot be empty.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            DialogResult = DialogResult.OK;
        }

        // Returns false if the user cancels applying the changes to multiple threads.
        private bool ConfirmMultiThreadChanges() {
            if (_watchers.Count <= 1 || !IsDirty) return true;
            var result = MessageBox.Show(this, $"Changes made will be applied to {_watchers.Count} selected threads.", "Confirm changes", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            return result != DialogResult.Cancel;
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
            _cboCheckEveryLastValue = cboCheckEvery.SelectedValue;
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
    }
}