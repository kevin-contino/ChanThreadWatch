using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace JDP.Api {
    // The result of an add: the new thread, or an error
    internal sealed class ApiAddResult {
        private ApiAddResult(ApiThread thread, ApiError error) {
            Thread = thread;
            Error = error;
        }

        public ApiThread Thread { get; }
        public ApiError Error { get; }

        public static ApiAddResult Added(ApiThread thread) {
            return new ApiAddResult(thread, null);
        }

        public static ApiAddResult Failed(ApiError error) {
            return new ApiAddResult(null, error);
        }
    }

    // The thread operations of the API, independent of the host. What can be checked without the session runs on the
    // request's thread; the session is only touched on the owner thread, in one work item per request.
    internal sealed class ApiThreadService {
        private readonly WatchSession _session;
        private readonly OwnerThreadDispatcher _dispatcher;
        private readonly Func<string, ThreadInfo> _newThread;
        private readonly ApiPolicy _policy;

        // newThread makes the thread to add from its URL, with the host's defaults (NewThread.Create, or the main
        // window's choices in the app), never with a login or a folder
        public ApiThreadService(WatchSession session, OwnerThreadDispatcher dispatcher, Func<string, ThreadInfo> newThread, ApiPolicy policy) {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _newThread = newThread ?? throw new ArgumentNullException(nameof(newThread));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        }

        public ApiPolicy Policy {
            get { return _policy; }
        }

        public OwnerThreadDispatcher Dispatcher {
            get { return _dispatcher; }
        }

        public Task<ApiThreadList> ListAsync() {
            return _dispatcher.RunAsync(() => new ApiThreadList { Threads = _session.ThreadWatchers.ConvertAll(ApiThreadProjection.From) }, _policy.OwnerThreadTimeout);
        }

        // The checks that need no lookup and no session; null when the URL passes them. Sets the URL to add.
        public ApiError ValidateUrl(string url, out Uri uri) {
            uri = ApiUrlRules.ParseThreadUrl(url, _policy.MaxUrlLength);
            if (uri == null) return ApiError.InvalidUrl;
            return ApiUrlRules.CheckHostName(uri, Settings.ApiAllowUnknownHosts == true);
        }

        // uri passed ValidateUrl. A request that the client cancels while it waits throws OperationCanceledException
        // and adds nothing.
        public async Task<ApiAddResult> AddAsync(Uri uri, CancellationToken cancellationToken) {
            ApiError error = await CheckResolvedAddressesAsync(uri, cancellationToken).ConfigureAwait(false) ?? CheckFreeSpace();
            if (error != null) return ApiAddResult.Failed(error);
            Uri named = await LookUpThreadNameAsync(uri, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            string url = named.AbsoluteUri;
            return await _dispatcher.RunAsync(() => AddOnOwnerThread(url), _policy.OwnerThreadTimeout).ConfigureAwait(false);
        }

        // The 4chan slug, when the settings use it and the URL lacks it: the watcher's constructor would download the
        // page on the owner thread to learn it, so it is downloaded here, on the request's thread, on the guarded
        // clients, within ThreadNameLookupTimeout. The URL the page names (its canonical link) is taken only if it is
        // the same thread with the same scheme, host and port, and passes every check the request's URL passed.
        // Otherwise, or when the download fails, takes too long or can't be read, the request's URL is added and the
        // thread is named by its number; the watcher never downloads the page for its name
        // (ThreadInfo.ThreadNameLookedUp). A canonical link to another host of the same site (boards.4channel.org to
        // boards.4chan.org) is not taken either: its page ID differs, so it would be another entry in the list.
        private async Task<Uri> LookUpThreadNameAsync(Uri uri, CancellationToken cancellationToken) {
            SiteHelper siteHelper = SiteHelpers.GetInstance(uri.Host);
            siteHelper.SetURL(uri.AbsoluteUri);
            if (!siteHelper.NeedsThreadNameLookup()) return uri;
            string page = await FetchThreadPageAsync(uri.AbsoluteUri, cancellationToken).ConfigureAwait(false);
            Uri named = page != null ? FindNamedUrl(siteHelper, page, uri) : null;
            return named != null && await CheckResolvedAddressesAsync(named, cancellationToken).ConfigureAwait(false) == null ? named : uri;
        }

        // A page the parser can't read gives no URL, as a failed download does
        private Uri FindNamedUrl(SiteHelper siteHelper, string page, Uri requested) {
            try {
                return ParseNamedUrl(siteHelper.GetURLWithThreadName(page), requested, siteHelper.GetPageID());
            }
            catch (Exception ex) {
                Logger.Log("Local API: the thread's page could not be read for its name, so it is added without it: " + ex.GetType().FullName);
                return null;
            }
        }

        // Null when the download failed or took too long; a request the client cancels throws OperationCanceledException
        private async Task<string> FetchThreadPageAsync(string url, CancellationToken cancellationToken) {
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
                timeout.CancelAfter(_policy.ThreadNameLookupTimeout);
                try {
                    return await _policy.FetchThreadPage(url, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested) {
                    Logger.Log("Local API: the thread's name could not be looked up, so it is added without it: " + ex.GetType().FullName);
                    return null;
                }
            }
        }

        // The URL the page names, if it passes the URL rules and the host rules, and is the same thread at the same
        // place
        private Uri ParseNamedUrl(string url, Uri requested, string pageID) {
            Uri named = ApiUrlRules.ParseThreadUrl(url, _policy.MaxUrlLength);
            if (named == null || ApiUrlRules.CheckHostName(named, Settings.ApiAllowUnknownHosts == true) != null) return null;
            return IsSamePlace(named, requested) && GetPageID(named.AbsoluteUri) == pageID ? named : null;
        }

        // The same scheme (never https to http), host and port
        private static bool IsSamePlace(Uri named, Uri requested) {
            return named.Scheme == requested.Scheme && String.Equals(named.Host, requested.Host, StringComparison.OrdinalIgnoreCase) && named.Port == requested.Port;
        }

        // Every address the host resolves to must be public. A known site whose name does not resolve is added (the
        // watcher reports the error); an unknown one is refused. This refuses a bad URL early, with an answer the
        // client sees; it is not what keeps the thread off local addresses. The thread is guarded (CreateThread), so
        // each of its watcher's connections, every redirect and meta refresh hop and a name that later resolves
        // elsewhere included, is checked against SSRFGuard's ranges when it connects.
        private async Task<ApiError> CheckResolvedAddressesAsync(Uri uri, CancellationToken cancellationToken) {
            IPAddress[] addresses = await ResolveAsync(uri.IdnHost, cancellationToken).ConfigureAwait(false);
            if (addresses == null) return SiteHelpers.IsKnownHost(uri.Host) ? null : ApiError.UnresolvableHost;
            return Array.Exists(addresses, SSRFGuard.IsBlockedAddress) ? ApiError.BlockedHost : null;
        }

        // Null when the lookup failed or found nothing
        private async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) {
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
                timeout.CancelAfter(_policy.ResolveTimeout);
                try {
                    return NullIfEmpty(await _policy.ResolveHost(host, timeout.Token).ConfigureAwait(false));
                }
                catch (Exception ex) when (IsLookupFailure(ex) && !cancellationToken.IsCancellationRequested) {
                    return null;
                }
            }
        }

        private static IPAddress[] NullIfEmpty(IPAddress[] addresses) {
            return addresses != null && addresses.Length != 0 ? addresses : null;
        }

        private static bool IsLookupFailure(Exception ex) {
            return ex is SocketException || ex is OperationCanceledException || ex is ArgumentException;
        }

        private ApiError CheckFreeSpace() {
            long? free = _policy.GetFreeSpace();
            return free.HasValue && free.Value < _policy.FreeSpaceFloorBytes ? ApiError.InsufficientStorage : null;
        }

        // On the owner thread: an existing thread is left as it is
        private ApiAddResult AddOnOwnerThread(string url) {
            string pageID = GetPageID(url);
            ApiError error = CheckCanAdd(url, pageID);
            if (error != null) return ApiAddResult.Failed(error);
            if (!_session.AddThread(CreateThread(url))) return ApiAddResult.Failed(ApiError.AlreadyWatched);
            _session.SaveThreadListPending = true;
            ThreadWatcher watcher;
            return _session.TryGetThreadWatcher(pageID, out watcher) ? ApiAddResult.Added(ApiThreadProjection.From(watcher)) : ApiAddResult.Failed(ApiError.InternalError);
        }

        // The host's defaults, but never a login or a folder (D8, G11), and always the URL that was checked. Guarded:
        // the watcher never connects to a local or private address, nor through a proxy (SSRFGuard), and the mark is
        // saved in api-threads.txt.
        private ThreadInfo CreateThread(string url) {
            ThreadInfo thread = _newThread(url) ?? throw new InvalidOperationException("No thread was made.");
            thread.URL = url;
            thread.PageAuth = String.Empty;
            thread.ImageAuth = String.Empty;
            thread.SaveDir = String.Empty;
            thread.Guarded = true;
            thread.ThreadNameLookedUp = true;
            return thread;
        }

        // The unknown-sites setting is read again here, in case it was turned off while the lookup ran
        private ApiError CheckCanAdd(string url, string pageID) {
            if (_policy.IsExiting()) return ApiError.Unavailable;
            if (!_session.CanSaveApiThreadMarks) return ApiError.MarksUnavailable;
            return ApiUrlRules.CheckHostName(new Uri(url), Settings.ApiAllowUnknownHosts == true) ?? CheckSession(url, pageID);
        }

        private ApiError CheckSession(string url, string pageID) {
            if (_session.IsThreadWatched(url)) return ApiError.AlreadyWatched;
            if (_session.IsBlacklisted(pageID)) return ApiError.Blacklisted;
            return _session.ThreadWatchers.Count >= _policy.ThreadCap ? ApiError.ThreadLimit : null;
        }

        private static string GetPageID(string url) {
            SiteHelper siteHelper = SiteHelpers.GetInstance(new Uri(url).Host);
            siteHelper.SetURL(url);
            return siteHelper.GetPageID();
        }
    }

    // What a client sees of a thread: no folder, login or error text
    internal static class ApiThreadProjection {
        public static ApiThread From(ThreadWatcher watcher) {
            WatcherExtraData extraData = watcher.Tag as WatcherExtraData ?? new WatcherExtraData { AddedOn = DateTime.Now };
            string state = GetState(watcher);
            return new ApiThread {
                Id = watcher.PageID,
                Url = EmptyIfNull(General.RemoveUserInfo(watcher.PageURL)),
                Description = EmptyIfNull(watcher.Description),
                Category = EmptyIfNull(watcher.Category),
                State = state,
                StopReason = state == "stopped" || state == "notFound" ? GetStopReasonName(watcher.StopReason) : null,
                AddedOn = new DateTimeOffset(extraData.AddedOn),
                AddedFrom = String.IsNullOrEmpty(extraData.AddedFrom) ? null : extraData.AddedFrom
            };
        }

        private static string EmptyIfNull(string text) {
            return text ?? String.Empty;
        }

        private static string GetState(ThreadWatcher watcher) {
            if (watcher.IsWaiting) return "waiting";
            if (watcher.IsRunning) return "running";
            return watcher.StopReason == StopReason.PageNotFound ? "notFound" : "stopped";
        }

        private static readonly Dictionary<StopReason, string> _stopReasonNames = new Dictionary<StopReason, string> {
            { StopReason.Other, "other" },
            { StopReason.UserRequest, "userRequest" },
            { StopReason.Exiting, "exiting" },
            { StopReason.PageNotFound, "pageNotFound" },
            { StopReason.DownloadComplete, "downloadComplete" },
            { StopReason.IOError, "ioError" }
        };

        private static string GetStopReasonName(StopReason reason) {
            string name;
            return _stopReasonNames.TryGetValue(reason, out name) ? name : "other";
        }
    }

    // The checks of a URL that need no lookup (security items 9, G12)
    internal static class ApiUrlRules {
        private static readonly string[] _localSuffixes = { ".local", ".internal", ".localhost" };

        // Null unless the value is an absolute http or https URL without a login, of at most maxLength characters,
        // whose host is the same in Unicode and in its DNS form (otherwise the known-site check would see another host
        // than the watcher). Cleaned as the app's Add button cleans it (General.CleanPageURL).
        // The cleaned URL (escaped, as the watcher uses it) must also fit the limit
        public static Uri ParseThreadUrl(string url, int maxLength) {
            if (!IsAcceptableText(url, maxLength) || ParseAllowedUri(url) == null) return null;
            return FitsLimit(ParseAllowedUri(General.CleanPageURL(url)), maxLength);
        }

        private static bool IsAcceptableText(string url, int maxLength) {
            return url != null && url.Length <= maxLength && !HasControlCharacter(url);
        }

        private static Uri FitsLimit(Uri uri, int maxLength) {
            return uri != null && uri.AbsoluteUri.Length <= maxLength ? uri : null;
        }

        private static Uri ParseAllowedUri(string url) {
            Uri uri;
            return url != null && Uri.TryCreate(url, UriKind.Absolute, out uri) && IsAllowedUri(uri) ? uri : null;
        }

        private static bool IsAllowedUri(Uri uri) {
            return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.UserInfo.Length == 0 && uri.Host.Length != 0 && uri.Host == uri.IdnHost;
        }

        private static bool HasControlCharacter(string text) {
            foreach (char c in text) {
                if (Char.IsControl(c)) return true;
            }
            return false;
        }

        // A known site passes. An unknown one passes only with the opt-in, and never as an IP address or a local name.
        public static ApiError CheckHostName(Uri uri, bool allowUnknownHosts) {
            if (SiteHelpers.IsKnownHost(uri.Host)) return null;
            if (!allowUnknownHosts) return ApiError.UnknownHost;
            return IsIPOrLocalName(uri) ? ApiError.BlockedHost : null;
        }

        private static bool IsIPOrLocalName(Uri uri) {
            if (uri.HostNameType != UriHostNameType.Dns) return true;
            string host = uri.Host.TrimEnd('.');
            return host.IndexOf('.') == -1 || host == "localhost" || Array.Exists(_localSuffixes, suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }
    }
}
