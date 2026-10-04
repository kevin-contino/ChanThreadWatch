using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace JDP {
    // Writes or reads the saved logins of a settings folder:
    //   write <folder>  settings.txt with PageAuth and ImageAuth, and threads.txt with one thread that has both logins
    //   read <folder>   exits 0 only if all four logins decrypt to the expected sterile values
    //
    // The net48 build uses the same StoredAuth.cs as Core and writes the file layout the 4.8 app wrote; the
    // net10.0 build uses Core's Settings and ThreadListFile. The logins are sterile: no real account.
    internal static class Program {
        private static readonly string[] _names = { "settings.txt PageAuth", "settings.txt ImageAuth", "threads.txt PageAuth", "threads.txt ImageAuth" };
        // Non-ASCII text and a ':' in the password check the UTF-8 round trip
        private static readonly string[] _logins = { "ctw-check-page:päss:1", "ctw-check-image:日本", "ctw-check-thread-page:pw3", "ctw-check-thread-image:pw4" };
        private const string ThreadURL = "https://boards.4chan.org/b/thread/123";

        private static int Main(string[] args) {
            if (args.Length != 2) return Usage();
            string settingsPath = Path.Combine(args[1], "settings.txt");
            string threadsPath = Path.Combine(args[1], "threads.txt");
            Console.WriteLine(RuntimeInformation.FrameworkDescription + ": " + args[0] + " " + args[1]);
            if (args[0] == "write") return WriteAndCheck(settingsPath, threadsPath);
            if (args[0] == "read") return Check(Read(settingsPath, threadsPath)) ? 0 : 1;
            return Usage();
        }

        private static int WriteAndCheck(string settingsPath, string threadsPath) {
            Write(settingsPath, threadsPath);
            return File.Exists(settingsPath) && File.Exists(threadsPath) ? 0 : 1;
        }

        private static int Usage() {
            Console.Error.WriteLine("usage: StoredAuthCheck write|read <folder>");
            return 2;
        }

        private static bool Check(string[] actual) {
            bool ok = true;
            for (int i = 0; i < _logins.Length; i++) {
                bool match = actual[i] == _logins[i];
                // Never prints a login, decrypted or not
                Console.WriteLine(_names[i] + ": " + (match ? "decrypted to the expected login" : "MISMATCH"));
                ok &= match;
            }
            return ok;
        }

#if NETFRAMEWORK
        // The layout Settings.Save and ThreadListFile.Serialize (file version 4) write
        private static void Write(string settingsPath, string threadsPath) {
            File.WriteAllLines(settingsPath, new[] { "PageAuth=" + StoredAuth.Protect(_logins[0]), "ImageAuth=" + StoredAuth.Protect(_logins[1]) });
            File.WriteAllLines(threadsPath, new[] {
                "4", ThreadURL, StoredAuth.Protect(_logins[2]), StoredAuth.Protect(_logins[3]), "600", "0", "", "", "", "0", "", "", "", "0"
            });
        }

        private static string[] Read(string settingsPath, string threadsPath) {
            Dictionary<string, string> settings = ReadSettings(settingsPath);
            string[] threads = File.ReadAllLines(threadsPath);
            if (threads[0] != "4" || threads[1] != ThreadURL) throw new FormatException("threads.txt is not the expected version 4 file");
            return new[] {
                StoredAuth.Unprotect(settings["PageAuth"]), StoredAuth.Unprotect(settings["ImageAuth"]), StoredAuth.Unprotect(threads[2]), StoredAuth.Unprotect(threads[3])
            };
        }

        // As Settings.Load: "name=value" lines, and the first of a duplicate name wins
        private static Dictionary<string, string> ReadSettings(string path) {
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(path)) {
                int pos = line.IndexOf('=');
                if (pos != -1 && !settings.ContainsKey(line.Substring(0, pos))) settings.Add(line.Substring(0, pos), line.Substring(pos + 1));
            }
            return settings;
        }
#else
        private static void Write(string settingsPath, string threadsPath) {
            UseExeFolderForLog();
            Settings.PageAuth = _logins[0];
            Settings.ImageAuth = _logins[1];
            Settings.Save(settingsPath);
            var thread = new ThreadInfo { URL = ThreadURL, PageAuth = _logins[2], ImageAuth = _logins[3], CheckIntervalSeconds = 600, ExtraData = new WatcherExtraData() };
            File.WriteAllLines(threadsPath, ThreadListFile.Serialize(new[] { thread }));
        }

        private static string[] Read(string settingsPath, string threadsPath) {
            UseExeFolderForLog();
            Settings.Load(settingsPath);
            ThreadInfo thread = ThreadListFile.Parse(File.ReadAllLines(threadsPath)).Threads[0];
            return new[] { Settings.PageAuth, Settings.ImageAuth, thread.PageAuth, thread.ImageAuth };
        }

        // Core logs errors to the settings folder; this keeps them next to the tool, never in the user's AppData
        private static void UseExeFolderForLog() {
            Settings.UseExeDirectoryForSettings = true;
        }
#endif
    }

#if NETFRAMEWORK
    // StoredAuth and TextFile log through Logger, which is part of Core
    internal static class Logger {
        public static void Log(string message) {
            Console.Error.WriteLine(message);
        }
    }
#endif
}
