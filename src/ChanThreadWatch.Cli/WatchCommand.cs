using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace JDP.Cli {
    // ctw watch: watches the threads of the thread list without a window, as the app does (see HeadlessWatch). It
    // holds the settings folder's lock for as long as it runs, so the app and the other ctw commands refuse
    // meanwhile, and it refuses to start while another program uses the folder. Saved logins are read, written and
    // deleted as the app does, with the app's backend for this system (DPAPI, Keychain or Secret Service).
    internal static class WatchCommand {
        // The saved-login backend; the tests put one in its place that never asks a system login store
        internal static Func<IStoredAuthProtector> CreateProtector = StoredAuth.CreateSystemProtector;

        public static int Run(CliContext context) {
            WatchStatusOutput output = new WatchStatusOutput(context.Output);
            if (context.StopToken.HasValue) return Run(context, context.StopToken.Value, output);
            using (StopSignals signals = StopSignals.Register(output)) {
                try {
                    return Run(context, signals.Token, output);
                }
                finally {
                    // A signal that ends the process when its handler returns waits for this
                    signals.Finish();
                }
            }
        }

        private static int Run(CliContext context, CancellationToken stopToken, WatchStatusOutput output) {
            SettingsFolder folder = context.GetSettingsFolder();
            // The app creates its folder in the application data the same way
            if (folder.IsAppData) Directory.CreateDirectory(folder.Path);
            using (UseSettingsFolder(folder))
            using (SettingsFolderAccess.LockForWatch(folder.Path))
            using (UseProtector(CreateProtector())) {
                LoadSettings(folder);
                // As the status lines, an error output that can't be written is dropped
                WatchStatusOutput error = new WatchStatusOutput(context.Error);
                WarnAboutPlaintextLogins(error, folder.Path);
                return new HeadlessWatch(output, folder.Path).Run(stopToken) ? CliApp.ExitSuccess : ReportSaveFailed(error);
            }
        }

        // Core then logs, saves the settings and makes backups in this folder, and bases relative download and
        // completed folders on it (in portable mode it is the app's folder). Set before anything is logged.
        private static IDisposable UseSettingsFolder(SettingsFolder folder) {
            Settings.SettingsDirectoryOverride = folder.Path;
            Settings.RelativeFolderBaseOverride = folder.IsAppData ? null : folder.Path;
            return new Undo(() => {
                Settings.SettingsDirectoryOverride = null;
                Settings.RelativeFolderBaseOverride = null;
            });
        }

        private static IDisposable UseProtector(IStoredAuthProtector protector) {
            IStoredAuthProtector previous = StoredAuth.Protector;
            StoredAuth.Protector = protector;
            return new Undo(() => StoredAuth.Protector = previous);
        }

        // As the app at startup: a settings file that can't be read is logged and copied aside, and the defaults are
        // used. Unlike the app, a missing download folder is refused rather than replaced by the default one, so
        // settings.txt is never changed and nothing is written to Documents behind the user's back.
        private static void LoadSettings(SettingsFolder folder) {
            string path = Path.Combine(folder.Path, Settings.SettingsFileName);
            EnsureSettingsCanBeRead(path);
            Settings.Load(path);
            if (folder.IsAppData) EnsureNoRelativeFolder();
            EnsureDownloadFolderExists();
        }

        // A settings file that can't be read is refused, as ctw add does: Settings.Load would log the failure, copy the
        // file aside and go on with the built-in defaults (with no download folder). It is read first through
        // SharedFile, which waits a moment for a program that is replacing it; ctw holds the folder's lock, so the app
        // can't change it before Settings.Load reads it again. A missing file is no settings, as in the app.
        private static void EnsureSettingsCanBeRead(string path) {
            try {
                SharedFile.ReadAllLines(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                throw new CliException(Settings.SettingsFileName + " could not be read, so ctw watch does not start: " + ex.Message);
            }
        }

        // A relative download or completed folder is based on the app's folder, which ctw can't find when the
        // settings are in the application data
        internal static void EnsureNoRelativeFolder() {
            if (IsRelative(Settings.DownloadFolder, Settings.DownloadFolderIsRelative) || IsRelative(Settings.CompletedFolder, Settings.CompletedFolderIsRelative)) {
                throw new CliException("The download or completed folder is set relative to the app's folder, which ctw can't find when the settings are in " +
                    "the application data. Set full paths for them in the app's settings, then try again.");
            }
        }

        private static bool IsRelative(string folder, bool? isRelative) {
            return !String.IsNullOrEmpty(folder) && isRelative == true;
        }

        // The completed folder is not checked: watch never moves threads there (only the app's Remove Completed does)
        internal static void EnsureDownloadFolderExists() {
            if (String.IsNullOrEmpty(Settings.DownloadFolder)) {
                throw new CliException("No download folder is set in " + Settings.SettingsFileName + ". Set one in the app's settings, then try again.");
            }
            string folder = GetDownloadFolder();
            if (!Directory.Exists(folder)) {
                throw new CliException("The download folder " + folder + " does not exist. Create it, or set another one in the app's settings, then try again.");
            }
        }

        // An absolute path of another OS (a portable folder used on Windows and on Linux or macOS) can't be used here
        private static string GetDownloadFolder() {
            try {
                return Settings.AbsoluteDownloadDirectory;
            }
            catch (FormatException ex) {
                throw new CliException(ex.Message + " Set a download folder for this system in the app's settings, then try again.");
            }
        }

        // Where no login store can be used, the app uses plaintext logins from an older version for the session only
        // and clears them at the next save (logged once); watch does the same and says so first
        private static void WarnAboutPlaintextLogins(WatchStatusOutput error, string folder) {
            if (StoredAuth.CanProtect) return;
            foreach (string fileName in TryGetFilesWithPlaintextLogins(folder)) {
                error.WriteLine("ctw: warning: " + fileName + " holds logins saved without encryption by an older version. No login store can be used on " +
                    "this system, so they are used for this session only and cleared at the next save (see " + Settings.LogFileName + ").");
            }
        }

        // The warning only gives notice: a file that can't be read here is left to the load, which logs it
        private static List<string> TryGetFilesWithPlaintextLogins(string folder) {
            try {
                return GetFilesWithPlaintextLogins(folder);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                return new List<string>();
            }
        }

        internal static List<string> GetFilesWithPlaintextLogins(string folder) {
            List<string> files = new List<string>();
            string[] threads = SharedFile.ReadAllLines(Path.Combine(folder, Settings.ThreadsFileName));
            if (threads != null && ThreadListFile.HasPlaintextAuth(threads)) files.Add(Settings.ThreadsFileName);
            if (SettingsHavePlaintextLogins(Path.Combine(folder, Settings.SettingsFileName))) files.Add(Settings.SettingsFileName);
            return files;
        }

        private static bool SettingsHavePlaintextLogins(string path) {
            if (!File.Exists(path)) return false;
            byte[] content = File.ReadAllBytes(path);
            return !content.AsSpan().SequenceEqual(Settings.BlankPlaintextAuth(content));
        }

        private static int ReportSaveFailed(WatchStatusOutput error) {
            error.WriteLine("ctw: The thread list could not be saved when ctw watch stopped. See " + Settings.LogFileName + " in the settings folder.");
            return CliApp.ExitFailure;
        }

        private sealed class Undo : IDisposable {
            private readonly Action _undo;

            public Undo(Action undo) {
                _undo = undo;
            }

            public void Dispose() {
                _undo();
            }
        }
    }

    // Ctrl+C (SIGINT), Ctrl+Break or Ctrl+\ (SIGQUIT), SIGTERM and SIGHUP stop the watch cleanly. On Windows SIGHUP
    // is the console window being closed and SIGTERM a shutdown; Windows ends the process about 5 seconds after
    // such an event, whatever the handler does. So for SIGTERM and SIGHUP the handler waits (at most SaveWait) until
    // the final save has finished. A second signal ends the process at once; the thread list on disk is then the
    // one saved last (every save replaces the file in one step).
    internal sealed class StopSignals : IDisposable {
        internal static readonly TimeSpan WindowsSaveWait = TimeSpan.FromSeconds(4);
        internal static readonly TimeSpan UnixSaveWait = TimeSpan.FromSeconds(20);
        private static readonly PosixSignal[] _stopSignals = { PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGTERM, PosixSignal.SIGHUP };

        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        // Not disposed: a handler can still be waiting on it while the program ends
        private readonly ManualResetEventSlim _finished = new ManualResetEventSlim(false);
        private readonly List<PosixSignalRegistration> _registrations = new List<PosixSignalRegistration>();
        private readonly WatchStatusOutput _output;
        private readonly TimeSpan _saveWait;
        private int _requested;

        // Not registered for any signal (the tests call Handle)
        internal StopSignals(WatchStatusOutput output, TimeSpan saveWait) {
            _output = output;
            _saveWait = saveWait;
        }

        public static StopSignals Register(WatchStatusOutput output) {
            StopSignals signals = new StopSignals(output, OperatingSystem.IsWindows() ? WindowsSaveWait : UnixSaveWait);
            foreach (PosixSignal signal in _stopSignals) {
                signals._registrations.Add(PosixSignalRegistration.Create(signal, context => context.Cancel = signals.Handle(context.Signal)));
            }
            return signals;
        }

        public CancellationToken Token {
            get { return _stop.Token; }
        }

        // The watch has stopped and saved, or failed
        public void Finish() {
            _finished.Set();
        }

        // Returns whether the signal is canceled, so the process goes on to its final save. A second signal is not,
        // and ends the process as it would without this handler. Neither is a SIGTERM or SIGHUP whose wait for the
        // final save ran out: the process then ends, and threads.txt is the one saved last (the stop saves it once
        // before it waits for the watchers).
        internal bool Handle(PosixSignal signal) {
            if (!RequestStop()) return false;
            return !IsTerminating(signal) || _finished.Wait(_saveWait);
        }

        private static bool IsTerminating(PosixSignal signal) {
            return signal == PosixSignal.SIGTERM || signal == PosixSignal.SIGHUP;
        }

        private bool RequestStop() {
            if (Interlocked.Exchange(ref _requested, 1) != 0) return false;
            _output.WriteLine("Stopping. Press Ctrl+C again to quit at once, without the final save.");
            try {
                _stop.Cancel();
                return true;
            }
            catch (ObjectDisposedException) {
                return false;
            }
        }

        // Marked as requested first, so a handler that runs late neither cancels the disposed source nor waits
        public void Dispose() {
            Interlocked.Exchange(ref _requested, 1);
            _finished.Set();
            foreach (PosixSignalRegistration registration in _registrations) {
                registration.Dispose();
            }
            _stop.Dispose();
        }
    }
}
