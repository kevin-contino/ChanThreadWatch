using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace JDP {
    internal static class Program {
        private static Mutex _mutex;
        private static SettingsFolderLock _settingsFolderLock;

        // Shown when the settings folder's lock is held by a program the window can't run beside
        internal const string SettingsFolderInUseMessage = "The settings folder is in use by another copy of Chan Thread Watch " +
            "(on this or another computer). Close it and try again.";

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
            // Taken after the mutex, before the thread list is loaded
            SettingsFolderLock folderLock;
            if (!TryLockSettingsFolder(null, Settings.GetSettingsDirectory(), true, out folderLock)) return;
            ReplaceSettingsFolderLock(folderLock);
            Application.Run(new frmChanThreadWatch());
        }

        // Returns false (after showing a message) if the window must not use the folder: the
        // lock file can't be written, or another program holds the lock and the user didn't
        // choose to start anyway. On true, folderLock is null if the user chose to start
        // without the lock (another window holds it on another computer).
        // atStartup picks the advice shown when the folder can't be written.
        internal static bool TryLockSettingsFolder(IWin32Window owner, string folder, bool atStartup, out SettingsFolderLock folderLock) {
            try {
                if (TryAcquireWaitingForCommandLine(folder, atStartup, out folderLock)) return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                Logger.Log("The settings folder lock could not be written: " + ex.Message);
                MessageBox.Show(owner, GetCannotWriteSettingsFolderMessage(folder, atStartup), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                folderLock = null;
                return false;
            }
            return ConfirmStartWithoutLock(owner, folder);
        }

        // Shown when the command line (ctw) still holds the settings folder's lock after the longer wait
        internal const string CommandLineHoldsFolderMessage = "The command line tool (ctw) is changing the thread list in this settings folder. " +
            "Try again when it has finished.";

        // Waits as for a program that is closing, and at startup longer when the command line holds the lock
        // (see SettingsFolderLockHolder.Decide), since it lets go once it has changed the thread list. The
        // settings window (atStartup false) waits only DefaultWait, so its UI thread is not blocked for long;
        // the user can try again.
        internal static bool TryAcquireWaitingForCommandLine(string folder, bool atStartup, out SettingsFolderLock folderLock) {
            if (SettingsFolderLock.TryAcquire(folder, SettingsFolderLockHolder.WinForms, SettingsFolderLock.DefaultWait, out folderLock)) return true;
            return atStartup && DecideForHolder(folder) == HeldLockAction.WaitForCommandLine &&
                SettingsFolderLock.TryAcquire(folder, SettingsFolderLockHolder.WinForms, SettingsFolderLock.CommandLineWait, out folderLock);
        }

        private static HeldLockAction DecideForHolder(string folder) {
            SettingsFolderLockHolder holder = SettingsFolderLock.ReadHolderAfterRecordIsWritten(folder);
            return SettingsFolderLockHolder.Decide(holder, SettingsFolderLockHolder.WinForms, SettingsFolderLockHolder.GetThisMachineName());
        }

        // The message for a lock the window can't take and must not start without
        internal static string GetHeldLockMessage(HeldLockAction action) {
            return action == HeldLockAction.WaitForCommandLine ? CommandLineHoldsFolderMessage : SettingsFolderInUseMessage;
        }

        private static bool ConfirmStartWithoutLock(IWin32Window owner, string folder) {
            SettingsFolderLockHolder holder = SettingsFolderLock.ReadHolderAfterRecordIsWritten(folder);
            HeldLockAction action = SettingsFolderLockHolder.Decide(holder, SettingsFolderLockHolder.WinForms, SettingsFolderLockHolder.GetThisMachineName());
            if (action != HeldLockAction.AskToStartAnyway) {
                MessageBox.Show(owner, GetHeldLockMessage(action), "Already Running", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            if (MessageBox.Show(owner, GetRunningOnOtherComputerMessage(holder.MachineName), "Already Running",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
            Logger.Log("Started without the settings folder lock, which Chan Thread Watch on " + holder.MachineName + " holds.");
            return true;
        }

        internal static string GetRunningOnOtherComputerMessage(string machineName) {
            return "Chan Thread Watch is already running on " + machineName + " with this settings folder. " +
                "If both keep running, their saves can overwrite each other's thread list. Start anyway?";
        }

        // At startup the folder comes from where the program is (portable mode) or AppData; when
        // the settings folder is being changed, the user picks another
        internal static string GetCannotWriteSettingsFolderMessage(string folder, bool atStartup) {
            string advice = atStartup
                ? "Move the program to a folder you can write to, or remove " + Settings.SettingsFileName + " from the program folder to keep settings in AppData."
                : "Choose a folder you can write to.";
            return "Chan Thread Watch cannot write to its settings folder " + folder + ". " + advice;
        }

        // For a window that started without the lock (the user chose to start anyway): takes
        // the lock once the other program has let go of it, so a program started later is warned
        // or refused again. Returns true if it took the lock now. Never waits or throws.
        internal static bool TryTakeMissingSettingsFolderLock(string folder) {
            if (_settingsFolderLock != null) return false;
            try {
                SettingsFolderLock folderLock;
                if (!SettingsFolderLock.TryAcquire(folder, SettingsFolderLockHolder.WinForms, out folderLock)) return false;
                ReplaceSettingsFolderLock(folderLock);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                return false;
            }
            General.LogQuietly("Took the settings folder lock, which another program held when this one started.");
            return true;
        }

        // Keeps the new lock (null for none) and releases the one held before
        public static void ReplaceSettingsFolderLock(SettingsFolderLock folderLock) {
            SettingsFolderLock oldLock = _settingsFolderLock;
            _settingsFolderLock = folderLock;
            if (oldLock != null) oldLock.Dispose();
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
        // The name is computed in Core, where the command line checks it as well.
        internal static string GetMutexName(string settingsFolder) {
            return SettingsFolderLock.GetAppMutexName(settingsFolder);
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