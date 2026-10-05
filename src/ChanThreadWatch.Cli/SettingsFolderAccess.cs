using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;

namespace JDP.Cli {
    // The settings folder ctw uses. As the app (Settings.GetSettingsDirectory): the program's own folder when it
    // holds settings.txt (portable mode), otherwise the app's folder in the application data. ctw also looks in the
    // parent folder, so ctw can also sit in a folder next to ChanThreadWatch.exe. Finding it creates nothing.
    internal sealed class SettingsFolder {
        public SettingsFolder(string path, bool isAppData) {
            Path = path;
            IsAppData = isAppData;
        }

        public string Path { get; }
        public bool IsAppData { get; }

        // programFolder is written as the app writes its own folder (Settings.ExeDirectory), so the window's mutex
        // name for a portable folder is the same
        public static SettingsFolder Find(string programFolder) {
            programFolder = System.IO.Path.TrimEndingDirectorySeparator(programFolder);
            if (HasSettingsFile(programFolder)) return new SettingsFolder(programFolder, false);
            string parent = System.IO.Path.GetDirectoryName(programFolder);
            if (parent != null && HasSettingsFile(parent)) return new SettingsFolder(parent, false);
            return new SettingsFolder(Settings.AppDataDirectory, true);
        }

        private static bool HasSettingsFile(string folder) {
            return File.Exists(System.IO.Path.Combine(folder, Settings.SettingsFileName));
        }

        public string Describe() {
            return Path + (IsAppData ? " (application data)" : " (portable)");
        }
    }

    // Takes the settings folder's lock for a command that changes the thread list (add, remove), or for ctw watch
    internal static class SettingsFolderAccess {
        private static readonly Dictionary<string, string> _holderNames = new Dictionary<string, string>(StringComparer.Ordinal) {
            { SettingsFolderLockHolder.WinForms, "Chan Thread Watch" },
            { SettingsFolderLockHolder.Cli, "Another ctw command" },
            { SettingsFolderLockHolder.Watch, "ctw watch" },
            { SettingsFolderLockHolder.Service, "The Chan Thread Watch service" }
        };

        // What the user is told when ctw watch holds the lock
        public const string StopWatchAdvice = "Stop it first (Ctrl+C where it runs), then try again.";

        // verb is what the command does to the thread ("add", "remove"), for the message. Waits as the app does
        // for a program that is just closing. Throws CliException naming the holder if another program keeps the
        // lock, if the app runs with the folder on this computer (its mutex, which also a window started without
        // the lock and versions before 1.40.0 hold), or if the lock file can't be written.
        public static SettingsFolderLock Lock(string folder, string verb) {
            return Lock(folder, SettingsFolderLockHolder.Cli, "Close it, or " + verb + " the thread in the app.");
        }

        // As Lock, for ctw watch, which keeps the lock until it stops
        public static SettingsFolderLock LockForWatch(string folder) {
            return Lock(folder, SettingsFolderLockHolder.Watch, "Close it, then start ctw watch again.");
        }

        // appAdvice is what the message advises when the app uses the folder
        private static SettingsFolderLock Lock(string folder, string hostKind, string appAdvice) {
            SettingsFolderLock folderLock;
            if (!TryAcquire(folder, hostKind, out folderLock)) throw CreateHeldException(folder, appAdvice);
            if (!IsAppRunningOnThisComputer(folder)) return folderLock;
            folderLock.Dispose();
            throw new CliException("Chan Thread Watch is running with the settings folder " + folder + " on this computer. " + appAdvice);
        }

        private static bool TryAcquire(string folder, string hostKind, out SettingsFolderLock folderLock) {
            try {
                return SettingsFolderLock.TryAcquire(folder, hostKind, SettingsFolderLock.DefaultWait, out folderLock);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                throw new CliException("Cannot write to the settings folder " + folder + ": " + ex.Message);
            }
        }

        private static CliException CreateHeldException(string folder, string appAdvice) {
            SettingsFolderLockHolder holder = SettingsFolderLock.ReadHolderAfterRecordIsWritten(folder);
            return new CliException(DescribeHolder(holder) + " is using the settings folder " + folder + ". " + GetAdvice(holder, appAdvice));
        }

        internal static bool IsAppRunningOnThisComputer(string folder) {
            return OperatingSystem.IsWindows() && AppMutexExists(folder);
        }

        // The window's mutex can deny the access an open asks for, which still means it exists
        [SupportedOSPlatform("windows")]
        private static bool AppMutexExists(string folder) {
            try {
                Mutex mutex;
                if (!Mutex.TryOpenExisting(SettingsFolderLock.GetAppMutexName(folder), out mutex)) return false;
                mutex.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException) {
                return true;
            }
        }

        // The record is written by another program, so its text is cleaned before it is shown
        internal static string DescribeHolder(SettingsFolderLockHolder holder) {
            if (holder == null) return "Another program";
            string name;
            if (!_holderNames.TryGetValue(holder.Kind, out name)) name = "Another program";
            return name + " (pid " + holder.ProcessId.ToString(CultureInfo.InvariantCulture) + " on " + ConsoleText.Clean(holder.MachineName) + ")";
        }

        private static string GetAdvice(SettingsFolderLockHolder holder, string appAdvice) {
            string kind = holder != null ? holder.Kind : null;
            if (kind == SettingsFolderLockHolder.WinForms) return appAdvice;
            return kind == SettingsFolderLockHolder.Watch ? StopWatchAdvice : "Wait until it has finished, then try again.";
        }
    }
}
