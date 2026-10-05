using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace JDP.Tests {
    // Some tests need a second process (the settings folder lock, two programs saving the
    // thread list). They start this test assembly again with "dotnet <assembly> <command>":
    // the test runner loads the assembly without calling Main, so Main only runs then.
    // GenerateProgramFile is off in the project so this is the assembly's entry point.
    public static class ChildProcess {
        public const string HoldLockCommand = "hold-lock";
        public const string SaveThreadListCommand = "save-thread-list";

        public const string Acquired = "acquired";
        public const string Held = "held";
        public const int HeldExitCode = 3;

        public static int Main(string[] args) {
            if (args.Length == 3 && args[0] == HoldLockCommand) return HoldLock(args[1], args[2]);
            if (args.Length == 4 && args[0] == SaveThreadListCommand) {
                return SaveThreadList(args[1], args[2], Int32.Parse(args[3], CultureInfo.InvariantCulture));
            }
            Console.Error.WriteLine("Unknown command");
            return 2;
        }

        // Takes the lock as a program of the given kind. Prints "acquired" and keeps the lock
        // until a line is read or standard input closes, or prints "held" and exits with
        // HeldExitCode.
        private static int HoldLock(string folder, string hostKind) {
            SettingsFolderLock folderLock;
            if (!SettingsFolderLock.TryAcquire(folder, hostKind, out folderLock)) {
                Console.WriteLine(Held);
                return HeldExitCode;
            }
            using (folderLock) {
                Console.WriteLine(Acquired);
                Console.Out.Flush();
                Console.ReadLine();
            }
            return 0;
        }

        // Saves the writer's complete thread list the given number of times. A save that fails
        // (another program swapping the file in at the same moment, which Windows can report as
        // a sharing violation) is tried again, as the program's next save would.
        private static int SaveThreadList(string path, string writer, int saves) {
            string[] lines = ThreadListFile.Serialize(ThreadList(writer));
            int retries = 0;
            for (int i = 0; i < saves; i++) {
                retries += SaveWithRetries(path, lines);
            }
            Console.WriteLine(retries.ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        private static int SaveWithRetries(string path, string[] lines) {
            for (int attempt = 0; ; attempt++) {
                try {
                    TextFile.WriteAllLinesAtomic(path, lines);
                    return attempt;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 100) {
                    Thread.Sleep(5);
                }
            }
        }

        // Each writer's list has its own threads and length, so a mix of two lists or a
        // truncated one matches neither
        public static List<ThreadInfo> ThreadList(string writer) {
            int count = writer == "a" ? 40 : 65;
            List<ThreadInfo> threads = new List<ThreadInfo>();
            for (int i = 0; i < count; i++) {
                threads.Add(new ThreadInfo {
                    URL = "https://example.invalid/" + writer + "/thread/" + i.ToString(CultureInfo.InvariantCulture),
                    Description = "Writer " + writer + " thread " + i.ToString(CultureInfo.InvariantCulture),
                    ExtraData = new WatcherExtraData { AddedOn = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) }
                });
            }
            return threads;
        }

        // Starts this assembly with the arguments, with standard input and output redirected
        public static Process Start(params string[] args) {
            ProcessStartInfo startInfo = new ProcessStartInfo(GetDotnetPath()) {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(typeof(ChildProcess).Assembly.Location);
            foreach (string arg in args) {
                startInfo.ArgumentList.Add(arg);
            }
            return Process.Start(startInfo) ?? throw new InvalidOperationException("The child process did not start.");
        }

        // The dotnet host that runs the tests: DOTNET_HOST_PATH when the SDK set it, otherwise
        // the one in the root of the running runtime's install (shared/Microsoft.NETCore.App/<version>)
        private static string GetDotnetPath() {
            string hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!String.IsNullOrEmpty(hostPath) && File.Exists(hostPath)) return hostPath;
            string root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
            string dotnet = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (!File.Exists(dotnet)) throw new FileNotFoundException("The dotnet host was not found.", dotnet);
            return dotnet;
        }

        // The next line the process prints; fails if none comes in time
        public static string ReadLine(Process process, TimeSpan timeout) {
            Task<string> line = process.StandardOutput.ReadLineAsync();
            if (!line.Wait(timeout)) throw new TimeoutException("The child process printed nothing in time.");
            return line.Result;
        }

        // For a finally block: ends the process if it is still running
        public static void KillIfRunning(Process process) {
            try {
                if (!process.HasExited) process.Kill(true);
            }
            catch (InvalidOperationException) {
                // Exited meanwhile
            }
        }

        // Waits for the process to exit and returns its exit code; kills it on a timeout
        public static int WaitForExit(Process process, TimeSpan timeout) {
            if (!process.WaitForExit((int)timeout.TotalMilliseconds)) {
                process.Kill(true);
                throw new TimeoutException("The child process did not exit in time.");
            }
            return process.ExitCode;
        }
    }
}
