namespace JDP {
    partial class frmLocalApi {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing) {
            if (disposing && (components != null)) {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent() {
            this.components = new System.ComponentModel.Container();
            this.chkEnabled = new System.Windows.Forms.CheckBox();
            this.lblPort = new System.Windows.Forms.Label();
            this.txtPort = new System.Windows.Forms.TextBox();
            this.lblStatusCaption = new System.Windows.Forms.Label();
            this.lblStatus = new System.Windows.Forms.Label();
            this.btnNewToken = new System.Windows.Forms.Button();
            this.txtToken = new System.Windows.Forms.TextBox();
            this.btnCopyToken = new System.Windows.Forms.Button();
            this.lblTokenNote = new System.Windows.Forms.Label();
            this.chkAllowUnknownHosts = new System.Windows.Forms.CheckBox();
            this.lblAllowUnknownHostsNote = new System.Windows.Forms.Label();
            this.grpExtension = new System.Windows.Forms.GroupBox();
            this.lblChromeCaption = new System.Windows.Forms.Label();
            this.lblChrome = new System.Windows.Forms.Label();
            this.btnUnpairChrome = new System.Windows.Forms.Button();
            this.lblFirefoxCaption = new System.Windows.Forms.Label();
            this.lblFirefox = new System.Windows.Forms.Label();
            this.btnUnpairFirefox = new System.Windows.Forms.Button();
            this.lblClientsNote = new System.Windows.Forms.Label();
            this.btnPair = new System.Windows.Forms.Button();
            this.lblPairCode = new System.Windows.Forms.Label();
            this.lblPairStatus = new System.Windows.Forms.Label();
            this.btnOK = new System.Windows.Forms.Button();
            this.btnCancel = new System.Windows.Forms.Button();
            this.tmrStatus = new System.Windows.Forms.Timer(this.components);
            this.grpExtension.SuspendLayout();
            this.SuspendLayout();
            // 
            // chkEnabled
            // 
            this.chkEnabled.AutoSize = true;
            this.chkEnabled.Location = new System.Drawing.Point(12, 12);
            this.chkEnabled.Name = "chkEnabled";
            this.chkEnabled.Size = new System.Drawing.Size(124, 17);
            this.chkEnabled.TabIndex = 0;
            this.chkEnabled.Text = "Enable the local API";
            this.chkEnabled.UseVisualStyleBackColor = true;
            // 
            // lblPort
            // 
            this.lblPort.AutoSize = true;
            this.lblPort.Location = new System.Drawing.Point(12, 41);
            this.lblPort.Name = "lblPort";
            this.lblPort.Size = new System.Drawing.Size(29, 13);
            this.lblPort.TabIndex = 1;
            this.lblPort.Text = "Port:";
            // 
            // txtPort
            // 
            this.txtPort.AccessibleName = "Port";
            this.txtPort.Location = new System.Drawing.Point(56, 38);
            this.txtPort.MaxLength = 5;
            this.txtPort.Name = "txtPort";
            this.txtPort.Size = new System.Drawing.Size(50, 20);
            this.txtPort.TabIndex = 2;
            // 
            // lblStatusCaption
            // 
            this.lblStatusCaption.AutoSize = true;
            this.lblStatusCaption.Location = new System.Drawing.Point(12, 68);
            this.lblStatusCaption.Name = "lblStatusCaption";
            this.lblStatusCaption.Size = new System.Drawing.Size(40, 13);
            this.lblStatusCaption.TabIndex = 4;
            this.lblStatusCaption.Text = "Status:";
            // 
            // lblStatus
            // 
            this.lblStatus.Location = new System.Drawing.Point(56, 68);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(330, 39);
            this.lblStatus.TabIndex = 5;
            // 
            // btnNewToken
            // 
            this.btnNewToken.Location = new System.Drawing.Point(12, 112);
            this.btnNewToken.Name = "btnNewToken";
            this.btnNewToken.Size = new System.Drawing.Size(90, 23);
            this.btnNewToken.TabIndex = 6;
            this.btnNewToken.Text = "New token...";
            this.btnNewToken.UseVisualStyleBackColor = true;
            this.btnNewToken.Click += new System.EventHandler(this.btnNewToken_Click);
            // 
            // txtToken
            // 
            this.txtToken.AccessibleName = "New token";
            this.txtToken.Location = new System.Drawing.Point(12, 143);
            this.txtToken.Name = "txtToken";
            this.txtToken.ReadOnly = true;
            this.txtToken.Size = new System.Drawing.Size(306, 20);
            this.txtToken.TabIndex = 7;
            this.txtToken.Visible = false;
            // 
            // btnCopyToken
            // 
            this.btnCopyToken.AccessibleName = "Copy the token";
            this.btnCopyToken.Location = new System.Drawing.Point(326, 141);
            this.btnCopyToken.Name = "btnCopyToken";
            this.btnCopyToken.Size = new System.Drawing.Size(60, 23);
            this.btnCopyToken.TabIndex = 8;
            this.btnCopyToken.Text = "Copy";
            this.btnCopyToken.UseVisualStyleBackColor = true;
            this.btnCopyToken.Visible = false;
            this.btnCopyToken.Click += new System.EventHandler(this.btnCopyToken_Click);
            // 
            // lblTokenNote
            // 
            this.lblTokenNote.Location = new System.Drawing.Point(12, 168);
            this.lblTokenNote.Name = "lblTokenNote";
            this.lblTokenNote.Size = new System.Drawing.Size(374, 39);
            this.lblTokenNote.TabIndex = 9;
            this.lblTokenNote.Text = "The token is shown only this once. Send it as \"Authorization: Bearer <token>\". Copy keeps it out of the Windows clipboard history, but other clipboard tools may still keep it.";
            this.lblTokenNote.Visible = false;
            // 
            // chkAllowUnknownHosts
            // 
            this.chkAllowUnknownHosts.AutoSize = true;
            this.chkAllowUnknownHosts.Location = new System.Drawing.Point(12, 215);
            this.chkAllowUnknownHosts.Name = "chkAllowUnknownHosts";
            this.chkAllowUnknownHosts.Size = new System.Drawing.Size(122, 17);
            this.chkAllowUnknownHosts.TabIndex = 10;
            this.chkAllowUnknownHosts.Text = "Allow unknown sites";
            this.chkAllowUnknownHosts.UseVisualStyleBackColor = true;
            // 
            // lblAllowUnknownHostsNote
            // 
            this.lblAllowUnknownHostsNote.Location = new System.Drawing.Point(29, 235);
            this.lblAllowUnknownHostsNote.Name = "lblAllowUnknownHostsNote";
            this.lblAllowUnknownHostsNote.Size = new System.Drawing.Size(357, 52);
            this.lblAllowUnknownHostsNote.TabIndex = 11;
            this.lblAllowUnknownHostsNote.Text = "Lets scripts add threads of sites that this program has no support for. Such threads never connect to " +
    "local or private addresses, but anyone with the token can then make this program download from any public site.";
            // 
            // grpExtension
            // 
            this.grpExtension.Controls.Add(this.lblPairStatus);
            this.grpExtension.Controls.Add(this.lblPairCode);
            this.grpExtension.Controls.Add(this.btnPair);
            this.grpExtension.Controls.Add(this.lblClientsNote);
            this.grpExtension.Controls.Add(this.btnUnpairFirefox);
            this.grpExtension.Controls.Add(this.lblFirefox);
            this.grpExtension.Controls.Add(this.lblFirefoxCaption);
            this.grpExtension.Controls.Add(this.btnUnpairChrome);
            this.grpExtension.Controls.Add(this.lblChrome);
            this.grpExtension.Controls.Add(this.lblChromeCaption);
            this.grpExtension.Location = new System.Drawing.Point(12, 295);
            this.grpExtension.Name = "grpExtension";
            this.grpExtension.Size = new System.Drawing.Size(374, 208);
            this.grpExtension.TabIndex = 12;
            this.grpExtension.TabStop = false;
            this.grpExtension.Text = "Browser extension";
            // 
            // lblChromeCaption
            // 
            this.lblChromeCaption.AutoSize = true;
            this.lblChromeCaption.Location = new System.Drawing.Point(9, 22);
            this.lblChromeCaption.Name = "lblChromeCaption";
            this.lblChromeCaption.Size = new System.Drawing.Size(47, 13);
            this.lblChromeCaption.TabIndex = 0;
            this.lblChromeCaption.Text = "Chrome:";
            // 
            // lblChrome
            // 
            this.lblChrome.Location = new System.Drawing.Point(70, 22);
            this.lblChrome.Name = "lblChrome";
            this.lblChrome.Size = new System.Drawing.Size(226, 13);
            this.lblChrome.TabIndex = 1;
            // 
            // btnUnpairChrome
            // 
            this.btnUnpairChrome.AccessibleName = "Unpair the Chrome extension";
            this.btnUnpairChrome.Location = new System.Drawing.Point(304, 17);
            this.btnUnpairChrome.Name = "btnUnpairChrome";
            this.btnUnpairChrome.Size = new System.Drawing.Size(60, 23);
            this.btnUnpairChrome.TabIndex = 2;
            this.btnUnpairChrome.Text = "Unpair";
            this.btnUnpairChrome.UseVisualStyleBackColor = true;
            this.btnUnpairChrome.Click += new System.EventHandler(this.btnUnpairChrome_Click);
            // 
            // lblFirefoxCaption
            // 
            this.lblFirefoxCaption.AutoSize = true;
            this.lblFirefoxCaption.Location = new System.Drawing.Point(9, 51);
            this.lblFirefoxCaption.Name = "lblFirefoxCaption";
            this.lblFirefoxCaption.Size = new System.Drawing.Size(45, 13);
            this.lblFirefoxCaption.TabIndex = 3;
            this.lblFirefoxCaption.Text = "Firefox:";
            // 
            // lblFirefox
            // 
            this.lblFirefox.Location = new System.Drawing.Point(70, 51);
            this.lblFirefox.Name = "lblFirefox";
            this.lblFirefox.Size = new System.Drawing.Size(226, 13);
            this.lblFirefox.TabIndex = 4;
            // 
            // btnUnpairFirefox
            // 
            this.btnUnpairFirefox.AccessibleName = "Unpair the Firefox extension";
            this.btnUnpairFirefox.Location = new System.Drawing.Point(304, 46);
            this.btnUnpairFirefox.Name = "btnUnpairFirefox";
            this.btnUnpairFirefox.Size = new System.Drawing.Size(60, 23);
            this.btnUnpairFirefox.TabIndex = 5;
            this.btnUnpairFirefox.Text = "Unpair";
            this.btnUnpairFirefox.UseVisualStyleBackColor = true;
            this.btnUnpairFirefox.Click += new System.EventHandler(this.btnUnpairFirefox_Click);
            // 
            // lblClientsNote
            // 
            this.lblClientsNote.Location = new System.Drawing.Point(9, 75);
            this.lblClientsNote.Name = "lblClientsNote";
            this.lblClientsNote.Size = new System.Drawing.Size(355, 39);
            this.lblClientsNote.TabIndex = 6;
            this.lblClientsNote.Text = "api-clients.txt could not be read, or it is damaged, a link, or others can read it, so no browser extension can connect.";
            this.lblClientsNote.Visible = false;
            // 
            // btnPair
            // 
            this.btnPair.Enabled = false;
            this.btnPair.Location = new System.Drawing.Point(9, 118);
            this.btnPair.Name = "btnPair";
            this.btnPair.Size = new System.Drawing.Size(110, 23);
            this.btnPair.TabIndex = 7;
            this.btnPair.Text = "Pair extension...";
            this.btnPair.UseVisualStyleBackColor = true;
            this.btnPair.Click += new System.EventHandler(this.btnPair_Click);
            // 
            // lblPairCode
            // 
            this.lblPairCode.AccessibleName = "Pairing code";
            this.lblPairCode.Font = new System.Drawing.Font("Consolas", 20F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblPairCode.Location = new System.Drawing.Point(130, 113);
            this.lblPairCode.Name = "lblPairCode";
            this.lblPairCode.Size = new System.Drawing.Size(234, 32);
            this.lblPairCode.TabIndex = 8;
            this.lblPairCode.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // lblPairStatus
            // 
            this.lblPairStatus.Location = new System.Drawing.Point(9, 148);
            this.lblPairStatus.Name = "lblPairStatus";
            this.lblPairStatus.Size = new System.Drawing.Size(355, 52);
            this.lblPairStatus.TabIndex = 9;
            // 
            // btnOK
            // 
            this.btnOK.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnOK.Location = new System.Drawing.Point(250, 513);
            this.btnOK.Name = "btnOK";
            this.btnOK.Size = new System.Drawing.Size(60, 23);
            this.btnOK.TabIndex = 13;
            this.btnOK.Text = "OK";
            this.btnOK.UseVisualStyleBackColor = true;
            this.btnOK.Click += new System.EventHandler(this.btnOK_Click);
            // 
            // btnCancel
            // 
            this.btnCancel.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnCancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.btnCancel.Location = new System.Drawing.Point(318, 513);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(68, 23);
            this.btnCancel.TabIndex = 14;
            this.btnCancel.Text = "Cancel";
            this.btnCancel.UseVisualStyleBackColor = true;
            // 
            // tmrStatus
            // 
            this.tmrStatus.Enabled = true;
            this.tmrStatus.Interval = 500;
            this.tmrStatus.Tick += new System.EventHandler(this.tmrStatus_Tick);
            // 
            // frmLocalApi
            // 
            this.AcceptButton = this.btnOK;
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.None;
            this.CancelButton = this.btnCancel;
            this.ClientSize = new System.Drawing.Size(398, 548);
            this.Controls.Add(this.btnCancel);
            this.Controls.Add(this.btnOK);
            this.Controls.Add(this.grpExtension);
            this.Controls.Add(this.lblAllowUnknownHostsNote);
            this.Controls.Add(this.chkAllowUnknownHosts);
            this.Controls.Add(this.lblTokenNote);
            this.Controls.Add(this.btnCopyToken);
            this.Controls.Add(this.txtToken);
            this.Controls.Add(this.btnNewToken);
            this.Controls.Add(this.lblStatus);
            this.Controls.Add(this.lblStatusCaption);
            this.Controls.Add(this.txtPort);
            this.Controls.Add(this.lblPort);
            this.Controls.Add(this.chkEnabled);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "frmLocalApi";
            this.ShowIcon = false;
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Local API";
            this.FormClosed += new System.Windows.Forms.FormClosedEventHandler(this.frmLocalApi_FormClosed);
            this.grpExtension.ResumeLayout(false);
            this.grpExtension.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.CheckBox chkEnabled;
        private System.Windows.Forms.Label lblPort;
        private System.Windows.Forms.TextBox txtPort;
        private System.Windows.Forms.Label lblStatusCaption;
        private System.Windows.Forms.Label lblStatus;
        private System.Windows.Forms.Button btnNewToken;
        private System.Windows.Forms.TextBox txtToken;
        private System.Windows.Forms.Button btnCopyToken;
        private System.Windows.Forms.Label lblTokenNote;
        private System.Windows.Forms.CheckBox chkAllowUnknownHosts;
        private System.Windows.Forms.Label lblAllowUnknownHostsNote;
        private System.Windows.Forms.GroupBox grpExtension;
        private System.Windows.Forms.Label lblChromeCaption;
        private System.Windows.Forms.Label lblChrome;
        private System.Windows.Forms.Button btnUnpairChrome;
        private System.Windows.Forms.Label lblFirefoxCaption;
        private System.Windows.Forms.Label lblFirefox;
        private System.Windows.Forms.Button btnUnpairFirefox;
        private System.Windows.Forms.Label lblClientsNote;
        private System.Windows.Forms.Button btnPair;
        private System.Windows.Forms.Label lblPairCode;
        private System.Windows.Forms.Label lblPairStatus;
        private System.Windows.Forms.Button btnOK;
        private System.Windows.Forms.Button btnCancel;
        private System.Windows.Forms.Timer tmrStatus;
    }
}
