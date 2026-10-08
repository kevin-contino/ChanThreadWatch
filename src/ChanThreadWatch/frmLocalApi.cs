using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using JDP.Api;

namespace JDP {
    // The Settings window's "Local API..." dialog (MP-7a): turns the local API on or off, sets its port and whether it
    // adds threads of sites without a site helper, shows its status, makes a new token, and pairs browser extensions.
    // OK saves the values that changed at once and applies them without a restart (LocalApiHost.Apply), then waits a
    // moment for the start: it closes when the API listens or is off, and stays open with the reason when the start
    // failed. Cancel keeps the saved values. A new token is shown once, in a read-only box that is cleared when the
    // dialog closes; only its hash is saved (ApiTokenStore), and it is never logged or put in the settings.
    //
    // The "Browser extension" group shows the paired browsers (api-clients.txt) with an Unpair button each, and pairs
    // one: "Pair extension..." (only while the API listens) makes a code off the UI thread, shows it with a countdown,
    // follows api-pairing.txt on the status timer (LocalApiPairing) and shows how the code ended. The code is never
    // copied, logged or saved; closing the dialog, or the API stopping, ends a code that is still pending.
    public partial class frmLocalApi : Form {
        private static readonly TimeSpan StartPollInterval = TimeSpan.FromMilliseconds(100);

        private readonly LocalApiHost _host;
        private LocalApiSettings _loaded;
        // How many tokens the dialog has shown, so the OK wait sees one shown after OK was pressed
        private int _tokensShown;
        // False while the OK wait runs
        private bool _editable = true;
        // The code being made on the thread pool, then the code being followed; null when there is none
        private LocalApiPairing _making;
        private LocalApiPairing _pairing;
        // The paired browsers as the rows last showed them, so Unpair acts on the pairing the user saw
        private IReadOnlyList<ApiClient> _shownClients;

        internal frmLocalApi(LocalApiHost host) {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            InitializeComponent();
            GUI.SetFontAndScaling(this);
            _loaded = LocalApiSettings.Load();
            chkEnabled.Checked = _loaded.Enabled;
            txtPort.Text = _loaded.Port.ToString(CultureInfo.InvariantCulture);
            chkAllowUnknownHosts.Checked = _loaded.AllowUnknownHosts;
            ShowStatus();
            ShowPairedBrowsers();
        }

        private static ApiTokenStore TokenStore {
            get { return new ApiTokenStore(Settings.GetSettingsDirectory()); }
        }

        private static ApiClientStore ClientStore {
            get { return new ApiClientStore(Settings.GetSettingsDirectory()); }
        }

        // A start runs in the background, so the status line follows it while the dialog is open
        private void ShowStatus() {
            lblStatus.Text = _host.Status.Text;
            btnPair.Enabled = _editable && _making == null && _host.Status.State == LocalApiState.Listening;
        }

        // The paired browsers are read again too, so a pairing or unpair by another program (ctw api-pair) shows
        private void tmrStatus_Tick(object sender, EventArgs e) {
            ShowStatus();
            FollowPairing();
            StopPairingIfTheApiStopped();
            ShowPairedBrowsers();
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

        // A new code is not made meanwhile either: the close would end it
        private void SetEditable(bool editable) {
            _editable = editable;
            btnOK.Enabled = editable;
            btnNewToken.Enabled = editable;
            chkEnabled.Enabled = editable;
            txtPort.Enabled = editable;
            chkAllowUnknownHosts.Enabled = editable;
            ShowStatus();
            ShowPairedBrowsers();
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

        // A row for each browser; Unpair only for one that is paired. Labels are set only when their text changes, since
        // this runs on each tick.
        private void ShowPairedBrowsers() {
            IReadOnlyList<ApiClient> clients = ClientStore.Read();
            _shownClients = clients;
            SetText(lblChrome, LocalApiPairing.DescribeClient(clients, ApiPairing.ChromeFamily, TimeZoneInfo.Local));
            SetText(lblFirefox, LocalApiPairing.DescribeClient(clients, ApiPairing.FirefoxFamily, TimeZoneInfo.Local));
            btnUnpairChrome.Enabled = _editable && LocalApiPairing.FindClient(clients, ApiPairing.ChromeFamily) != null;
            btnUnpairFirefox.Enabled = _editable && LocalApiPairing.FindClient(clients, ApiPairing.FirefoxFamily) != null;
            lblClientsNote.Visible = clients == null;
        }

        private static void SetText(Label label, string text) {
            if (label.Text != text) label.Text = text;
        }

        private void btnUnpairChrome_Click(object sender, EventArgs e) {
            Unpair(ApiPairing.ChromeFamily);
        }

        private void btnUnpairFirefox_Click(object sender, EventArgs e) {
            Unpair(ApiPairing.FirefoxFamily);
        }

        // The browser's token stops working at once, also in a ctw watch that runs. Only the pairing the user saw is
        // removed: the browser may pair again before the user confirms, and its new line stays.
        private void Unpair(string family) {
            ApiClient shown = LocalApiPairing.FindClient(_shownClients, family);
            if (shown == null) {
                ShowPairedBrowsers();
                return;
            }
            if (MessageBox.Show(this, "Unpair the " + ApiPairingFollow.DisplayName(family) + " extension? Its token stops working at once.", "Unpair Extension",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try {
                if (!ClientStore.Remove(family, shown.Hash)) {
                    MessageBox.Show(this, LocalApiPairing.DescribeUnpairFailure(ClientStore.Read(), family), "Unpair Extension", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (ApiTokenException ex) {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            ShowPairedBrowsers();
        }

        // A code that is still pending is ended first (a new code would replace it anyway). The code is made on the
        // thread pool; the dialog may close meanwhile, which ends it as soon as it is made. Any failure ends the code and
        // is shown in place of it.
        private async void btnPair_Click(object sender, EventArgs e) {
            EndPairing();
            LocalApiPairing pairing = new LocalApiPairing(Settings.GetSettingsDirectory(), () => DateTimeOffset.UtcNow);
            _making = pairing;
            ShowStatus();
            lblPairStatus.Text = "Making a code...";
            Exception failure = null;
            try {
                await Task.Run(pairing.MakeCode);
            }
            catch (Exception ex) {
                failure = ex;
            }
            if (_making != pairing) return;
            _making = null;
            if (failure != null) ShowPairingFailure(pairing, failure);
            else ShowCode(pairing);
            ShowStatus();
        }

        private void ShowCode(LocalApiPairing pairing) {
            _pairing = pairing;
            lblPairCode.Text = pairing.Code.Code;
            lblPairStatus.Text = LocalApiPairing.DescribeCountdown(pairing.Remaining);
        }

        // Shown once: the code is ended, so the next tick does not try again
        private void ShowPairingFailure(LocalApiPairing pairing, Exception ex) {
            pairing.End();
            lblPairStatus.Text = LocalApiPairing.DescribeFailure(ex);
        }

        // A code works only while the API listens, so one that is pending or being made is ended once the API is off
        // or failed to start again
        private void StopPairingIfTheApiStopped() {
            string stopped = LocalApiPairing.DescribeStop(_host.Status.State);
            if (stopped == null || (_pairing == null && _making == null)) return;
            EndPairing();
            lblPairStatus.Text = stopped;
            ShowStatus();
        }

        // One look at the pairing file on each tick of the status timer, until the code ends. A look that throws ends
        // the code, so the failure is shown once rather than on every tick.
        private void FollowPairing() {
            if (_pairing == null) return;
            ApiPairingEnd? end;
            try {
                end = _pairing.Look();
            }
            catch (Exception ex) {
                LocalApiPairing failed = _pairing;
                _pairing = null;
                lblPairCode.Text = "";
                ShowPairingFailure(failed, ex);
                return;
            }
            ShowLook(end);
        }

        private void ShowLook(ApiPairingEnd? end) {
            if (!end.HasValue) {
                lblPairStatus.Text = LocalApiPairing.DescribeCountdown(_pairing.Remaining);
                return;
            }
            lblPairStatus.Text = LocalApiPairing.DescribeEnd(end.Value, _pairing.PairedFamily);
            EndPairing();
        }

        // Ends the code that is made or followed, if any, and takes it off the dialog
        private void EndPairing() {
            LocalApiPairing pairing = _pairing ?? _making;
            _making = null;
            _pairing = null;
            lblPairCode.Text = "";
            pairing?.End();
        }

        // The token and the pairing code are shown only while the dialog is open; a pending code ends with it
        private void frmLocalApi_FormClosed(object sender, FormClosedEventArgs e) {
            tmrStatus.Stop();
            txtToken.Clear();
            EndPairing();
        }
    }
}
