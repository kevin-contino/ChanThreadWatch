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
            SetHostVersion();
            InstallExceptionHandlers();
            // Visual styles, text rendering, DPI mode and default font, from the Application* properties in ChanThreadWatch.csproj
            ApplicationConfiguration.Initialize();
            if (!ObtainMutex()) {
                MessageBox.Show("Another instance of this program is running.", "Already Running", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Application.Run(new frmChanThreadWatch());
        }

        // General.Version (About box, update check) reports this app's version, not ChanThreadWatch.Core's
        internal static void SetHostVersion() {
            General.HostVersion = typeof(Program).Assembly.GetName().Version;
        }

        // UI thread exceptions are logged and shown, and the program keeps running. Exceptions on
        // other threads still end the process (the CLR always does that), but get logged first.
        private static void InstallExceptionHandlers() {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => {
                HandleUIException(e.Exception, ShowErrorMessage);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                General.LogQuietly(FormatUnhandledException("Background thread", e.ExceptionObject, e.IsTerminating));
            };
        }

        private static void ShowErrorMessage(string message) {
            MessageBox.Show("An unexpected error occurred. Details were written to the log file." + Environment.NewLine + Environment.NewLine +
                message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // Set while an error message box is open. The box pumps messages, so a recurring UI
        // exception re-enters the handler; it is logged again but gets no second box.
        private static bool _showingError;

        // Logs every UI thread exception and shows at most one error message at a time. Never throws.
        internal static void HandleUIException(Exception ex, Action<string> showMessage) {
            General.LogQuietly(FormatUnhandledException("UI thread", ex, false));
            if (_showingError) return;
            _showingError = true;
            try {
                showMessage(ex.Message);
            }
            catch (Exception showEx) {
                General.LogQuietly(showEx.ToString());
            }
            finally {
                _showingError = false;
            }
        }

        internal static string FormatUnhandledException(string source, object exceptionObject, bool isTerminating) {
            string details = (exceptionObject != null) ? exceptionObject.ToString() : "(no exception object)";
            return "Unhandled exception (" + source + (isTerminating ? ", terminating" : "") + "):" + Environment.NewLine + details;
        }

        public static bool ObtainMutex() {
            return ObtainMutex(Settings.GetSettingsDirectory());
        }

        public static bool ObtainMutex(string settingsFolder) {
            SecurityIdentifier sid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            MutexSecurity security = new MutexSecurity();
            bool useDefaultSecurity = !TryAddAccessRules(security, sid);
            Mutex mutex = CreateMutex(GetMutexName(settingsFolder), useDefaultSecurity, security);
            if (!TryAcquireMutex(mutex)) {
                return false;
            }
            ReleaseMutex();
            _mutex = mutex;
            return true;
        }

        // One instance per settings folder. Older versions use the same name, so they exclude each other too.
        internal static string GetMutexName(string settingsFolder) {
            return @"Global\ChanThreadWatch_" + General.Calculate64BitMD5(Encoding.UTF8.GetBytes(
                settingsFolder.ToUpperInvariant())).ToString("X16");
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
            if (useDefaultSecurity) {
                return new Mutex(false, name);
            }
            // If the owner exits between the failed create and the open, try again, so the mutex is
            // never created without its ACL. .NET Framework retried the same way.
            for (int attempt = 1; attempt < 10; attempt++) {
                Mutex mutex = TryCreateOrOpenMutex(name, security);
                if (mutex != null) return mutex;
            }
            return MutexAcl.Create(false, name, out _, security);
        }

        // Returns null if the mutex existed when the create was denied but was gone before the open.
        private static Mutex TryCreateOrOpenMutex(string name, MutexSecurity security) {
            try {
                return MutexAcl.Create(false, name, out _, security);
            }
            catch (UnauthorizedAccessException) {
                // The mutex already exists and its ACL denies the full access MutexAcl.Create asks for.
                // Open it with only the rights a wait and a release need.
                Mutex mutex;
                return MutexAcl.TryOpenExisting(name, MutexRights.Synchronize | MutexRights.Modify, out mutex) ? mutex : null;
            }
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