using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace JDP.Cli {
    // ctw: lists, adds and removes the threads in the thread list (threads.txt) of the settings folder
    // Chan Thread Watch uses, watches them without a window (watch), and makes the local API's token (api-token,
    // see ApiTokenCommand). list, add and remove never download
    // anything and never read or write a saved login (see KeptStoredAuthProtector); watch uses saved logins as the
    // app does (see WatchCommand). add, remove and watch use the thread list only while they hold the settings
    // folder's lock (SettingsFolderLock) and the app's mutex for the folder is not there, so they refuse while the
    // app has the folder open on this computer.
    public static class CliApp {
        public const int ExitSuccess = 0;
        public const int ExitFailure = 1;
        public const int ExitUsage = 2;

        public const string Usage =
            "Usage: ctw <command> [options]\n" +
            "\n" +
            "Commands:\n" +
            "  list                 List the watched threads, one per line: URL, category and description, separated by tabs.\n" +
            "  add <url> [--description <text>] [--category <text>]\n" +
            "                       Add a thread with the app's default check interval, one-time download and auto-follow settings.\n" +
            "  remove <url>         Remove a thread from the list. Its downloaded files are kept.\n" +
            "  watch                Watch the threads in the list and download them as the app does, without a window,\n" +
            "                       until Ctrl+C, SIGTERM or SIGHUP. Saves the thread list every minute and when it stops.\n" +
            "                       Runs the local API on 127.0.0.1 when settings.txt has ApiEnabled=1.\n" +
            "  api-token            Make a new token for the local API and print it once. It replaces the previous token.\n" +
            "\n" +
            "Options:\n" +
            "  --help, -h           Show this help, or after a command, that command's usage.\n" +
            "  --version            Show the version.\n" +
            "  --                   Treat the arguments after it as values, not options.\n" +
            "\n" +
            "ctw uses the settings folder of Chan Thread Watch: the folder of ctw or the folder above it, if it holds\n" +
            "settings.txt (portable mode), otherwise the app's folder in your application data. add, remove and watch\n" +
            "refuse while Chan Thread Watch uses that folder on this computer, and the app refuses while ctw watch runs.\n" +
            "A window on another computer that was started with \"start anyway\" is not detected. api-token also works\n" +
            "while the app or ctw watch runs.\n" +
            "Exit codes: 0 success, 1 error, 2 invalid command line.\n";

        private static readonly Dictionary<CliCommandKind, Func<CliContext, int>> _handlers = new Dictionary<CliCommandKind, Func<CliContext, int>> {
            { CliCommandKind.Help, ShowHelp },
            { CliCommandKind.CommandHelp, ShowCommandHelp },
            { CliCommandKind.MissingCommand, ShowUsageError },
            { CliCommandKind.Version, ShowVersion },
            { CliCommandKind.List, ThreadListCommands.List },
            { CliCommandKind.Add, ThreadListCommands.Add },
            { CliCommandKind.Remove, ThreadListCommands.Remove },
            { CliCommandKind.Watch, WatchCommand.Run },
            { CliCommandKind.ApiToken, ApiTokenCommand.Run }
        };

        public static int Run(string[] args, TextWriter output, TextWriter error) {
            return Run(args, output, error, null);
        }

        // settingsFolder is used in place of the app's settings folder (for the tests); null for the app's
        internal static int Run(string[] args, TextWriter output, TextWriter error, string settingsFolder) {
            return Run(args, output, error, settingsFolder, null);
        }

        // stopToken stops watch in place of Ctrl+C and SIGTERM (for the tests); null to stop on those
        internal static int Run(string[] args, TextWriter output, TextWriter error, string settingsFolder, CancellationToken? stopToken) {
            if (args == null) throw new ArgumentNullException(nameof(args));
            InitializeHost();
            CliCommand command;
            string usageError;
            if (!CliArguments.TryParse(args, out command, out usageError)) {
                error.WriteLine("ctw: " + usageError + " Run 'ctw --help' for usage.");
                return ExitUsage;
            }
            SettingsFolder folder = settingsFolder != null ? new SettingsFolder(settingsFolder, false) : null;
            return Execute(new CliContext(command, output, error, folder) { StopToken = stopToken });
        }

        // Core reports the host's version (General.Version), and no saved login is ever read or written (watch puts
        // the app's backend in place for itself)
        private static void InitializeHost() {
            General.HostVersion = typeof(CliApp).Assembly.GetName().Version;
            StoredAuth.Protector = new KeptStoredAuthProtector();
        }

        private static int Execute(CliContext context) {
            try {
                return _handlers[context.Command.Kind](context);
            }
            catch (Exception ex) when (IsReported(ex)) {
                context.Error.WriteLine("ctw: " + ConsoleText.Clean(ex.Message));
                return ExitFailure;
            }
        }

        // Failures a command reports as one line; anything else is a bug and ends the program with its stack trace
        private static bool IsReported(Exception ex) {
            return ex is CliException || ex is IOException || ex is UnauthorizedAccessException;
        }

        private static int ShowHelp(CliContext context) {
            context.Output.Write(Usage);
            context.Output.WriteLine("Settings folder: " + ConsoleText.Clean(context.GetSettingsFolder().Describe()));
            return ExitSuccess;
        }

        private static int ShowCommandHelp(CliContext context) {
            context.Output.WriteLine("Usage: " + context.Command.HelpText);
            return ExitSuccess;
        }

        private static int ShowUsageError(CliContext context) {
            context.Error.Write(Usage);
            return ExitUsage;
        }

        private static int ShowVersion(CliContext context) {
            context.Output.WriteLine(General.Version);
            return ExitSuccess;
        }
    }

    // A failure the command reports to the user; the message is one line
    internal sealed class CliException : Exception {
        public CliException(string message) : base(message) {
        }
    }

    internal sealed class CliContext {
        private readonly SettingsFolder _settingsFolder;

        public CliContext(CliCommand command, TextWriter output, TextWriter error, SettingsFolder settingsFolder) {
            Command = command;
            Output = output;
            Error = error;
            _settingsFolder = settingsFolder;
        }

        public CliCommand Command { get; }
        public TextWriter Output { get; }
        public TextWriter Error { get; }
        // Stops watch (for the tests); null to stop on Ctrl+C and SIGTERM
        public CancellationToken? StopToken { get; set; }

        // The folder the app uses, found from where ctw is (see SettingsFolder), unless the tests gave another
        public SettingsFolder GetSettingsFolder() {
            return _settingsFolder ?? SettingsFolder.Find(Settings.ExeDirectory);
        }
    }

    internal static class ConsoleText {
        // Thread lists hold text from web pages (descriptions come from thread titles), so control characters,
        // such as terminal escape sequences, tabs and line breaks, are shown as spaces
        public static string Clean(string text) {
            if (text == null) return String.Empty;
            char[] chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++) {
                if (Char.IsControl(chars[i])) chars[i] = ' ';
            }
            return new String(chars);
        }

        // Everything up to the last '@' before the first '/' after the scheme (or before the first '/' of an
        // address without one) is a login ("name:password@"), which is never shown
        private static readonly Regex _urlLogin = new Regex("^([^/@]*://)?[^/]*@", RegexOptions.CultureInvariant);

        // For any URL ctw prints, also one that is not valid: without its login, then as Clean
        public static string CleanUrl(string url) {
            return Clean(url != null ? _urlLogin.Replace(url, "$1") : null);
        }
    }
}
