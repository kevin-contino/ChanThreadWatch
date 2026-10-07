using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using JDP.Api;

namespace JDP {
    // The Settings window's "Local API..." dialog (MP-7a): turns the local API on or off, sets its port and whether it
    // adds threads of sites without a site helper, shows its status, and makes a new token. OK saves the values that
    // changed at once and applies them without a restart (LocalApiHost.Apply), then waits a moment for the start: it
    // closes when the API listens or is off, and stays open with the reason when the start failed. Cancel keeps the
    // saved values. A new token is shown once, in a read-only box that is cleared when the dialog closes; only its hash
    // is saved (ApiTokenStore), and it is never logged or put in the settings.
    public partial class frmLocalApi : Form {
        private static readonly TimeSpan StartPollInterval = TimeSpan.FromMilliseconds(100);

        private readonly LocalApiHost _host;
        private LocalApiSettings _loaded;
        // How many tokens the dialog has shown, so the OK wait sees one shown after OK was pressed
        private int _tokensShown;

        internal frmLocalApi(LocalApiHost host) {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            InitializeComponent();
            GUI.SetFontAndScaling(this);
            _loaded = LocalApiSettings.Load();
            chkEnabled.Checked = _loaded.Enabled;
            txtPort.Text = _loaded.Port.ToString(CultureInfo.InvariantCulture);
            chkAllowUnknownHosts.Checked = _loaded.AllowUnknownHosts;
            ShowStatus();
        }

        private static ApiTokenStore TokenStore {
            get { return new ApiTokenStore(Settings.GetSettingsDirectory()); }
        }

        // A start runs in the background, so the status line follows it while the dialog is open
        private void ShowStatus() {
            lblStatus.Text = _host.Status.Text;
        }

        private void tmrStatus_Tick(object sender, EventArgs e) {
            ShowStatus();
        }

        // A port that is not valid keeps the dialog open, as the Settings window does for a folder it can't use. When
        // the API is on without a token, the dialog offers to make one; it then stays open, so the token can be copied.
        private async void btnOK_Click(object sender, EventArgs e) {
            int port;
            string portError = LocalApiSettings.ParsePort(txtPort.Text, out port);
            if (portError != null) {
                MessageBox.Show(this, portError, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                txtPort.Focus();
                txtPort.SelectAll();
                return;
            }
            LocalApiSettings edited = new LocalApiSettings { Enabled = chkEnabled.Checked, Port = port, AllowUnknownHosts = chkAllowUnknownHosts.Checked };
            SaveChanges(edited);
            bool tokenShown = edited.Enabled && OfferTokenIfMissing();
            _host.Apply(tokenShown);
            ShowStatus();
            if (!tokenShown) await CloseOnceStartedAsync();
        }

        // Never silent: a save that failed is said, and the values still apply until the program closes
        private void SaveChanges(LocalApiSettings edited) {
            LocalApiSaveResult result = edited.SaveChanges(_loaded);
            _loaded = edited;
            if (result != LocalApiSaveResult.NotSaved) return;
            MessageBox.Show(this, "The settings could not be saved (see " + Settings.LogFileName + "). The change applies now, but may be lost when the program closes.",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // Up to LocalApiOkWait.MaxWait, without blocking the UI thread. OK, "New token..." and the values are off
        // meanwhile, so no edit is lost and no token is shown that the close would take away; Cancel still closes.
        private async Task CloseOnceStartedAsync() {
            int tokensShownAtOk = _tokensShown;
            SetEditable(false);
            Stopwatch waited = Stopwatch.StartNew();
            LocalApiOkAction action;
            while ((action = LocalApiOkWait.Decide(_host.Status.State, waited.Elapsed, _tokensShown != tokensShownAtOk)) == LocalApiOkAction.Wait) {
                await Task.Delay(StartPollInterval);
                // Cancel closed the dialog meanwhile
                if (IsDisposed || !Visible) return;
            }
            ShowStatus();
            SetEditable(true);
            if (action == LocalApiOkAction.Close) DialogResult = DialogResult.OK;
        }

        private void SetEditable(bool editable) {
            btnOK.Enabled = editable;
            btnNewToken.Enabled = editable;
            chkEnabled.Enabled = editable;
            txtPort.Enabled = editable;
            chkAllowUnknownHosts.Enabled = editable;
        }

        // True when a token was made (and is shown)
        private bool OfferTokenIfMissing() {
            if (TokenStore.IsConfigured()) return false;
            if (MessageBox.Show(this, "The local API needs a token to start, and there is none yet. Make one now?", "Local API",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
            return MakeToken();
        }

        // A server that is starting or failed is started again with the new token
        private void btnNewToken_Click(object sender, EventArgs e) {
            if (MessageBox.Show(this, "Make a new token? The current token, if any, stops working at once, also for scripts that use it now.", "New Token",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            if (MakeToken()) _host.Apply(true);
        }

        private bool MakeToken() {
            string token;
            try {
                token = TokenStore.Generate();
            }
            catch (ApiTokenException ex) {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            ShowToken(token);
            return true;
        }

        private void ShowToken(string token) {
            _tokensShown++;
            txtToken.Text = token;
            txtToken.Visible = true;
            btnCopyToken.Visible = true;
            lblTokenNote.Visible = true;
            txtToken.Focus();
            txtToken.SelectAll();
        }

        private void btnCopyToken_Click(object sender, EventArgs e) {
            try {
                Clipboard.SetDataObject(CreateTokenData(txtToken.Text), true);
            }
            catch (Exception ex) {
                MessageBox.Show(this, "Unable to copy to clipboard: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // The token as text, marked so that Windows keeps it out of the clipboard history and the cloud clipboard, and
        // so that clipboard tools that honor the mark do not record it
        internal static DataObject CreateTokenData(string token) {
            DataObject data = new DataObject();
            data.SetText(token, TextDataFormat.UnicodeText);
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 0 }));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
            return data;
        }

        // The token is shown only while the dialog is open
        private void frmLocalApi_FormClosed(object sender, FormClosedEventArgs e) {
            tmrStatus.Stop();
            txtToken.Clear();
        }
    }
}
