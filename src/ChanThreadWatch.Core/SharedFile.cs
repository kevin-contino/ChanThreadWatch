using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace JDP {
    // Reads a file of the settings folder while another program may swap in a new version of it. Used by ctw and by
    // the load of api-threads.txt (WatchSession).
    internal static class SharedFile {
        private const int ReadAttempts = 5;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

        // Returns null if the file (or its folder) does not exist. Shares writing and deleting, so the app can swap
        // in a new file while this reads; the swap is atomic (TextFile.WriteAllLinesAtomic), so this reads either
        // the old or the new file. A file that is missing for a moment or locked by another program is read again
        // a few times (about 200 ms in all); an error that remains is thrown.
        public static string[] ReadAllLines(string path) {
            for (int attempt = 1; ; attempt++) {
                try {
                    return ReadOnce(path);
                }
                catch (IOException ex) when (attempt < ReadAttempts && IsTransient(ex)) {
                    Thread.Sleep(RetryDelay);
                }
                catch (IOException ex) when (IsMissing(ex)) {
                    return null;
                }
            }
        }

        // As ReadAllLines, but a file that is not there costs no retries (null at once). Anything else in its place (a
        // folder) is read, and fails as a file that can't be read.
        public static string[] ReadAllLinesIfPresent(string path) {
            return File.Exists(path) || Directory.Exists(path) ? ReadAllLines(path) : null;
        }

        // Missing, or open in another program without sharing (a sharing or lock violation on Windows, flock's
        // EWOULDBLOCK on Unix, as for the settings folder's lock)
        private static bool IsTransient(IOException ex) {
            return IsMissing(ex) || SettingsFolderLock.IsHeldByAnother(ex);
        }

        private static bool IsMissing(IOException ex) {
            return ex is FileNotFoundException || ex is DirectoryNotFoundException;
        }

        private static string[] ReadOnce(string path) {
            List<string> lines = new List<string>();
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(fs, Encoding.UTF8, true)) {
                string line;
                while ((line = reader.ReadLine()) != null) {
                    lines.Add(line);
                }
            }
            return lines.ToArray();
        }
    }
}
