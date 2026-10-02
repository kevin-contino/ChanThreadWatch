using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web;

namespace JDP {
    public static class General {
        public static string Version {
            get {
                Version ver = Assembly.GetExecutingAssembly().GetName().Version;
                return ver.Major + "." + ver.Minor + "." + ver.Revision;
            }
        }

        public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

        public static string ReleaseDate {
            get { return "2019-Jan-13"; }
        }

        public static string ProgramURL {
            get { return "https://github.com/SuperGouge/ChanThreadWatch/releases"; }
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

        private static Exception TranslateWebException(Exception ex) {
            WebException webEx = ex as WebException;
            if (webEx == null || webEx.Status != WebExceptionStatus.ProtocolError) return ex;
            HttpStatusCode code = ((HttpWebResponse)webEx.Response).StatusCode;
            if (code == HttpStatusCode.NotFound) return new HTTP404Exception();
            if (code == HttpStatusCode.NotModified) return new HTTP304Exception();
            return ex;
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

        public static string DownloadPageToString(string url) {
            HttpWebRequest request = BuildWebRequest(url: url);
            HttpWebResponse response = null;
            Stream responseStream = null;
            MemoryStream memoryStream = null;
            try {
                response = (HttpWebResponse)request.GetResponse();
                responseStream = response.GetResponseStream();
                memoryStream = new MemoryStream();
                CopyStream(responseStream, memoryStream);
                byte[] pageBytes = memoryStream.ToArray();
                Encoding encoding = DetectHTMLEncoding(pageBytes, response.ContentType);
                return encoding.GetString(pageBytes);
            }
            finally {
                CloseQuietly(responseStream);
                CloseQuietly(response);
                CloseQuietly(memoryStream);
            }
        }

        private static HttpWebRequest BuildWebRequest(string url, string auth = null, string connectionGroupName = null, string referer = null, DateTime? cacheLastModifiedTime = null) {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            if (connectionGroupName != null) {
                request.ConnectionGroupName = connectionGroupName;
            }
            // 4chan blocks (HTTP 403) non-browser user agents, so default to a browser-like one
            request.UserAgent = GetUserAgent();
            if (cacheLastModifiedTime != null) {
                request.IfModifiedSince = cacheLastModifiedTime.Value;
            }
            if (!String.IsNullOrEmpty(auth)) {
                Encoding encoding = Encoding.GetEncoding("iso-8859-1");
                request.Headers.Add("Authorization", "Basic " + Convert.ToBase64String(encoding.GetBytes(auth)));
            }
            if (!String.IsNullOrEmpty(referer)) {
                request.Referer = referer;
            }
            return request;
        }

        private static string GetUserAgent() {
            return (Settings.UseCustomUserAgent == true) ? Settings.CustomUserAgent : DefaultUserAgent;
        }

        private static void CopyStream(Stream srcStream, params Stream[] dstStreams) {
            byte[] data = new byte[8192];
            while (true) {
                int dataLen = srcStream.Read(data, 0, data.Length);
                if (dataLen == 0) break;
                foreach (Stream dstStream in dstStreams) {
                    if (dstStream != null) {
                        dstStream.Write(data, 0, dataLen);
                    }
                }
            }
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

        private static bool IsFilePathTooLong(string path) {
            try {
                using (File.Create(path)) { }
                try { File.Delete(path); }
                catch { }
                return false;
            }
            catch (PathTooLongException) {
                return true;
            }
            catch (DirectoryNotFoundException) {
                // Workaround for Mono
                return true;
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
            using (MD5CryptoServiceProvider hashAlgo = new MD5CryptoServiceProvider()) {
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

        public static void WriteReplacedString(string str, List<ReplaceInfo> replaceList, TextWriter outStream) {
            int offset = 0;
            replaceList.Sort((x, y) => x.Offset.CompareTo(y.Offset));
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
            HashSet<int> existingOffsets = new HashSet<int>();

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

            AddURLAttributeReplaces(htmlParser, pageURL, replaceList, existingOffsets);
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
            foreach (HTMLTag tag in htmlParser.FindStartTags("a", "img", "script", "link")) {
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
            if (tag.NameEqualsAny("img", "script")) return tag.GetAttribute("src");
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
                    string[] lines = File.ReadAllLines(path);
                    // Never replace the backup with a thread list that wouldn't load
                    if (!ThreadListFile.IsValid(lines)) return;
                    TextFile.WriteAllLinesAtomic(path + ".bak", lines);
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
        // All state changes happen while holding _sync.
        private sealed class AsyncDownload {
            private const int ReadBufferSize = 8192;
            private const int RequestTimeoutMS = 60000;
            private const int ReadTimeoutMS = 60000;

            private readonly object _sync = new object();
            private readonly string _auth;
            private readonly string _connectionGroupName;
            private readonly DateTime? _cacheLastModifiedTime;
            private readonly Action<HttpWebResponse> _onResponse;
            private readonly Action<byte[], int> _onDownloadChunk;
            private readonly Action _onComplete;
            private readonly Action<Exception> _onException;
            private bool _aborting;
            private HttpWebRequest _request;
            private HttpWebResponse _response;
            private Stream _responseStream;
            private byte[] _buff;
            private string _url;

            public AsyncDownload(string auth, string connectionGroupName, DateTime? cacheLastModifiedTime, Action<HttpWebResponse> onResponse, Action<byte[], int> onDownloadChunk, Action onComplete, Action<Exception> onException) {
                _auth = auth;
                _connectionGroupName = connectionGroupName;
                _cacheLastModifiedTime = cacheLastModifiedTime;
                _onResponse = onResponse;
                _onDownloadChunk = onDownloadChunk;
                _onComplete = onComplete;
                _onException = onException;
            }

            public void Start(string url, string referer) {
                lock (_sync) {
                    _url = url;
                    try {
                        _request = BuildWebRequest(url: url, auth: _auth, connectionGroupName: _connectionGroupName, cacheLastModifiedTime: _cacheLastModifiedTime, referer: referer);
                        // Unfortunately BeginGetResponse blocks until the DNS lookup has finished
                        IAsyncResult requestResult = _request.BeginGetResponse(OnGetResponse, null);
                        AbortOnTimeout(requestResult, RequestTimeoutMS, "Timed out while waiting for response.");
                    }
                    catch (Exception ex) {
                        AbortInternal(ex);
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
                    Cleanup();
                    _onException(ex);
                }
            }

            private void AbortOnTimeout(IAsyncResult asyncResult, int timeoutMS, string message) {
                ThreadPool.RegisterWaitForSingleObject(asyncResult.AsyncWaitHandle,
                    (state, timedOut) => {
                        if (!timedOut) return;
                        AbortInternal(new Exception(message));
                    }, null, timeoutMS, true);
            }

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

            private void OnGetResponse(IAsyncResult requestResultParam) {
                lock (_sync) {
                    try {
                        if (_aborting) return;
                        _response = (HttpWebResponse)_request.EndGetResponse(requestResultParam);
                        OpenResponseStream();
                        _onResponse(_response);
                        _buff = new byte[ReadBufferSize];
                        OnRead(null);
                    }
                    catch (Exception ex) {
                        AbortInternal(TranslateWebException(ex));
                    }
                }
            }

            private void OpenResponseStream() {
                if (GetMIMETypeFromContentType(_response.ContentType) == "text/html") {
                    OpenHTMLResponseStream();
                }
                else {
                    _responseStream = CreateThrottledStream(_response.GetResponseStream());
                }
            }

            // Buffers the page so it can be checked for a meta refresh redirect, and follows it if present
            private void OpenHTMLResponseStream() {
                var memoryStream = new MemoryStream();
                CopyStream(CreateThrottledStream(_response.GetResponseStream()), memoryStream);
                memoryStream.Position = 0;
                byte[] redirectPageBytes = memoryStream.ToArray();
                Encoding pageEncoding = DetectHTMLEncoding(redirectPageBytes, _response.ContentType);
                string metaRedirectHtml = pageEncoding.GetString(redirectPageBytes);
                memoryStream.Position = 0;
                _responseStream = memoryStream;
                string redirectUrl = GetRedirectUrl(metaRedirectHtml, _response.ResponseUri.AbsoluteUri);
                if (!string.IsNullOrEmpty(redirectUrl)) {
                    HttpWebRequest redirectionRequest = BuildWebRequest(url: redirectUrl, auth: GetAuthForURL(_auth, _url, redirectUrl), connectionGroupName: _connectionGroupName, cacheLastModifiedTime: _cacheLastModifiedTime);
                    _response = (HttpWebResponse)redirectionRequest.GetResponse();
                    _responseStream = CreateThrottledStream(_response.GetResponseStream());
                }
            }

            private void OnRead(IAsyncResult readResultParam) {
                lock (_sync) {
                    try {
                        if (_aborting) return;
                        if (!HandleReadResult(readResultParam)) return;
                        IAsyncResult readResult = _responseStream.BeginRead(_buff, 0, _buff.Length, OnRead, null);
                        AbortOnTimeout(readResult, ReadTimeoutMS, "Timed out while reading response.");
                    }
                    catch (Exception ex) {
                        AbortInternal(ex);
                    }
                }
            }

            // Returns false when the download has completed and no further reads should be started
            private bool HandleReadResult(IAsyncResult readResultParam) {
                if (readResultParam == null) return true;
                int bytesRead = _responseStream.EndRead(readResultParam);
                if (bytesRead == 0) {
                    _request = null;
                    _onComplete();
                    _aborting = true;
                    Cleanup();
                    return false;
                }
                _onDownloadChunk(_buff, bytesRead);
                return true;
            }
        }
    }
}