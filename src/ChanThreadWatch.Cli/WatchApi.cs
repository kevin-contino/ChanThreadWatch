using System;
using System.Threading.Tasks;
using JDP.Api;

namespace JDP.Cli {
    // The local API (MP-7a) in ctw watch, with the app's settings: on only with ApiEnabled=1 in settings.txt, on
    // 127.0.0.1 at ApiPort, with the token whose hash is in api-token.txt (ctw api-token). HeadlessWatch starts it
    // once the thread list is loaded, so no request sees a list that is still loading, and stops it first when it
    // stops, before the watchers stop and the owner thread ends. A request's work runs on the owner thread
    // (HeadlessWatch.Post). A start that fails is a warning: ctw watch goes on without the API, with the same exit code.
    internal sealed class WatchApi {
        // How long the stop waits for the requests that are running; pending ones get 503 at once. The tests make it
        // longer. Declared first: static fields are set in order.
        internal static readonly TimeSpan DefaultStopWait = TimeSpan.FromSeconds(2);
        internal static TimeSpan StopWait { get; set; } = DefaultStopWait;

        private const string GoesOnWithout = "ctw watch goes on without the local API.";

        // Test only: gives the port to listen on in place of the one from the settings, so a test never binds the
        // default port
        internal static Func<int, int> ListenPortForTesting { get; set; }

        private readonly ApiServer _server;

        private WatchApi(ApiServer server) {
            _server = server;
        }

        internal ApiServer Server {
            get { return _server; }
        }

        // Null when the API is off (nothing is made) or did not start (a warning is written)
        public static WatchApi Start(WatchSession session, Action<Action> post, Func<bool> isExiting, string settingsFolder, WatchStatusOutput output,
            WatchStatusOutput error) {
            if (!IsEnabled(error)) return null;
            int port = GetPort(error);
            ApiPolicy policy = new ApiPolicy { IsExiting = isExiting };
            ApiThreadService threads = new ApiThreadService(session, new OwnerThreadDispatcher(post), url => NewThread.Create(url, null, null), policy);
            ApiServer server = new ApiServer(threads, new ApiTokenStore(settingsFolder));
            try {
                port = server.StartAsync(port).GetAwaiter().GetResult();
                output.WriteLine("Local API listening on http://127.0.0.1:" + port + "/api/v1/");
                return new WatchApi(server);
            }
            catch (Exception ex) when (ApiStartFailure.IsExpected(ex)) {
                error.WriteLine("ctw: warning: " + DescribeStartFailure(ex));
                LogStartFailure(ex);
                return null;
            }
        }

        // Only "1" is on; any other value but "0" is most likely a typo, so it is named
        private static bool IsEnabled(WatchStatusOutput error) {
            if (Settings.ApiEnabled == true) return true;
            if (Settings.ApiEnabledIsNotValid) {
                error.WriteLine("ctw: warning: ApiEnabled in " + Settings.SettingsFileName + " is neither 0 nor 1, so the local API stays off. Only ApiEnabled=1 turns it on.");
            }
            return false;
        }

        // Settings.ApiPort gives the default port for a value that is not valid
        private static int GetPort(WatchStatusOutput error) {
            if (Settings.ApiPortIsNotValid) {
                error.WriteLine("ctw: warning: ApiPort in " + Settings.SettingsFileName + " is not a port from " + Settings.MinimumApiPort + " to " + Settings.MaximumApiPort +
                    ", so the local API uses port " + Settings.DefaultApiPort + ".");
            }
            int port = Settings.ApiPort.Value;
            return ListenPortForTesting != null ? ListenPortForTesting(port) : port;
        }

        // Never the token or its hash
        internal static string DescribeStartFailure(Exception ex) {
            ApiStartException start = ex as ApiStartException;
            if (start == null) return "The local API could not start (see " + Settings.LogFileName + "). " + GoesOnWithout;
            if (start.Error == ApiStartError.TokenMissing) {
                return "The local API is on (ApiEnabled=1 in " + Settings.SettingsFileName + "), but " + ApiTokenStore.FileName + " is missing, not valid, or " +
                    "can be read by others, so the API does not start. Run 'ctw api-token' to make a token, then start ctw watch again.";
            }
            string hint = start.Error == ApiStartError.BindFailed ? " The port may be reserved by the system; set another ApiPort in " + Settings.SettingsFileName + "." : "";
            return start.Message + hint + " " + GoesOnWithout;
        }

        // The type only, as the server logs (ApiLogging): an exception's message can hold a path
        private static void LogStartFailure(Exception ex) {
            string causeType = ApiStartFailure.GetCauseTypeName(ex);
            if (causeType != null) Logger.Log("Local API: ctw watch could not start it: " + causeType);
        }

        // Closes the dispatcher before it returns, so no work of a request runs on the owner thread after this; the
        // task ends once the port is free. Never throws.
        public Task StopAsync() {
            return _server.StopAsync(StopWait);
        }
    }
}
