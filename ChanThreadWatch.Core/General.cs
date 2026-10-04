using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;

namespace JDP {
    public static class General {
        // .NET has only UTF-8, UTF-16/32, ASCII and Latin-1 built in. Pages and the update check can use other
        // code pages (Windows-1252, Shift_JIS, ...), and the app's drag and drop uses the system ANSI code page.
        static General() {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        // The version of the host app, set once at startup. This class lives in ChanThreadWatch.Core,
        // whose own assembly version is not the app's.
        public static Version HostVersion { get; set; }

        public static string Version {
            get {
                Version ver = HostVersion ?? throw new InvalidOperationException("General.HostVersion must be set by the host app at startup.");
                return ver.Major + "." + ver.Minor + "." + ver.Revision;
            }
        }

        // Settable so tests can use short limits. Production code never changes them.
        internal static int RequestTimeoutMS { get; set; } = DefaultRequestTimeoutMS;
        internal static int ReadTimeoutMS { get; set; } = DefaultReadTimeoutMS;
        internal static int MaxPageBytes { get; set; } = DefaultMaxPageBytes;
        internal static int MaxPageReadMS { get; set; } = DefaultMaxPageReadMS;
        internal static int FileReadGraceMS { get; set; } = DefaultFileReadGraceMS;
        internal static int MinFileBytesPerSecond { get; set; } = DefaultMinFileBytesPerSecond;

        internal const int DefaultRequestTimeoutMS = 60000;
        internal const int DefaultReadTimeoutMS = 60000;
        // Largest page (HTML, JSON) that is buffered in memory; real thread pages are a few MB at most
        internal const int DefaultMaxPageBytes = 32 * 1024 * 1024;
        // Longest time spent reading a page that is buffered in memory. Each read is also limited
        // by ReadTimeoutMS, but a server sending a byte now and then would never reach that.
        internal const int DefaultMaxPageReadMS = 5 * 60 * 1000;
        // A streamed download (a file, or a page reached through a meta refresh) has no fixed
        // size to base a time limit on, so it may take FileReadGraceMS plus the time its data
        // needs at MinFileBytesPerSecond: a 100 MB video may take about 3 hours. A server
        // dripping data slower than that is cut off, and the size limit bounds the rest.
        internal const int DefaultFileReadGraceMS = 5 * 60 * 1000;
        internal const int DefaultMinFileBytesPerSecond = 10 * 1024;

        public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

        public static string ReleaseDate {
            get { return "2026-Oct-03"; }
        }

        public static string ProgramURL {
            get { return "https://github.com/kevin-contino/ChanThreadWatch/releases"; }
        }

        public static string LatestReleaseAPIURL {
            get { return "https://api.github.com/repos/kevin-contino/ChanThreadWatch/releases/latest"; }
        }

        public static string WikiURL {
            get { return "https://github.com/SuperGouge/ChanThreadWatch/wiki"; }
        }

        public static Action DownloadAsync(string url, string auth, string referer, string connectionGroupName, DateTime? cacheLastModifiedTime, Action<HttpWebResponse> onResponse, Action<byte[], int> onDownloadChunk, Action onComplete, Action<Exception> onException) {
            AsyncDownload download = new AsyncDownload(auth, connectionGroupName, cacheLastModifiedTime, onResponse, onDownloadChunk, onComplete, onException);
            download.Start(url, referer);
            return download.Abort;
        }

        private static ThrottledStream CreateThrottledStream(Stream stream) {
            return new ThrottledStream(stream, Settings.MaximumBytesPerSecond ?? ThrottledStream.Infinite);
        }

        // Maps 404, 304 and rate limit (429, or 503 with Retry-After) protocol errors to their own
        // exception types. Closes the error response so its connection is released. Never throws.
        internal static Exception TranslateWebException(Exception ex) {
            HttpWebResponse response = GetProtocolErrorResponse(ex as WebException);
            if (response == null) return ex;
            try {
                return TranslateProtocolError(response, ex);
            }
            catch {
                return ex;
            }
            finally {
                CloseQuietly(response);
            }
        }

        // Returns null for any failure other than a protocol error, or when the error has no HTTP response
        private static HttpWebResponse GetProtocolErrorResponse(WebException webEx) {
            if (webEx == null || webEx.Status != WebExceptionStatus.ProtocolError) return null;
            return webEx.Response as HttpWebResponse;
        }

        private static Exception TranslateProtocolError(HttpWebResponse response, Exception ex) {
            if (response.StatusCode == HttpStatusCode.NotFound) return new HTTP404Exception();
            if (response.StatusCode == HttpStatusCode.NotModified) return new HTTP304Exception();
            string retryAfter = response.Headers["Retry-After"];
            if (IsRateLimitStatus(response.StatusCode, retryAfter != null)) return new HTTPRateLimitedException(response.ResponseUri.Host, ParseRetryAfter(retryAfter, DateTime.UtcNow), ex);
            return WithStatusText(response, ex);
        }

        // Returns the protocol error as an exception that keeps the HTTP status as its message, without
        // the response; other failures are returned as they are. The response is closed once the error is
        // handled, and .NET 10 cannot read the status of a closed response (.NET Framework could).
        internal static WebException WithoutResponse(WebException ex) {
            HttpWebResponse response = GetProtocolErrorResponse(ex);
            return response == null ? ex : WithStatusText(response, ex);
        }

        private static WebException WithStatusText(HttpWebResponse response, Exception ex) {
            return new WebException(FormatHTTPStatus(response), ex, WebExceptionStatus.ProtocolError, null);
        }

        // For example "HTTP 403 Forbidden"
        internal static string FormatHTTPStatus(HttpWebResponse response) {
            return String.Format("HTTP {0} {1}", (int)response.StatusCode, response.StatusDescription).TrimEnd();
        }

        private static bool IsRateLimitStatus(HttpStatusCode code, bool hasRetryAfter) {
            return (int)code == 429 || (code == HttpStatusCode.ServiceUnavailable && hasRetryAfter);
        }

        // Parses a Retry-After value, either a number of seconds or an HTTP date. Returns null if
        // the value is missing or invalid; a date in the past gives a zero wait.
        internal static TimeSpan? ParseRetryAfter(string value, DateTime utcNow) {
            if (value == null) return null;
            value = value.Trim();
            decimal seconds;
            if (Decimal.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out seconds)) {
                return TimeSpan.FromSeconds((double)Math.Min(seconds, Int32.MaxValue));
            }
            DateTime date;
            if (!TryParseHTTPDate(value, out date)) return null;
            return date > utcNow ? date - utcNow : TimeSpan.Zero;
        }

        // The three date formats HTTP allows (RFC 9110 section 5.6.7), as UTC
        private static bool TryParseHTTPDate(string value, out DateTime date) {
            return DateTime.TryParseExact(value,
                new[] { "r", "dddd, dd-MMM-yy HH:mm:ss G\\MT", "ddd MMM d HH:mm:ss yyyy" },
                CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
        }

        // For failure paths: Logger's type initializer can itself throw, and these callers must not
        internal static void LogQuietly(string message) {
            try {
                Logger.Log(message);
            }
            catch { }
        }

        private static void CloseQuietly(Stream stream) {
            if (stream == null) return;
            try { stream.Close(); }
            catch { }
        }

        private static void CloseQuietly(WebResponse response) {
            if (response == null) return;
            try { response.Close(); }
            catch { }
        }

        public static string GetRedirectUrl(string html, string currentPage) {
            try {
                HTMLParser parser = new HTMLParser(html);
                foreach (HTMLTag metaTag in parser.FindStartTags(parser.CreateTagRange(parser.FindStartTag("head")), "meta")) {
                    if (!string.Equals(metaTag.GetAttributeValueOrEmpty("http-equiv"), "Refresh", StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }
                    string metaContent = metaTag.GetAttributeValueOrEmpty("Content");
                    if (string.IsNullOrEmpty(metaContent)) {
                        continue;
                    }
                    return ParseMetaRefreshContent(metaContent, currentPage);
                }
                return null;
            }
            catch {
                return null;
            }
        }

        // Throws IndexOutOfRangeException if the content ends early; GetRedirectUrl treats that as no redirect.
        private static string ParseMetaRefreshContent(string metaContent, string currentPage) {
            int currentPosition = 0;
            currentPosition = GetNextNonWhiteSpaceCharacterPosition(metaContent, currentPosition);
            currentPosition = SkipMetaRefreshTime(metaContent, currentPosition);
            if (currentPosition == -1) {
                return null;
            }
            // ";", "URL" and "=", each optionally preceded by whitespace
            foreach (string token in new[] { "\u003B", "\u0055\u0052\u004C", "\u003D" }) {
                currentPosition = GetNextNonWhiteSpaceCharacterPosition(metaContent, currentPosition);
                currentPosition = MatchCharacters(metaContent, currentPosition, token);
                if (currentPosition == -1) {
                    return null;
                }
            }
            currentPosition = GetNextNonWhiteSpaceCharacterPosition(metaContent, currentPosition);
            return GetMetaRefreshURL(metaContent, currentPosition, currentPage);
        }

        private static int SkipMetaRefreshTime(string metaContent, int currentPosition) {
            StringBuilder timeString = new StringBuilder();
            while (metaContent.Length > currentPosition && IsMetaRefreshTimeCharacter(metaContent[currentPosition])) {
                timeString.Append(metaContent[currentPosition++]);
            }
            int time;
            int.TryParse(timeString.ToString(), out time);
            if (time < 0) {
                return -1;
            }
            return currentPosition;
        }

        private static bool IsMetaRefreshTimeCharacter(char c) {
            return c >= 48 && c <= 57 || c == 46;
        }

        private static int MatchCharacters(string str, int currentPosition, string expected) {
            foreach (char c in expected) {
                if (!IsCharacterMatch(str[currentPosition++], c)) {
                    return -1;
                }
            }
            return currentPosition;
        }

        private static string GetMetaRefreshURL(string metaContent, int currentPosition, string currentPage) {
            char quote = new char();
            if (IsCharacterMatch(metaContent[currentPosition], '\u0027') || IsCharacterMatch(metaContent[currentPosition], '\u0022')) {
                quote = metaContent[currentPosition++];
            }
            string redirectUrl = metaContent.Substring(currentPosition);
            if (!string.IsNullOrEmpty(quote.ToString())) {
                redirectUrl = redirectUrl.TrimEnd(quote);
            }
            redirectUrl = redirectUrl.TrimEnd('\u0020', '\u0009', '\u000A', '\u000C', '\u000D');
            redirectUrl = redirectUrl.Replace('\u0009', '\0').Replace('\u000A', '\0').Replace('\u000D', '\0');
            redirectUrl = GetAbsoluteURL(currentPage, redirectUrl);
            return redirectUrl;
        }

        // Sends nothing (throws RateLimitException) while the host is paused by a rate limit, and
        // pauses the host if it answers with one (throws HTTPRateLimitedException). Not paced by
        // MinRequestStartIntervalMS: callers (e.g. a new watcher's constructor) run on the UI thread.
        public static string DownloadPageToString(string url) {
            ConnectionManager connectionManager = ConnectionManager.GetInstance(url);
            connectionManager.ThrowIfPaused();
            try {
                return DownloadPageToStringUnchecked(url);
            }
            catch (WebException ex) {
                // Read before TranslateWebException closes the response
                WebException withoutResponse = WithoutResponse(ex);
                HTTPRateLimitedException rateLimited = TranslateWebException(ex) as HTTPRateLimitedException;
                if (rateLimited == null && withoutResponse == ex) throw;
                if (rateLimited == null) throw withoutResponse;
                connectionManager.PauseAndLog(rateLimited.RetryAfter);
                throw rateLimited;
            }
        }

        private static string DownloadPageToStringUnchecked(string url) {
            HttpWebRequest request = BuildWebRequest(url: url);
            HttpWebResponse response = null;
            try {
                response = (HttpWebResponse)request.GetResponse();
                string contentType = response.ContentType;
                byte[] pageBytes = ReadPageBytes(response.GetResponseStream());
                Encoding encoding = DetectHTMLEncoding(pageBytes, contentType);
                return encoding.GetString(pageBytes);
            }
            finally {
                CloseQuietly(response);
            }
        }

        private static HttpWebRequest BuildWebRequest(string url, string auth = null, string connectionGroupName = null, string referer = null, DateTime? cacheLastModifiedTime = null) {
            // SYSLIB0014: HttpWebRequest and ServicePointManager stay until the HttpClient transport (MP-5b)
#pragma warning disable SYSLIB0014
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
#pragma warning restore SYSLIB0014
            if (connectionGroupName != null) {
                request.ConnectionGroupName = connectionGroupName;
            }
            // 4chan blocks (HTTP 403) non-browser user agents, so default to a browser-like one
            request.UserAgent = GetUserAgent();
            // Bound the synchronous GetResponse and response stream reads (the meta refresh path and DownloadPageToString)
            request.Timeout = RequestTimeoutMS;
            request.ReadWriteTimeout = ReadTimeoutMS;
            if (cacheLastModifiedTime != null) {
                request.IfModifiedSince = cacheLastModifiedTime.Value;
            }
            if (!String.IsNullOrEmpty(auth)) {
                Encoding encoding = Encoding.GetEncoding("iso-8859-1");
                request.Headers.Add("Authorization", "Basic " + Convert.ToBase64String(encoding.GetBytes(auth)));
            }
            if (!String.IsNullOrEmpty(referer)) {
                request.Referer = RemoveUserInfo(referer);
            }
            return request;
        }

        // A thread URL may hold a login (user:password@host). It must never reach another server in the
        // Referer or a saved page. A value that isn't a URL but might hold a login gives null.
        public static string RemoveUserInfo(string url) {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return (url != null && url.Contains("@")) ? null : url;
            if (uri.UserInfo.Length == 0) return url;
            return uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped);
        }

        private static string GetUserAgent() {
            return (Settings.UseCustomUserAgent == true) ? Settings.CustomUserAgent : DefaultUserAgent;
        }

        // Reads the stream to its end and closes it. Throws PageTooLargeException as soon as the
        // data grows past MaxPageBytes, so an oversized page never gets buffered in full, and
        // times out once reading has taken longer than MaxPageReadMS (not counting the speed limit).
        internal static byte[] ReadPageBytes(Stream stream) {
            long startTicks = TickCount.Now;
            using (stream)
            using (MemoryStream memoryStream = new MemoryStream()) {
                byte[] data = new byte[8192];
                int dataLen;
                while ((dataLen = stream.Read(data, 0, data.Length)) != 0) {
                    if (memoryStream.Length + dataLen > MaxPageBytes) {
                        throw new PageTooLargeException(MaxPageBytes);
                    }
                    if (GetActiveReadMS(startTicks, stream) > MaxPageReadMS) {
                        throw new Exception("Timed out while reading response.");
                    }
                    memoryStream.Write(data, 0, dataLen);
                }
                return memoryStream.ToArray();
            }
        }

        // The time spent reading since startTicks, less the time the speed limit (shared by
        // all downloads) made the stream sleep, which can make a healthy download take any time
        private static long GetActiveReadMS(long startTicks, Stream stream) {
            ThrottledStream throttledStream = stream as ThrottledStream;
            long sleptMS = throttledStream != null ? throttledStream.SleptMilliseconds : 0;
            return TickCount.Now - startTicks - sleptMS;
        }

        // A streamed download may take FileReadGraceMS plus the time its data needs at MinFileBytesPerSecond
        private static bool IsPastStreamReadDeadline(long startTicks, Stream stream, long bytesRead) {
            return GetActiveReadMS(startTicks, stream) > FileReadGraceMS + bytesRead * 1000 / MinFileBytesPerSecond;
        }

        public static DateTime? GetResponseLastModifiedTime(HttpWebResponse response) {
            DateTime? lastModified = null;
            if (response.Headers["Last-Modified"] != null) {
                try {
                    // Parse the time string ourself instead of using .LastModified because
                    // older versions of Mono don't convert it from GMT to local.
                    lastModified = DateTime.ParseExact(response.Headers["Last-Modified"],
                        new[] { "r", "dddd, dd-MMM-yy HH:mm:ss G\\MT", "ddd MMM d HH:mm:ss yyyy" },
                        CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal);
                }
                catch { }
            }
            return lastModified;
        }

        public static int GetNextNonWhiteSpaceCharacterPosition(string str, int currentPosition) {
            while (char.IsWhiteSpace(str[currentPosition])) {
                currentPosition++;
            }
            return currentPosition;
        }

        public static bool IsCharacterMatch(char inputChar, char comparisonCharacter) {
            return char.ToUpperInvariant(inputChar).Equals(char.ToUpperInvariant(comparisonCharacter));
        }

        public static Encoding DetectHTMLEncoding(byte[] bytes, string httpContentType) {
            string charSet = DetectCharacterSet(bytes, httpContentType);
            if (charSet != null) {
                Encoding encoding = GetEncodingFromCharacterSet(charSet, bytes);
                if (encoding != null) {
                    return encoding;
                }
            }
            return Encoding.GetEncoding("Windows-1252");
        }

        private static string DetectCharacterSet(byte[] bytes, string httpContentType) {
            return
                GetCharSetFromContentType(httpContentType) ??
                DetectCharacterSetFromBOM(bytes) ??
                DetectCharacterSetFromContent(bytes, httpContentType);
        }

        // Returns null if the character set is not supported
        private static Encoding GetEncodingFromCharacterSet(string charSet, byte[] bytes) {
            if (IsUTF8(charSet)) {
                return new UTF8Encoding(HasBOM(bytes));
            }
            else if (IsUTF16(charSet)) {
                return new UnicodeEncoding(IsUTFBigEndian(charSet) ?? false, HasBOM(bytes));
            }
            else {
                try {
                    return Encoding.GetEncoding(charSet);
                }
                catch { }
            }
            return null;
        }

        private static string DetectCharacterSetFromBOM(byte[] bytes) {
            switch (GetBOMType(bytes)) {
                case BOMType.UTF8: return "UTF-8";
                case BOMType.UTF16LE: return "UTF-16LE";
                case BOMType.UTF16BE: return "UTF-16BE";
                default: return null;
            }
        }

        private static string DetectCharacterSetFromContent(byte[] bytes, string httpContentType) {
            string text = UnknownEncodingToString(bytes, 4096);
            HTMLParser htmlParser = new HTMLParser(text);
            string mimeType = GetMIMETypeFromContentType(httpContentType) ?? String.Empty;

            if (IsXMLMIMEType(mimeType)) {
                return DetectCharacterSetFromXMLDeclaration(text);
            }

            foreach (HTMLTag tag in htmlParser.FindStartTags("meta")) {
                string charSet = GetCharSetFromMetaTag(tag);
                if (!String.IsNullOrEmpty(charSet)) return charSet;
            }

            return null;
        }

        private static bool IsXMLMIMEType(string mimeType) {
            return mimeType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase) ||
                   mimeType.Equals("application/xml", StringComparison.OrdinalIgnoreCase) ||
                   mimeType.Equals("text/xml", StringComparison.OrdinalIgnoreCase);
        }

        private static string DetectCharacterSetFromXMLDeclaration(string text) {
            HTMLTag xmlTag = GetXMLDeclarationTag(text);
            if (xmlTag != null) {
                string charSet = xmlTag.GetAttributeValue("encoding");
                if (!String.IsNullOrEmpty(charSet)) return charSet;
            }

            // Default
            return "UTF-8";
        }

        private static HTMLTag GetXMLDeclarationTag(string text) {
            if (!text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)) return null;
            // XML declaration
            HTMLParser xmlParser = new HTMLParser("<" + text.Substring(2));
            if (xmlParser.Tags.Count < 1) return null;
            HTMLTag xmlTag = xmlParser.Tags[0];
            if (xmlTag.NameEquals("xml") && xmlTag.Offset == 0) return xmlTag;
            return null;
        }

        private static string GetCharSetFromMetaTag(HTMLTag tag) {
            // charset attribute
            string charSet = tag.GetAttributeValue("charset");
            if (!String.IsNullOrEmpty(charSet)) return charSet;

            // http-equiv and content attributes
            if (tag.GetAttributeValueOrEmpty("http-equiv").Trim().Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) {
                return GetCharSetFromContentType(tag.GetAttributeValue("content"));
            }
            return null;
        }

        public static string GetMIMETypeFromContentType(string contentType) {
            if (contentType == null) return null;
            int pos = contentType.IndexOf(';');
            if (pos != -1) {
                contentType = contentType.Substring(0, pos);
            }
            contentType = contentType.Trim();
            return contentType.Length != 0 ? contentType : null;
        }

        public static string GetCharSetFromContentType(string contentType) {
            if (contentType == null) return null;
            foreach (string part in contentType.Split(';')) {
                int pos = part.IndexOf('=');
                if (pos == -1) continue;
                string name = part.Substring(0, pos).Trim();
                if (!name.Equals("charset", StringComparison.OrdinalIgnoreCase)) continue;
                return ParseCharSetValue(part.Substring(pos + 1).Trim());
            }
            return null;
        }

        private static string ParseCharSetValue(string value) {
            if (IsQuotedValue(value)) {
                int pos = value.IndexOf(value[0], 1);
                if (pos == -1) pos = value.Length;
                value = value.Substring(1, pos - 1).Trim();
            }
            return value.Length != 0 ? value : null;
        }

        private static bool IsQuotedValue(string value) {
            return value.Length >= 1 && (value[0] == '"' || value[0] == '\'');
        }

        public static string UnknownEncodingToString(byte[] src, int maxLength) {
            byte[] dst = new byte[maxLength > 0 ? Math.Min(maxLength, src.Length) : src.Length];
            int iDst = 0;
            for (int iSrc = 0; iSrc < src.Length; iSrc++) {
                if (src[iSrc] == 0) continue;
                dst[iDst++] = src[iSrc];
                if (iDst >= dst.Length) break;
            }
            return Encoding.ASCII.GetString(dst, 0, iDst);
        }

        private static BOMType GetBOMType(byte[] bytes) {
            if (StartsWithBytes(bytes, 0xEF, 0xBB, 0xBF)) return BOMType.UTF8;
            if (StartsWithBytes(bytes, 0xFF, 0xFE)) return BOMType.UTF16LE;
            if (StartsWithBytes(bytes, 0xFE, 0xFF)) return BOMType.UTF16BE;
            return BOMType.None;
        }

        private static bool StartsWithBytes(byte[] bytes, params byte[] prefix) {
            if (bytes.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++) {
                if (bytes[i] != prefix[i]) return false;
            }
            return true;
        }

        private static bool HasBOM(byte[] bytes) {
            return GetBOMType(bytes) != BOMType.None;
        }

        private static bool IsUTF8(string charSet) {
            return charSet.Equals("UTF-8", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUTF16(string charSet) {
            return charSet.Equals("UTF-16", StringComparison.OrdinalIgnoreCase) ||
                   charSet.Equals("UTF-16BE", StringComparison.OrdinalIgnoreCase) ||
                   charSet.Equals("UTF-16LE", StringComparison.OrdinalIgnoreCase);
        }

        private static bool? IsUTFBigEndian(string charSet) {
            if (charSet.EndsWith("BE", StringComparison.OrdinalIgnoreCase)) return true;
            if (charSet.EndsWith("LE", StringComparison.OrdinalIgnoreCase)) return false;
            return null;
        }

        public static bool ArraysAreEqual<T>(T[] a, T[] b) where T : IComparable {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) {
                if (a[i].CompareTo(b[i]) != 0) return false;
            }
            return true;
        }

        // Credentials are only sent to the origin (scheme, host and port) they were entered for, so a
        // page, redirect or image link pointing elsewhere cannot collect them
        public static string GetAuthForURL(string auth, string authOriginURL, string targetURL) {
            return IsSameOrigin(authOriginURL, targetURL) ? auth : null;
        }

        public static bool IsSameOrigin(string urlA, string urlB) {
            Uri uriA, uriB;
            if (!Uri.TryCreate(urlA, UriKind.Absolute, out uriA) || !Uri.TryCreate(urlB, UriKind.Absolute, out uriB)) {
                return false;
            }
            return Uri.Compare(uriA, uriB, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;
        }

        public static string GetAbsoluteURL(string baseURL, string relativeURL) {
            try {
                Uri uri;
                if (!Uri.TryCreate(new Uri(baseURL), relativeURL, out uri)) {
                    return null;
                }
                // AbsoluteUri can throw undocumented Exception (e.g. for "mailto:+")
                return uri.AbsoluteUri;
            }
            catch {
                return null;
            }
        }

        public static string StripFragmentFromURL(string url) {
            int pos = url.IndexOf('#');
            return pos != -1 ? url.Substring(0, pos) : url;
        }

        public static string CleanPageURL(string url) {
            url = url.Trim();
            url = StripFragmentFromURL(url);
            if (url.Length == 0) return null;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "http://" + url;
            }
            if (url.IndexOf('/', url.IndexOf("//", StringComparison.Ordinal) + 2) == -1) return null;
            return TryGetAbsoluteURI(url);
        }

        private static string TryGetAbsoluteURI(string url) {
            try {
                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return null;
                return uri.AbsoluteUri;
            }
            catch {
                return null;
            }
        }

        public static string GetRelativeDirectoryPath(string dir, string baseDir) {
            if (dir.Length != 0 && Path.IsPathRooted(dir)) {
                Uri baseDirUri = new Uri(Path.Combine(baseDir, "dummy.txt"));
                Uri targetDirUri = new Uri(Path.Combine(dir, "dummy.txt"));
                try {
                    dir = Uri.UnescapeDataString(baseDirUri.MakeRelativeUri(targetDirUri).ToString());
                }
                catch (UriFormatException) {
                    // Workaround for Mono when determining the relative URI of directories
                    // on different drives in Windows.
                    return dir;
                }
                dir = (dir.Length == 0) ? "." : Path.GetDirectoryName(dir.Replace('/', Path.DirectorySeparatorChar));
            }
            return dir;
        }

        public static string GetAbsoluteDirectoryPath(string dir, string baseDir) {
            if (dir.Length != 0 && !Path.IsPathRooted(dir)) {
                dir = Path.GetFullPath(Path.Combine(baseDir, dir));
            }
            return dir;
        }

        public static string GetRelativeFilePath(string filePath, string baseDir) {
            if (filePath.Length != 0 && Path.IsPathRooted(filePath)) {
                string dir = Path.GetDirectoryName(filePath);
                string fileName = Path.GetFileName(filePath);
                dir = GetRelativeDirectoryPath(dir, baseDir);
                filePath = (dir == ".") ? fileName : Path.Combine(dir, fileName);
            }
            return filePath;
        }

        public static string GetAbsoluteFilePath(string filePath, string baseDir) {
            if (filePath.Length != 0 && !Path.IsPathRooted(filePath)) {
                filePath = Path.GetFullPath(Path.Combine(baseDir, filePath));
            }
            return filePath;
        }

        public static string GetLastDirectory(string dir) {
            char[] separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            dir = dir.TrimEnd(separators);
            int pos = dir.LastIndexOfAny(separators);
            return (pos == -1) ? String.Empty : dir.Substring(pos + 1);
        }

        public static string RemoveLastDirectory(string dir) {
            char[] separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            dir = dir.TrimEnd(separators);
            int pos = dir.LastIndexOfAny(separators);
            return (pos == -1) ? dir : dir.Substring(0, pos);
        }

        public static int GetMaximumFileNameLength(string dir) {
            // Kind of a binary search except we only know whether the middle
            // item is <= or > the target rather than <, =, or >.
            int min = 0;
            int max = 4096;
            while (max >= min + 2) {
                int n = (min + max) / 2;
                if (IsFileNameTooLong(dir, n)) {
                    max = n - 1;
                }
                else {
                    min = n;
                }
            }
            if (max > min) {
                return IsFileNameTooLong(dir, max) ? min : max;
            }
            else {
                return min;
            }
        }

        public static bool IsFileNameTooLong(string dir, int fileNameLength) {
            if (!Directory.Exists(dir)) throw new DirectoryNotFoundException();
            string path = FindUnusedFilePath(dir, fileNameLength);
            if (path == null) {
                throw new Exception("Unable to determine if filename is too long.");
            }
            return IsFilePathTooLong(path);
        }

        private static string FindUnusedFilePath(string dir, int fileNameLength) {
            for (char c = 'a'; c <= 'z'; c++) {
                string path = Path.Combine(dir, new string(c, fileNameLength));
                if (!File.Exists(path)) {
                    return path;
                }
            }
            return null;
        }

        // The longest full path the .NET Framework 4.8 app could create (MAX_PATH less the terminating
        // null; the app was not long path aware). .NET 10 creates longer paths, but file names are
        // shortened to fit this limit, so a longer one would name a thread's files differently than
        // older versions did and download them again.
        internal const int MaxFilePathLength = 259;
        private const int ErrorInvalidName = unchecked((int)0x8007007B);

        private static bool IsFilePathTooLong(string path) {
            return path.Length > MaxFilePathLength || !CanCreateFile(path);
        }

        // False if the file system rejects the path as too long
        private static bool CanCreateFile(string path) {
            try {
                using (File.Create(path)) { }
                try { File.Delete(path); }
                catch { }
                return true;
            }
            catch (PathTooLongException) {
                return false;
            }
            catch (DirectoryNotFoundException) {
                // Workaround for Mono
                return false;
            }
            catch (IOException ex) when (ex.HResult == ErrorInvalidName) {
                // A file name longer than 255 characters (.NET Framework threw PathTooLongException)
                return false;
            }
        }

        public static void EnsureThreadPoolMaxThreads(int minWorkerThreads, int minCompletionPortThreads) {
            int workerThreads;
            int completionPortThreads;
            ThreadPool.GetMaxThreads(out workerThreads, out completionPortThreads);
            if (workerThreads < minWorkerThreads || completionPortThreads < minCompletionPortThreads) {
                ThreadPool.SetMaxThreads(Math.Max(workerThreads, minWorkerThreads), Math.Max(completionPortThreads, minCompletionPortThreads));
            }
        }

        public static int ParseVersionNumber(string str) {
            string[] split = str.Split('.');
            int[] masks = { 0x7F, 0xFF, 0xFF, 0xFF };
            int[] shifts = { 24, 16, 8, 0 };
            int num = 0;
            try {
                for (int i = 0; i < split.Length && i < masks.Length; i++) {
                    num |= (Int32.Parse(split[i]) & masks[i]) << shifts[i];
                }
                return num;
            }
            catch {
                return -1;
            }
        }

        // An update may raise the major version by at most this much over the running version.
        private const int MaxUpdateMajorVersionJump = 1;

        private static readonly int[] _versionComponentMaximums = { 0x7F, 0xFF, 0xFF, 0xFF };

        // Returns the tag_name of a GitHub releases API response, or null if it is missing.
        public static string ParseReleaseTagName(string json) {
            if (json == null) return null;
            Match match = Regex.Match(json, @"""tag_name""\s*:\s*""([^""\\]*)""");
            return match.Success ? match.Groups[1].Value : null;
        }

        // Returns a release tag (e.g. "v1.17.2") as a plain version string ("1.17.2"), or null if
        // the tag is not a plausible version: one to four numeric components that each fit in
        // ParseVersionNumber's packing, with a major version not far above currentVersion.
        public static string NormalizeUpdateVersion(string tag, string currentVersion) {
            int[] components = ParseVersionComponents(tag == null ? null : StripVersionPrefix(tag.Trim()));
            int[] current = ParseVersionComponents(currentVersion);
            if (components == null || current == null || components[0] > current[0] + MaxUpdateMajorVersionJump) {
                return null;
            }
            return String.Join(".", Array.ConvertAll(components, c => c.ToString(CultureInfo.InvariantCulture)));
        }

        private static string StripVersionPrefix(string tag) {
            return tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Substring(1) : tag;
        }

        // Returns null unless every component is a plain non-negative number within its packing range.
        private static int[] ParseVersionComponents(string version) {
            if (version == null) return null;
            string[] split = version.Split('.');
            if (split.Length > _versionComponentMaximums.Length) return null;
            int[] components = new int[split.Length];
            for (int i = 0; i < split.Length; i++) {
                components[i] = ParseVersionComponent(split[i], _versionComponentMaximums[i]);
                if (components[i] == -1) return null;
            }
            return components;
        }

        // Returns -1 if the text is not a plain number between 0 and maximum.
        private static int ParseVersionComponent(string text, int maximum) {
            int value;
            return Int32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= maximum ? value : -1;
        }

        public static byte[] TryBase64Decode(string s) {
            try {
                return Convert.FromBase64String(s);
            }
            catch {
                return null;
            }
        }

        public static ulong Calculate64BitMD5(byte[] bytes) {
            using (MD5 hashAlgo = MD5.Create()) {
                return BytesTo64BitXor(hashAlgo.ComputeHash(bytes));
            }
        }

        public static ulong BytesTo64BitXor(byte[] bytes) {
            ulong result = 0;
            for (int i = 0; i < bytes.Length; i++) {
                result ^= (ulong)bytes[i] << ((7 - (i % 8)) * 8);
            }
            return result;
        }

        // Saved pages are written as UTF-8 with a byte order mark. A browser that opens the file
        // decodes it by the mark before any charset the page declares, so it reads the same text
        // that SavedPageSweep checked.
        public static readonly Encoding SavedPageEncoding = new UTF8Encoding(true);

        // Writes a page as it is saved: with the replacements applied, then through the last pass
        // of SavedPageSweep, which does not depend on how HTMLParser read the page
        public static void WriteSavedPage(string str, List<ReplaceInfo> replaceList, TextWriter outStream) {
            using (StringWriter replaced = new StringWriter()) {
                WriteReplacedString(str, replaceList, replaced);
                // A byte order mark from the download is dropped; the writer adds its own
                outStream.Write(SavedPageSweep.Sweep(replaced.ToString().TrimStart('\uFEFF')));
            }
        }

        public static void WriteReplacedString(string str, List<ReplaceInfo> replaceList, TextWriter outStream) {
            int offset = 0;
            SortByOffset(replaceList);
            for (int iReplace = 0; iReplace < replaceList.Count; iReplace++) {
                ReplaceInfo replace = replaceList[iReplace];
                if (IsSkippedReplace(replace, offset)) continue;
                if (replace.Offset + replace.Length > str.Length) break;
                WriteReplace(str, offset, replace, outStream);
                offset = replace.Offset + replace.Length;
            }
            if (str.Length > offset) {
                outStream.Write(str.Substring(offset));
            }
        }

        // List.Sort is unstable, so replacements at the same offset are kept in the order they were
        // added. Several posts resurrected after the same post are inserted at one offset.
        private static void SortByOffset(List<ReplaceInfo> replaceList) {
            Dictionary<ReplaceInfo, int> addedOrder = new Dictionary<ReplaceInfo, int>();
            for (int i = 0; i < replaceList.Count; i++) {
                addedOrder[replaceList[i]] = i;
            }
            replaceList.Sort((x, y) => x.Offset != y.Offset ? x.Offset.CompareTo(y.Offset) : addedOrder[x].CompareTo(addedOrder[y]));
        }

        // Overlapping and negative length replacements are ignored
        private static bool IsSkippedReplace(ReplaceInfo replace, int offset) {
            return replace.Offset < offset || replace.Length < 0;
        }

        private static void WriteReplace(string str, int offset, ReplaceInfo replace, TextWriter outStream) {
            if (replace.Offset > offset) {
                outStream.Write(str.Substring(offset, replace.Offset - offset));
            }
            if (!String.IsNullOrEmpty(replace.Value)) {
                outStream.Write(replace.Value);
            }
        }

        public static void AddOtherReplaces(HTMLParser htmlParser, string pageURL, List<ReplaceInfo> replaceList) {
            AddOtherReplaces(htmlParser, pageURL, replaceList, null);
        }

        // offlineScriptSite is one of the OfflinePageScript site names to add our script for that
        // site's markup, or null for no script
        public static void AddOtherReplaces(HTMLParser htmlParser, string pageURL, List<ReplaceInfo> replaceList, string offlineScriptSite) {
            HashSet<int> existingOffsets = new HashSet<int>();
            // Links made absolute in the saved page never carry a login from the thread URL
            pageURL = RemoveUserInfo(pageURL);

            foreach (ReplaceInfo replace in replaceList) {
                existingOffsets.Add(replace.Offset);
            }

            if (Environment.NewLine != "\n") {
                AddNewLineReplaces(htmlParser, replaceList);
            }

            foreach (HTMLTag tag in htmlParser.FindStartTags("base")) {
                replaceList.Add(
                    new ReplaceInfo {
                        Offset = tag.Offset,
                        Length = tag.Length,
                        Type = ReplaceType.Other,
                        Value = String.Empty
                    });
            }

            AddActiveContentReplaces(htmlParser, replaceList, existingOffsets, offlineScriptSite);
            AddURLAttributeReplaces(htmlParser, pageURL, replaceList, existingOffsets);
        }

        // Only the replacements that keep a page from running code, for a page that is otherwise
        // saved as downloaded, plus our offline script when offlineScriptSite is not null
        public static List<ReplaceInfo> GetActiveContentReplaces(HTMLParser htmlParser, string offlineScriptSite) {
            List<ReplaceInfo> replaceList = new List<ReplaceInfo>();
            if (Environment.NewLine != "\n") {
                AddNewLineReplaces(htmlParser, replaceList);
            }
            AddActiveContentReplaces(htmlParser, replaceList, new HashSet<int>(), offlineScriptSite);
            return replaceList;
        }

        // Elements that are removed from saved pages together with their contents
        private static readonly string[] _activeContentElements = { "script", "iframe", "frame", "object", "embed", "applet" };

        // Added to the saved page's head. Blocks anything the removal misses where a browser parses
        // the markup differently from HTMLParser.
        public const string ActiveContentPolicyMeta = "<meta http-equiv=\"Content-Security-Policy\" content=\"script-src 'none'; object-src 'none'; frame-src 'none'\">";

        // Removes scripts, embedded content, event handler attributes and script URLs, so a saved
        // page can't run code when it is opened from disk
        private static void AddActiveContentReplaces(HTMLParser htmlParser, List<ReplaceInfo> replaceList, HashSet<int> existingOffsets, string offlineScriptSite) {
            AddContentPolicyReplace(htmlParser, replaceList, offlineScriptSite);
            AddEarlierPolicyRemoveReplaces(htmlParser, replaceList);
            foreach (HTMLTag tag in htmlParser.Tags) {
                if (IsRemovedElement(tag)) replaceList.Add(CreateRemoveReplace(tag.Offset, GetRemovedElementLength(htmlParser, tag)));
            }
            foreach (HTMLTag tag in htmlParser.Tags) {
                AddActiveAttributeReplaces(tag.Attributes, replaceList, existingOffsets);
                AddActiveAttributeReplaces(tag.DuplicateAttributes, replaceList, existingOffsets);
            }
        }

        // Active elements, and elements whose raw text contents a browser may read as markup
        // (see HTMLTag.ContentsMayBeMarkup)
        private static bool IsRemovedElement(HTMLTag tag) {
            return !tag.IsEnd && (tag.ContentsMayBeMarkup || tag.NameEqualsAny(_activeContentElements));
        }

        // An element whose contents the parser read as raw text loses all of them, up to and with
        // its end tag, so nothing the parser skipped stays. Void elements have no contents;
        // matching a stray end tag would remove everything up to it. Any other element without an
        // end tag loses only its start tag, and its contents are markup the parser read.
        private static int GetRemovedElementLength(HTMLParser htmlParser, HTMLTag tag) {
            if (tag.RawTextEndOffset != -1) return htmlParser.GetRawTextElementEndOffset(tag) - tag.Offset;
            if (tag.NameEqualsAny("embed", "frame")) return tag.Length;
            HTMLTagRange tagRange = htmlParser.CreateTagRange(tag);
            return tagRange != null ? tagRange.Length : tag.Length;
        }

        // Adds our script and the policy that allows it where the browser reads them in the head
        // (see OfflinePageScript.CreateHeadReplace). A page without a script, or without a head
        // the browser would see as such, gets the policy that allows no script instead.
        private static void AddContentPolicyReplace(HTMLParser htmlParser, List<ReplaceInfo> replaceList, string offlineScriptSite) {
            ReplaceInfo replace = (offlineScriptSite != null ? OfflinePageScript.CreateHeadReplace(htmlParser, offlineScriptSite) : null) ?? CreateNoScriptPolicyReplace(htmlParser);
            replaceList.Add(replace);
        }

        // What a browser reads before it starts the html element: a BOM at the start, HTML white
        // space, comments, the doctype and other markup it reads as a comment. A comment ends
        // where a browser ends it.
        private static readonly Regex _leadingMarkup = new Regex("^\\uFEFF?(?:[ \\t\\n\\f\\r]|<!--(?:-?>|[\\s\\S]*?--!?>)|<!(?!--)[^>]*>|<\\?[^>]*>)*");

        // Replaces the head start tag, or the html start tag when the head is implied, so no other
        // replacement can share its offset; only where a browser starts its head there (see
        // OfflinePageScript.FindHeadAnchorIndex). Any other page gets the policy before its first
        // content, which a browser puts in the head it creates.
        private static ReplaceInfo CreateNoScriptPolicyReplace(HTMLParser htmlParser) {
            int anchorIndex = OfflinePageScript.FindHeadAnchorIndex(htmlParser);
            if (anchorIndex == -1) return CreateLeadingPolicyReplace(htmlParser);
            HTMLTag tag = htmlParser.Tags[anchorIndex];
            return new ReplaceInfo {
                Offset = tag.Offset,
                Length = tag.Length,
                Type = ReplaceType.Other,
                Value = "<" + tag.Name + ">" + ActiveContentPolicyMeta
            };
        }

        private static ReplaceInfo CreateLeadingPolicyReplace(HTMLParser htmlParser) {
            return new ReplaceInfo {
                Offset = _leadingMarkup.Match(htmlParser.PreprocessedHTML).Length,
                Length = 0,
                Type = ReplaceType.Other,
                Value = ActiveContentPolicyMeta
            };
        }

        // The policies this program wrote into pages: the one without a script, and the one that
        // allows our script by its hash
        private static readonly Regex _earlierPolicy = new Regex("^(?:script-src 'none'; object-src 'none'; frame-src 'none'|script-src 'sha256-[A-Za-z0-9+/]{43}='; object-src 'none'; frame-src 'none'; connect-src 'none')$");

        // A saved page that is saved again (a reparse) loses the policy written the last time,
        // since every policy applies and an old one would block the new script. A policy that the
        // site wrote is kept.
        private static void AddEarlierPolicyRemoveReplaces(HTMLParser htmlParser, List<ReplaceInfo> replaceList) {
            foreach (HTMLTag tag in htmlParser.FindStartTags("meta")) {
                if (IsEarlierPolicyMeta(tag)) replaceList.Add(CreateRemoveReplace(tag.Offset, tag.Length));
            }
        }

        private static bool IsEarlierPolicyMeta(HTMLTag tag) {
            return String.Equals(tag.GetAttributeValue("http-equiv"), "Content-Security-Policy", StringComparison.OrdinalIgnoreCase) &&
                _earlierPolicy.IsMatch(tag.GetAttributeValueOrEmpty("content"));
        }

        // Attributes already replaced by the site helper hold values the program wrote, so they are kept
        private static void AddActiveAttributeReplaces(List<HTMLAttribute> attributes, List<ReplaceInfo> replaceList, HashSet<int> existingOffsets) {
            foreach (HTMLAttribute attribute in attributes) {
                if (existingOffsets.Contains(attribute.Offset) || !IsActiveAttribute(attribute)) continue;
                replaceList.Add(CreateRemoveReplace(attribute.Offset, attribute.Length));
                existingOffsets.Add(attribute.Offset);
            }
        }

        private static ReplaceInfo CreateRemoveReplace(int offset, int length) {
            return new ReplaceInfo {
                Offset = offset,
                Length = length,
                Type = ReplaceType.Other,
                Value = String.Empty
            };
        }

        private static bool IsActiveAttribute(HTMLAttribute attribute) {
            return attribute.Name.StartsWith("on", StringComparison.Ordinal) || IsScriptURL(attribute.Value);
        }

        // Browsers ignore tabs, newlines and leading spaces in a URL scheme, also when written as
        // character references. HttpUtility doesn't know the HTML5-only names used here, and leaves
        // numeric references without a semicolon undecoded, which browsers decode.
        public static bool IsScriptURL(string value) {
            string decoded = DecodeAttributeValue(value);
            StringBuilder url = new StringBuilder(decoded.Length);
            foreach (char c in decoded) {
                if (c > ' ') url.Append(Char.ToLowerInvariant(c));
            }
            string scheme = url.ToString();
            return scheme.StartsWith("javascript:", StringComparison.Ordinal) || scheme.StartsWith("vbscript:", StringComparison.Ordinal);
        }

        // Decodes character references the way a browser does in an attribute value, including
        // numeric references without a semicolon and the named references for tab, newline and colon
        public static string DecodeAttributeValue(string value) {
            string terminated = Regex.Replace(value, "&#([0-9]+|[xX][0-9a-fA-F]+);?", "&#$1;");
            return HttpUtility.HtmlDecode(terminated.Replace("&Tab;", "\t").Replace("&NewLine;", "\n").Replace("&colon;", ":"));
        }

        private static void AddNewLineReplaces(HTMLParser htmlParser, List<ReplaceInfo> replaceList) {
            int offset = 0;
            while ((offset = htmlParser.PreprocessedHTML.IndexOf('\n', offset)) != -1) {
                replaceList.Add(new ReplaceInfo {
                    Offset = offset,
                    Length = 1,
                    Type = ReplaceType.Other,
                    Value = Environment.NewLine
                });
                offset += 1;
            }
        }

        private static void AddURLAttributeReplaces(HTMLParser htmlParser, string pageURL, List<ReplaceInfo> replaceList, HashSet<int> existingOffsets) {
            foreach (HTMLTag tag in htmlParser.FindStartTags("a", "img", "link")) {
                HTMLAttribute attribute = GetURLAttribute(tag);
                if (attribute == null || existingOffsets.Contains(attribute.Offset)) continue;
                string newURL = GetReplacementURL(pageURL, attribute.Value, tag.NameEquals("a"));
                if (newURL != null) {
                    replaceList.Add(
                        new ReplaceInfo {
                            Offset = attribute.Offset,
                            Length = attribute.Length,
                            Type = ReplaceType.Other,
                            Value = attribute.Name + "=\"" + HttpUtility.HtmlAttributeEncode(newURL) + "\""
                        });
                }
            }
        }

        private static HTMLAttribute GetURLAttribute(HTMLTag tag) {
            if (tag.NameEqualsAny("a", "link")) return tag.GetAttribute("href");
            if (tag.NameEquals("img")) return tag.GetAttribute("src");
            return null;
        }

        private static string GetReplacementURL(string pageURL, string attributeValue, bool isATag) {
            // Make attribute's URL absolute
            string newURL = GetAbsoluteURL(pageURL, HttpUtility.HtmlDecode(attributeValue));
            // For links to anchors on the current page, use just the fragment
            if (isATag && IsAnchorOnPage(newURL, pageURL)) {
                newURL = newURL.Substring(pageURL.Length);
            }
            return newURL;
        }

        private static bool IsAnchorOnPage(string url, string pageURL) {
            return url != null && url.Length > pageURL.Length &&
                url.StartsWith(pageURL, StringComparison.Ordinal) && url[pageURL.Length] == '#';
        }

        public static string URLFileName(string url) {
            int pos = url.LastIndexOf("/", StringComparison.Ordinal);
            return (pos == -1) ? String.Empty : url.Substring(pos + 1);
        }

        // Returns a single safe path segment: no invalid characters, never "." or "..", and never a
        // reserved device name, so it cannot escape or break the directory it is combined with
        public static string CleanFileName(string src) {
            // Windows drops trailing dots and spaces, which would turn "..." or ". ." into ".."
            string name = RemoveInvalidFileNameChars(src).TrimEnd('.', ' ');
            return IsReservedDeviceName(name) ? "_" + name : name;
        }

        private static bool IsReservedDeviceName(string name) {
            int pos = name.IndexOf('.');
            string baseName = (pos == -1) ? name : name.Substring(0, pos);
            return Array.Exists(_reservedDeviceNames, n => n.Equals(baseName.TrimEnd(' '), StringComparison.OrdinalIgnoreCase));
        }

        private static readonly string[] _reservedDeviceNames = {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00B9", "COM\u00B2", "COM\u00B3",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3"
        };

        private static string RemoveInvalidFileNameChars(string src) {
            char[] dst = new char[src.Length];
            char[] inv = Path.GetInvalidFileNameChars();
            int iDst = 0;
            for (int iSrc = 0; iSrc < src.Length; iSrc++) {
                char c = src[iSrc];
                for (int j = 0; j < inv.Length; j++) {
                    if (c == inv[j]) {
                        c = (char)0;
                        break;
                    }
                }
                if (c != 0) {
                    dst[iDst++] = c;
                }
            }
            return new string(dst, 0, iDst);
        }

        public static string HtmlAttributeEncode(string s, bool allowHashParameter = true) {
            return HttpUtility.HtmlAttributeEncode(allowHashParameter ? s : s.Replace("#", "%23"));
        }

        public static int StrLen(byte[] bytes) {
            for (int i = 0; i < bytes.Length; i++) {
                if (bytes[i] == 0) return i;
            }
            return bytes.Length;
        }

        public static int StrLenW(byte[] bytes) {
            for (int i = 0; i < bytes.Length - 1; i += 2) {
                if (bytes[i] == 0 && bytes[i + 1] == 0) return i / 2;
            }
            return bytes.Length / 2;
        }

        public static void BackupThreadList(bool checkSize = false) {
            try {
                string path = Path.Combine(Settings.GetSettingsDirectory(), Settings.ThreadsFileName);
                if (!File.Exists(path)) return;
                var backupInfo = new FileInfo(path + ".bak");
                if (ShouldBackupThreadList(path, backupInfo, checkSize)) {
                    string[] backupLines = ThreadListFile.GetBackupLines(File.ReadAllLines(path));
                    // Never replace the backup with a thread list that wouldn't load
                    if (backupLines == null) return;
                    TextFile.WriteAllLinesAtomic(path + ".bak", backupLines);
                }
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
        }

        // When checking size, avoid overwriting a larger backup with a smaller thread list
        private static bool ShouldBackupThreadList(string path, FileInfo backupInfo, bool checkSize) {
            return !checkSize || !backupInfo.Exists || new FileInfo(path).Length >= backupInfo.Length;
        }

        // Holds the state shared by the asynchronous callbacks of a single DownloadAsync call.
        // All state changes and all callbacks happen while holding _sync. Callback guarantees:
        // onComplete is called at most once; onException is called at most once; nothing is
        // called after onException; onException is never called after an onComplete that
        // returned normally, but it does follow an onComplete that threw (with that exception),
        // which ThreadWatcher uses to retry corrupt downloads.
        // Blocking work (BeginGetResponse's DNS lookup, buffering an HTML page, the synchronous
        // meta refresh request, and BeginRead, which can sleep in ThrottledStream) runs without
        // holding _sync, so AbortInternal can always take the lock and abort the request or close
        // the response or stream that the blocked thread is waiting on.
        private sealed class AsyncDownload {
            private const int ReadBufferSize = 8192;

            private readonly object _sync = new object();
            private readonly string _auth;
            private readonly string _connectionGroupName;
            private readonly DateTime? _cacheLastModifiedTime;
            private readonly Action<HttpWebResponse> _onResponse;
            private readonly Action<byte[], int> _onDownloadChunk;
            private readonly Action _onComplete;
            private readonly Action<Exception> _onException;
            private bool _aborting;
            private bool _completed;
            private HttpWebRequest _request;
            private HttpWebResponse _response;
            private Stream _responseStream;
            private byte[] _buff;
            private string _url;
            private long _readStartTicks;
            private long _totalBytesRead;

            public AsyncDownload(string auth, string connectionGroupName, DateTime? cacheLastModifiedTime, Action<HttpWebResponse> onResponse, Action<byte[], int> onDownloadChunk, Action onComplete, Action<Exception> onException) {
                _auth = auth;
                _connectionGroupName = connectionGroupName;
                _cacheLastModifiedTime = cacheLastModifiedTime;
                _onResponse = onResponse;
                _onDownloadChunk = onDownloadChunk;
                _onComplete = onComplete;
                _onException = onException;
            }

            // Unfortunately BeginGetResponse blocks until the DNS lookup (and proxy detection) has
            // finished, so it runs on a thread pool thread and the caller gets the abort delegate
            // right away. HttpWebRequest.Abort would also wait for the lookup, so the request is
            // only published for AbortInternal once BeginGetResponse has returned.
            public void Start(string url, string referer) {
                try {
                    HttpWebRequest request = CreateRequest(url, referer);
                    ThreadPool.QueueUserWorkItem((s) => BeginGetResponse(request));
                }
                catch (Exception ex) {
                    AbortInternal(ex);
                }
            }

            private void BeginGetResponse(HttpWebRequest request) {
                try {
                    IAsyncResult requestResult = request.BeginGetResponse(OnGetResponse, request);
                    PublishRequest(request);
                    AbortOnTimeout(requestResult, RequestTimeoutMS, "Timed out while waiting for response.");
                }
                catch (Exception ex) {
                    AbortInternal(ex);
                }
            }

            private HttpWebRequest CreateRequest(string url, string referer) {
                lock (_sync) {
                    _url = url;
                    return BuildWebRequest(url: url, auth: _auth, connectionGroupName: _connectionGroupName, cacheLastModifiedTime: _cacheLastModifiedTime, referer: referer);
                }
            }

            // If the download was aborted during BeginGetResponse, the request is aborted here
            // instead (but not once completed, which would close a reusable connection). A meta
            // refresh request may already have replaced it.
            private void PublishRequest(HttpWebRequest request) {
                lock (_sync) {
                    if (!_aborting) {
                        if (_request == null) _request = request;
                    }
                    else if (!_completed) {
                        request.Abort();
                    }
                }
            }

            public void Abort() {
                ThreadPool.QueueUserWorkItem((s) => {
                    AbortInternal(new Exception("Download has been aborted."));
                });
            }

            private void AbortInternal(Exception ex) {
                lock (_sync) {
                    if (_aborting) return;
                    _aborting = true;
                    CleanupQuietly();
                    NotifyException(ex);
                }
            }

            // Callers run on thread pool threads, where an escaping exception ends the process
            private void CleanupQuietly() {
                try {
                    Cleanup();
                }
                catch (Exception ex) {
                    LogQuietly(ex.ToString());
                }
            }

            // Callers run on thread pool threads, where an escaping exception ends the process
            private void NotifyException(Exception ex) {
                try {
                    _onException(ex);
                }
                catch (Exception callbackEx) {
                    LogQuietly(callbackEx.ToString());
                }
            }

            private void AbortOnTimeout(IAsyncResult asyncResult, int timeoutMS, string message) {
                ThreadPool.RegisterWaitForSingleObject(asyncResult.AsyncWaitHandle,
                    (state, timedOut) => {
                        if (!timedOut) return;
                        AbortInternal(new Exception(message));
                    }, null, timeoutMS, true);
            }

            // Aborting the request and closing the response also fails any read or GetResponse
            // that another thread is blocked in without holding _sync.
            private void Cleanup() {
                if (_request != null) {
                    _request.Abort();
                    _request = null;
                }
                CloseQuietly(_responseStream);
                _responseStream = null;
                CloseQuietly(_response);
                _response = null;
            }

            private void ThrowIfAborting() {
                if (_aborting) throw new OperationCanceledException("Download has been aborted.");
            }

            private void OnGetResponse(IAsyncResult requestResultParam) {
                try {
                    HttpWebResponse response = EndGetResponse(requestResultParam);
                    if (response == null) return;
                    if (!StartReading(OpenResponseStream(response))) return;
                }
                catch (Exception ex) {
                    // Once aborted, this only releases the error response (if any)
                    AbortInternal(TranslateWebException(ex));
                    return;
                }
                OnRead(null);
            }

            // Returns null if the download has already been aborted, after releasing any
            // response that completed just before the abort
            private HttpWebResponse EndGetResponse(IAsyncResult requestResult) {
                HttpWebRequest request = (HttpWebRequest)requestResult.AsyncState;
                lock (_sync) {
                    if (!_aborting) {
                        _response = (HttpWebResponse)request.EndGetResponse(requestResult);
                        return _response;
                    }
                }
                CloseQuietly(EndGetResponseQuietly(request, requestResult));
                return null;
            }

            private static WebResponse EndGetResponseQuietly(HttpWebRequest request, IAsyncResult requestResult) {
                try {
                    return request.EndGetResponse(requestResult);
                }
                catch (Exception ex) {
                    // Translating closes the error response of a protocol error
                    TranslateWebException(ex);
                    return null;
                }
            }

            // Runs without holding _sync; the HTML path blocks on the network
            private Stream OpenResponseStream(HttpWebResponse response) {
                if (GetMIMETypeFromContentType(response.ContentType) == "text/html") {
                    return OpenHTMLResponseStream(response);
                }
                return CreateThrottledStream(response.GetResponseStream());
            }

            // Buffers the page so it can be checked for a meta refresh redirect, and follows it if present
            private Stream OpenHTMLResponseStream(HttpWebResponse response) {
                string contentType = response.ContentType;
                string pageUrl = response.ResponseUri.AbsoluteUri;
                byte[] pageBytes = ReadPageBytes(CreateThrottledStream(response.GetResponseStream()));
                string html = DetectHTMLEncoding(pageBytes, contentType).GetString(pageBytes);
                string redirectUrl = GetRedirectUrl(html, pageUrl);
                if (string.IsNullOrEmpty(redirectUrl)) {
                    return new MemoryStream(pageBytes);
                }
                return FollowMetaRefresh(redirectUrl);
            }

            // The redirect request is published in _request before GetResponse blocks, so
            // AbortInternal can abort it. Request.Timeout bounds the wait otherwise.
            private Stream FollowMetaRefresh(string redirectUrl) {
                // Sends nothing to a host that is paused by a rate limit
                ConnectionManager.GetInstance(redirectUrl).ThrowIfPaused();
                HttpWebRequest redirectionRequest = BuildWebRequest(url: redirectUrl, auth: GetAuthForURL(_auth, _url, redirectUrl), connectionGroupName: _connectionGroupName, cacheLastModifiedTime: _cacheLastModifiedTime);
                ReplaceRequest(redirectionRequest);
                HttpWebResponse redirectionResponse = (HttpWebResponse)redirectionRequest.GetResponse();
                SetResponse(redirectionResponse);
                return CreateThrottledStream(redirectionResponse.GetResponseStream());
            }

            // Closes the meta refresh page's response, which the redirect replaces
            private void ReplaceRequest(HttpWebRequest request) {
                lock (_sync) {
                    ThrowIfAborting();
                    CloseQuietly(_response);
                    _response = null;
                    _request = request;
                }
            }

            private void SetResponse(HttpWebResponse response) {
                lock (_sync) {
                    if (_aborting) CloseQuietly(response);
                    ThrowIfAborting();
                    _response = response;
                }
            }

            // Returns false if the download has already been aborted
            private bool StartReading(Stream responseStream) {
                lock (_sync) {
                    if (_aborting) {
                        CloseQuietly(responseStream);
                        return false;
                    }
                    _responseStream = responseStream;
                    _onResponse(_response);
                    _buff = new byte[ReadBufferSize];
                    _readStartTicks = TickCount.Now;
                    return true;
                }
            }

            // BeginRead runs without holding _sync because ThrottledStream may sleep in it. If an
            // abort closes the stream meanwhile, the sleep ends and BeginRead (or the later EndRead)
            // fails; AbortInternal then does nothing. Only one read is ever in flight, so _buff is
            // not shared between reads.
            private void OnRead(IAsyncResult readResultParam) {
                try {
                    Stream responseStream = HandleReadResult(readResultParam);
                    if (responseStream == null) return;
                    IAsyncResult readResult = responseStream.BeginRead(_buff, 0, _buff.Length, OnRead, null);
                    AbortOnTimeout(readResult, ReadTimeoutMS, "Timed out while reading response.");
                }
                catch (Exception ex) {
                    AbortInternal(ex);
                }
            }

            // Returns the stream to read next, or null when the download has ended (completed or aborted)
            private Stream HandleReadResult(IAsyncResult readResultParam) {
                lock (_sync) {
                    if (_aborting) return null;
                    if (readResultParam == null || ReadChunk(readResultParam)) return _responseStream;
                    return null;
                }
            }

            // Called while holding _sync. Returns false when the download has completed. Each
            // read is limited by ReadTimeoutMS, and the whole download by the stream read deadline.
            private bool ReadChunk(IAsyncResult readResultParam) {
                int bytesRead = _responseStream.EndRead(readResultParam);
                if (bytesRead == 0) {
                    _request = null;
                    _completed = true;
                    _onComplete();
                    _aborting = true;
                    Cleanup();
                    return false;
                }
                _totalBytesRead += bytesRead;
                if (IsPastStreamReadDeadline(_readStartTicks, _responseStream, _totalBytesRead)) {
                    throw new Exception("Timed out while reading response.");
                }
                _onDownloadChunk(_buff, bytesRead);
                return true;
            }
        }
    }
}