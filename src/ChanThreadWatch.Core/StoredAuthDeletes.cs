using System;
using System.Collections.Generic;
using System.IO;

namespace JDP {
    // Deletes the login store items that StoredAuth.ScheduleDelete scheduled, after the thread list
    // or the settings were saved. An item is kept, and stays scheduled, while a live thread or any
    // thread list or settings file of the settings folder still refers to it: the saved files,
    // threads.txt.bak, the copies kept aside (.corrupt-*) and leftovers of an atomic write. So a
    // restored backup keeps its logins. If a file can't be read, nothing is deleted this time.
    internal static class StoredAuthDeletes {
        public static void Flush(string settingsDirectory, IEnumerable<string> liveValues) {
            List<string> scheduled = StoredAuth.TakeScheduledDeletes();
            if (scheduled.Count == 0) return;
            string referencing = TryReadReferencingFiles(settingsDirectory);
            HashSet<string> live = new HashSet<string>(liveValues ?? new string[0], StringComparer.Ordinal);
            foreach (string stored in scheduled) {
                if (IsStillReferenced(stored, referencing, live)) {
                    StoredAuth.ScheduleDelete(stored);
                }
                else {
                    StoredAuth.Protector.Delete(stored);
                }
            }
        }

        // referencing is null when the files couldn't be read, which keeps every item
        private static bool IsStillReferenced(string stored, string referencing, HashSet<string> live) {
            return referencing == null || live.Contains(stored) || referencing.Contains(stored, StringComparison.Ordinal);
        }

        // Returns null if a file can't be read
        private static string TryReadReferencingFiles(string settingsDirectory) {
            try {
                return ReadReferencingFiles(settingsDirectory);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                Logger.Log("Saved logins that are no longer used were not deleted from the login store this time, because a file " +
                    "of the settings folder could not be read. " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        // Every file whose name starts with the thread list's or the settings' file name, in any case
        private static string ReadReferencingFiles(string settingsDirectory) {
            List<string> contents = new List<string>();
            foreach (string path in Directory.GetFiles(settingsDirectory)) {
                if (IsReferencingFile(Path.GetFileName(path))) contents.Add(File.ReadAllText(path));
            }
            return String.Join("\n", contents);
        }

        private static bool IsReferencingFile(string fileName) {
            return fileName.StartsWith(Settings.ThreadsFileName, StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith(Settings.SettingsFileName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
