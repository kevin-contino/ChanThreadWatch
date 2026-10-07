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
            this.btnOK = new System.Windows.Forms.Button();
            this.btnCancel = new System.Windows.Forms.Button();
            this.tmrStatus = new System.Windows.Forms.Timer(this.components);
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
            // btnOK
            // 
            this.btnOK.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnOK.Location = new System.Drawing.Point(250, 297);
            this.btnOK.Name = "btnOK";
            this.btnOK.Size = new System.Drawing.Size(60, 23);
            this.btnOK.TabIndex = 12;
            this.btnOK.Text = "OK";
            this.btnOK.UseVisualStyleBackColor = true;
            this.btnOK.Click += new System.EventHandler(this.btnOK_Click);
            // 
            // btnCancel
            // 
            this.btnCancel.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnCancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.btnCancel.Location = new System.Drawing.Point(318, 297);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(68, 23);
            this.btnCancel.TabIndex = 13;
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
            this.ClientSize = new System.Drawing.Size(398, 332);
            this.Controls.Add(this.btnCancel);
            this.Controls.Add(this.btnOK);
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
        private System.Windows.Forms.Button btnOK;
        private System.Windows.Forms.Button btnCancel;
        private System.Windows.Forms.Timer tmrStatus;
    }
}
