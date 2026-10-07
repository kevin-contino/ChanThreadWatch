using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using JDP.Api;

namespace JDP.Cli {
    // ctw api-pair (MP-7b): pairs a browser extension with the local API. It makes a pairing code (ApiPairingFile, which
    // saves only its salt and stretched key in api-pairing.txt), prints the code once on stdout, and waits until a
    // browser pairs with it, the code ends, or Ctrl+C. The API that answers the extension runs in the app or in ctw
    // watch, another process, which marks the file paired or deletes it; this command polls the file and deletes it
    // at the end, only while the file still holds its own code. --list prints the paired browsers and --remove unpairs
    // one (ApiClientStore); neither makes a code. Like api-token, it takes no lock and loads no settings, so it works
    // while the app or ctw watch runs.
    internal static class ApiPairCommand {
        internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);
        internal static TimeSpan PollInterval { get; set; } = DefaultPollInterval;

        // The clock for the code's expiry. The tests put a fake one in its place.
        internal static Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

        // Test only: runs before each look at the pairing file
        internal static Action PollingForTesting { get; set; }

        private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
        private const string AgainAdvice = " Run 'ctw api-pair' again for a new code.";
        private const string ApiEnabledName = "ApiEnabled";

        private static readonly Dictionary<PairingEnd, string> _endMessages = new Dictionary<PairingEnd, string> {
            { PairingEnd.Expired, "The code expired before a browser extension paired with it." + AgainAdvice },
            { PairingEnd.Ended, "The code no longer works, and no browser extension paired with it: wrong codes used up its tries, or " +
                ApiPairingFile.FileName + " was deleted." + AgainAdvice },
            { PairingEnd.Replaced, "A newer code from another program that makes codes, such as the app or another 'ctw api-pair', replaced this one. " +
                "Use the newer code." },
            { PairingEnd.Changed, ApiPairingFile.FileName + " was changed by another program, so the code no longer works." + AgainAdvice },
            { PairingEnd.Cancelled, "Cancelled. The code no longer works." }
        };

        public static int Run(CliContext context) {
            // The API does not run as root (ApiServer), and as root the files' mode would prove nothing
            if (OwnerOnlyFile.IsRootOnUnix()) throw new CliException("ctw api-pair does not run as root, since the local API does not. Run it as the user that runs ctw watch.");
            CliCommand command = context.Command;
            if (command.ListPaired) return List(context);
            if (command.UnpairFamily != null) return Remove(context, command.UnpairFamily);
            return Pair(context);
        }

        // One line per paired browser: its family and when it paired (UTC), separated by a tab; never a hash
        private static int List(CliContext context) {
            foreach (ApiClient client in ReadClients(new ApiClientStore(context.GetSettingsFolder().Path))) {
                context.Output.WriteLine(client.Family + "\t" + client.PairedAt.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture));
            }
            return CliApp.ExitSuccess;
        }

        // The family is one of ApiPairing.Families (CliArguments checks it)
        private static int Remove(CliContext context, string family) {
            ApiClientStore store = new ApiClientStore(context.GetSettingsFolder().Path);
            ReadClients(store);
            if (!RemoveClient(store, family)) throw new CliException("The " + DisplayName(family) + " extension is not paired.");
            context.Output.WriteLine("Unpaired the " + DisplayName(family) + " extension. Its token no longer works.");
            return CliApp.ExitSuccess;
        }

        // A file that can't be used is an error: no paired browser can connect meanwhile. ApiClientStore.Read does not
        // say whether it could not be read or holds content that is not trusted, so the message names both.
        private static IReadOnlyList<ApiClient> ReadClients(ApiClientStore store) {
            IReadOnlyList<ApiClient> clients = store.Read();
            if (clients == null) {
                throw new CliException(ApiClientStore.FileName + " could not be read, or it is damaged, a link, or others can read it, so no browser extension " +
                    "can connect.");
            }
            return clients;
        }

        private static bool RemoveClient(ApiClientStore store, string family) {
            try {
                return store.Remove(family);
            }
            catch (ApiTokenException ex) {
                throw new CliException(ex.Message);
            }
        }

        internal static string DisplayName(string family) {
            return family == ApiPairing.ChromeFamily ? "Chrome" : "Firefox";
        }

        // The stop (Ctrl+C and the other signals, or the tests' token) is in place before the code is made, and until
        // the code's file is deleted
        private static int Pair(CliContext context) {
            SettingsFolder folder = context.GetSettingsFolder();
            // The app creates its folder in the application data the same way
            if (folder.IsAppData) Directory.CreateDirectory(folder.Path);
            // A note or warning that can't be written (a closed pipe) is dropped
            WatchStatusOutput notes = new WatchStatusOutput(context.Error);
            WarnAboutTheApi(notes, folder.Path);
            PairingWait wait = new PairingWait(folder.Path);
            PairingEnd end;
            using (PairingStop stop = PairingStop.Create(context.StopToken, wait)) {
                try {
                    end = MakeCodeAndWait(context, wait, stop.Token, notes);
                }
                finally {
                    EndCode(wait, notes);
                }
            }
            return Report(context, end, wait.PairedFamily);
        }

        // A stop while the code is made (PBKDF2, a moment of work) ends it before it is shown
        private static PairingEnd MakeCodeAndWait(CliContext context, PairingWait wait, CancellationToken stop, WatchStatusOutput notes) {
            if (stop.IsCancellationRequested) return PairingEnd.Cancelled;
            wait.MakeCode();
            if (stop.IsCancellationRequested) return PairingEnd.Cancelled;
            WriteCode(context, wait.Code.Code);
            notes.WriteLine("ctw: note: Enter this code in the extension's options within " + ApiPolicy.PairingCodeLifetime.TotalMinutes.ToString(CultureInfo.InvariantCulture) +
                " minutes. Press Ctrl+C to cancel.");
            return wait.Wait(stop);
        }

        // The code works only while the app or ctw watch runs the API, which needs ApiEnabled=1 and a token; the code
        // is made anyway, since the API may start within its 5 minutes
        private static void WarnAboutTheApi(WatchStatusOutput notes, string folder) {
            if (!IsApiEnabled(ReadSettings(Path.Combine(folder, Settings.SettingsFileName)))) {
                notes.WriteLine("ctw: warning: The local API is off: " + Settings.SettingsFileName + " does not have ApiEnabled=1. The code works only while the app " +
                    "or ctw watch runs the local API.");
            }
            if (!File.Exists(new ApiTokenStore(folder).Path)) {
                notes.WriteLine("ctw: warning: There is no " + ApiTokenStore.FileName + ", and ctw watch does not start the local API without a token. Run 'ctw api-token' first.");
            }
        }

        // A settings file that can't be read fails the command; a missing one is no settings
        private static string[] ReadSettings(string path) {
            try {
                return SharedFile.ReadAllLines(path) ?? new string[0];
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                throw new CliException(Settings.SettingsFileName + " could not be read, so ctw does not make a pairing code: " + ex.Message);
            }
        }

        // As Settings reads settings.txt (Settings.Load): a line is "name=value" split at its first '=', names match
        // in any case, the first line of a name wins, and ApiEnabled is on only for the exact value "1"
        internal static bool IsApiEnabled(string[] lines) {
            string line = lines.FirstOrDefault(IsApiEnabledLine);
            return line != null && line.Substring(line.IndexOf('=') + 1) == "1";
        }

        private static bool IsApiEnabledLine(string line) {
            int pos = line.IndexOf('=');
            return pos != -1 && String.Equals(line.Substring(0, pos), ApiEnabledName, StringComparison.OrdinalIgnoreCase);
        }

        // The code is never written anywhere else; one that can't be shown is ended at once (by the caller)
        private static void WriteCode(CliContext context, string code) {
            try {
                context.Output.WriteLine(code);
                context.Output.Flush();
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) {
                throw new CliException("The pairing code could not be written to stdout, so it was cancelled.");
            }
        }

        // A file that can't be deleted leaves a code that a browser can still use until it expires
        private static void EndCode(PairingWait wait, WatchStatusOutput notes) {
            if (wait.End()) return;
            notes.WriteLine("ctw: warning: " + ApiPairingFile.FileName + " could not be deleted, so the code still works until " +
                wait.Code.Expires.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture) + ".");
        }

        private static int Report(CliContext context, PairingEnd end, string family) {
            if (end != PairingEnd.Paired) throw new CliException(_endMessages[end]);
            context.Output.WriteLine("Paired with the " + DisplayName(family) + " extension");
            return CliApp.ExitSuccess;
        }
    }

    // How a wait for a pairing ended
    internal enum PairingEnd {
        Paired,
        // The code's 5 minutes passed
        Expired,
        // The file is gone before the code's expiry (the API burned the code, or it was deleted), and no browser paired
        // since the code was made
        Ended,
        // The file holds another code
        Replaced,
        // The file is there but not trusted (a link, access for others, content that does not parse)
        Changed,
        Cancelled
    }

    // Ctrl+C (SIGINT), Ctrl+Break or Ctrl+\ (SIGQUIT), SIGTERM and SIGHUP stop ctw api-pair: the first one stops the
    // wait and ends the code in the handler itself (Windows ends the process soon after its console window is closed,
    // whatever the handler does), a second one ends the process at once, as for ctw watch (StopSignals). The tests give
    // a token in place of the signals.
    internal sealed class PairingStop : IDisposable {
        private static readonly PosixSignal[] _stopSignals = { PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGTERM, PosixSignal.SIGHUP };

        private readonly CancellationTokenSource _stop;
        private readonly PairingWait _wait;
        private readonly List<PosixSignalRegistration> _registrations = new List<PosixSignalRegistration>();
        private int _requested;

        // Not registered for any signal (the tests call Handle)
        internal PairingStop(CancellationTokenSource stop, PairingWait wait) {
            _stop = stop;
            _wait = wait;
        }

        public static PairingStop Create(CancellationToken? testToken, PairingWait wait) {
            if (testToken.HasValue) return new PairingStop(CancellationTokenSource.CreateLinkedTokenSource(testToken.Value), wait);
            PairingStop stop = new PairingStop(new CancellationTokenSource(), wait);
            foreach (PosixSignal signal in _stopSignals) {
                stop._registrations.Add(PosixSignalRegistration.Create(signal, context => context.Cancel = stop.Handle()));
            }
            return stop;
        }

        public CancellationToken Token {
            get { return _stop.Token; }
        }

        // Returns whether the signal is canceled. The wait is stopped first, so it never reads the deleted file as a
        // code that ended; the code is then ended if it is made (a code made later is ended by the command, which sees
        // the stop before it shows the code).
        internal bool Handle() {
            if (Interlocked.Exchange(ref _requested, 1) != 0) return false;
            try {
                _stop.Cancel();
            }
            catch (ObjectDisposedException) {
                return false;
            }
            _wait.End();
            return true;
        }

        // Marked as requested first, so a handler that runs late neither cancels the disposed source nor ends a code
        public void Dispose() {
            Interlocked.Exchange(ref _requested, 1);
            foreach (PosixSignalRegistration registration in _registrations) {
                registration.Dispose();
            }
            _stop.Dispose();
        }
    }

    // One code and the wait for its result. Each look reads api-pairing.txt and compares its id with the code's, as
    // ApiPairingFile asks of a creator. A file that is gone (or still unreadable at the code's expiry) may still follow
    // a pairing: the API can save the browser's token and then find the file gone (the design accepts that window), so
    // the paired browsers are compared with the ones taken just after the code was made, before it was shown. A file
    // that holds another code is reported as replaced, although a pairing with this code may have been saved just
    // before (also accepted by the design).
    internal sealed class PairingWait {
        private readonly ApiPairingFile _file;
        private readonly ApiClientStore _clients;
        private HashSet<string> _hashesBefore;
        // Set once the code is made; read by a signal handler on another thread
        private volatile ApiPairingCode _code;

        public PairingWait(string settingsFolder) {
            _file = new ApiPairingFile(settingsFolder);
            _clients = new ApiClientStore(settingsFolder);
        }

        // Null until MakeCode returns
        public ApiPairingCode Code {
            get { return _code; }
        }

        // The browser that paired, once the wait ended with Paired
        public string PairedFamily { get; private set; }

        // Makes the code (PBKDF2, a moment of work), then takes the paired browsers: no browser can know the code yet
        public void MakeCode() {
            try {
                _code = _file.Create(ApiPairCommand.UtcNow());
            }
            catch (ApiTokenException ex) {
                throw new CliException(ex.Message);
            }
            _hashesBefore = PairedHashes();
        }

        public PairingEnd Wait(CancellationToken stop) {
            PairingEnd? end = null;
            while (!end.HasValue) {
                ApiPairCommand.PollingForTesting?.Invoke();
                end = Step(stop);
            }
            return end.Value;
        }

        private PairingEnd? Step(CancellationToken stop) {
            PairingEnd? end = WithStop(Look(), stop);
            if (end.HasValue || !stop.WaitHandle.WaitOne(ApiPairCommand.PollInterval)) return end;
            return PairingEnd.Cancelled;
        }

        // A stop wins over anything but a pairing: the stop deletes the file, which a look must not report as ended
        private static PairingEnd? WithStop(PairingEnd? end, CancellationToken stop) {
            return stop.IsCancellationRequested && end != PairingEnd.Paired ? PairingEnd.Cancelled : end;
        }

        // Null while the code is pending in the file (or the file can't be read just now)
        private PairingEnd? Look() {
            bool refused, unreadable;
            ApiPendingPairing pending = _file.Read(out refused, out unreadable);
            if (pending == null) return LookWithoutCode(refused, unreadable);
            if (pending.Id != Code.Id) return PairingEnd.Replaced;
            if (pending.PairedFamily != null) return Paired(pending.PairedFamily);
            return IsExpired() ? PairingEnd.Expired : null;
        }

        private bool IsExpired() {
            return ApiPairCommand.UtcNow() >= Code.Expires;
        }

        // A file that can't be read is waited for until the code's expiry, which then looks for a pairing as for a
        // file that is gone
        private PairingEnd? LookWithoutCode(bool refused, bool unreadable) {
            if (unreadable) return IsExpired() ? LookForNewClient() : null;
            return refused ? PairingEnd.Changed : LookForNewClient();
        }

        // A browser that paired since the code was made is a pairing, else the code ended. Without the paired browsers
        // from then (that file could not be used) no browser counts as new.
        private PairingEnd? LookForNewClient() {
            IReadOnlyList<ApiClient> clients = _clients.Read();
            if (clients == null) return LookWithoutClients();
            ApiClient added = clients.FirstOrDefault(client => _hashesBefore != null && !_hashesBefore.Contains(Convert.ToHexString(client.Hash)));
            if (added != null) return Paired(added.Family);
            return IsExpired() ? PairingEnd.Expired : PairingEnd.Ended;
        }

        // api-clients.txt can't be used just now: the wait goes on until the code's expiry
        private PairingEnd? LookWithoutClients() {
            return IsExpired() ? PairingEnd.Expired : null;
        }

        private PairingEnd Paired(string family) {
            PairedFamily = family;
            return PairingEnd.Paired;
        }

        // Null when api-clients.txt can't be used
        private HashSet<string> PairedHashes() {
            IReadOnlyList<ApiClient> clients = _clients.Read();
            return clients != null ? new HashSet<string>(clients.Select(client => Convert.ToHexString(client.Hash)), StringComparer.Ordinal) : null;
        }

        // Deletes the file only while it holds this code; true when there is no code yet or the file is gone or holds
        // another code, false when it could not be deleted
        public bool End() {
            ApiPairingCode code = _code;
            return code == null || _file.Delete(code.Id);
        }
    }
}
