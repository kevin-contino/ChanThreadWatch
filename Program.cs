using System;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace JDP {
    internal static class Program {
        private static Mutex _mutex;

        [STAThread]
        private static void Main() {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (!ObtainMutex()) {
                MessageBox.Show("Another instance of this program is running.", "Already Running", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Application.Run(new frmChanThreadWatch());
        }

        public static bool ObtainMutex() {
            return ObtainMutex(Settings.GetSettingsDirectory());
        }

        public static bool ObtainMutex(string settingsFolder) {
            SecurityIdentifier sid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            MutexSecurity security = new MutexSecurity();
            bool useDefaultSecurity = !TryAddAccessRules(security, sid);
            string name = @"Global\ChanThreadWatch_" + General.Calculate64BitMD5(Encoding.UTF8.GetBytes(
                settingsFolder.ToUpperInvariant())).ToString("X16");
            Mutex mutex = CreateMutex(name, useDefaultSecurity, security);
            if (!TryAcquireMutex(mutex)) {
                return false;
            }
            ReleaseMutex();
            _mutex = mutex;
            return true;
        }

        // Returns false if the platform does not support the access rules (Mono).
        private static bool TryAddAccessRules(MutexSecurity security, SecurityIdentifier sid) {
            try {
                security.AddAccessRule(new MutexAccessRule(sid, MutexRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new MutexAccessRule(sid, MutexRights.ChangePermissions, AccessControlType.Deny));
                security.AddAccessRule(new MutexAccessRule(sid, MutexRights.Delete, AccessControlType.Deny));
            }
            catch (Exception ex) {
                if (ex is ArgumentOutOfRangeException || ex is NotImplementedException) {
                    // Workaround for Mono
                    return false;
                }
                throw;
            }
            return true;
        }

        private static Mutex CreateMutex(string name, bool useDefaultSecurity, MutexSecurity security) {
            bool createdNew;
            if (useDefaultSecurity) {
                return new Mutex(false, name);
            }
            return new Mutex(false, name, out createdNew, security);
        }

        // Returns false if another process holds the mutex. An abandoned mutex counts as acquired.
        private static bool TryAcquireMutex(Mutex mutex) {
            try {
                if (!mutex.WaitOne(0, false)) {
                    return false;
                }
            }
            catch (AbandonedMutexException) { }
            return true;
        }

        public static void ReleaseMutex() {
            if (_mutex == null) return;
            try {
                _mutex.ReleaseMutex();
            }
            catch { }
            _mutex = null;
        }
    }
}