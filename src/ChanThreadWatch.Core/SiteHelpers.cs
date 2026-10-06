using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace JDP {
    public static class SiteHelpers {
        private static readonly Dictionary<string, Type> _siteHelpers = new Dictionary<string, Type> {
            { "4chan.org", typeof(FourChanSiteHelper) },
            { "4channel.org", typeof(FourChanSiteHelper) },
            { "8ch.net", typeof(InfinitechanSiteHelper) },

            { "warosu.org", typeof(FuukaSiteHelper) },

            { "4plebs.org", typeof(FoolFuukaSiteHelper) },
            { "archive.alice.al", typeof(FoolFuukaSiteHelper) },
            { "desuarchive.org", typeof(FoolFuukaSiteHelper) },
            { "arch.b4k.dev", typeof(FoolFuukaSiteHelper) },
            { "arch.b4k.co", typeof(FoolFuukaSiteHelper) },
            { "archived.moe", typeof(FoolFuukaSiteHelper) },
            { "thebarchive.com", typeof(FoolFuukaSiteHelper) },
            { "archiveofsins.com", typeof(FoolFuukaSiteHelper) },
            { "archive.rebeccablacktech.com", typeof(FoolFuukaSiteHelper) },
            { "rbt.asia", typeof(FoolFuukaSiteHelper) },

            { "endchan.net", typeof(LynxChanSiteHelper) },
            { "endchan.org", typeof(LynxChanSiteHelper) }
        };

        // Exact host name overrides, only set by tests (e.g. to parse a loopback server as 4chan)
        private static readonly Dictionary<string, Type> _testHostHelpers = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        internal static void RegisterHostForTesting(string host, Type helperType) {
            lock (_testHostHelpers) {
                _testHostHelpers[host] = helperType;
            }
        }

        internal static void UnregisterHostForTesting(string host) {
            lock (_testHostHelpers) {
                _testHostHelpers.Remove(host);
            }
        }

        public static SiteHelper GetInstance(string host) {
            Type type = FindTestHostHelperType(host) ?? FindHelperType(host);
            if (type != null && type.IsSubclassOf(typeof(SiteHelper))) {
                return (SiteHelper)Activator.CreateInstance(type);
            }
            return new SiteHelper();
        }

        // True when GetInstance gives a site helper of its own for the host rather than the generic one. The host
        // is matched as GetInstance matches it (ignoring case only), so a caller passes the host GetInstance will get,
        // Uri.Host, and refuses a URL whose Uri.Host differs from its Uri.IdnHost; a host with a trailing dot
        // ("4chan.org.") is not known.
        public static bool IsKnownHost(string host) {
            if (String.IsNullOrEmpty(host)) return false;
            Type type = FindTestHostHelperType(host) ?? FindHelperType(host);
            return type != null && type.IsSubclassOf(typeof(SiteHelper));
        }

        private static Type FindTestHostHelperType(string host) {
            lock (_testHostHelpers) {
                Type type;
                return _testHostHelpers.TryGetValue(host, out type) ? type : null;
            }
        }

        // Returns the helper type of the longest matching domain suffix, or null if none matches
        private static Type FindHelperType(string host) {
            Type type = null;
            string[] hostSplit = host.ToLower(CultureInfo.InvariantCulture).Split('.');
            for (int i = hostSplit.Length - 1; i >= 0; i--) {
                string domain = String.Join(".", hostSplit, i, hostSplit.Length - i);
                if (_siteHelpers.ContainsKey(domain)) {
                    type = _siteHelpers[domain];
                }
            }
            return type;
        }
    }

    public class SiteHelper {
        protected string _url = String.Empty;
        protected HTMLParser _htmlParser;

        // Set for a guarded thread (ThreadWatcher.Guarded) before anything is downloaded, so a request
        // the site helper sends itself (the 4chan slug lookup) goes out on the guarded clients
        public bool Guarded { get; set; }

        public void SetURL(string url) {
            _url = url;
        }

        public void SetHTMLParser(HTMLParser htmlParser) {
            _htmlParser = htmlParser;
        }

        public HTMLParser GetHTMLParser() {
            return _htmlParser;
        }

        protected string[] SplitURL() {
            return SplitURL(_url);
        }

        protected string[] SplitURL(string url) {
            int pos = url.IndexOf("://", StringComparison.Ordinal);
            if (pos == -1) return new string[0];
            return url.Substring(pos + 3).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        }

        // The second-level domain name (e.g. "4chan"), or the whole host if it is an IP address
        // or a single-label name (e.g. "localhost")
        public virtual string GetSiteName() {
            Uri uri = new Uri(_url);
            if (uri.HostNameType == UriHostNameType.IPv4 || uri.HostNameType == UriHostNameType.IPv6) return uri.Host.Replace(':', '-');
            string[] hostSplit = uri.Host.Split('.');
            return (hostSplit.Length >= 2) ? hostSplit[hostSplit.Length - 2] : uri.Host;
        }

        public virtual string GetBoardName() {
            string[] urlSplit = SplitURL();
            return (urlSplit.Length >= 3) ? urlSplit[1] : String.Empty;
        }

        public virtual string GetThreadName() {
            string[] urlSplit = SplitURL();
            if (urlSplit.Length >= 3) {
                string page = urlSplit[urlSplit.Length - 1];
                int pos = page.IndexOf('?');
                if (pos != -1) page = page.Substring(0, pos);
                pos = page.LastIndexOf('.');
                if (pos != -1) page = page.Substring(0, pos);
                return page;
            }
            return String.Empty;
        }

        protected virtual string GetThreadName(SlugType slugType) {
            return GetThreadName(_url, slugType);
        }

        protected virtual string GetThreadName(string url, SlugType slugType) {
            return String.Empty;
        }

        public virtual string GetThreadID() {
            return GetThreadName();
        }

        public virtual string GetPageID() {
            return String.Join("/", new[] { GetSiteName(), GetBoardName(), GetThreadID() });
        }

        public virtual bool HasSlug() {
            return HasSlug(_url);
        }

        protected virtual bool HasSlug(string url) {
            return false;
        }

        public virtual bool IsBoardHighTurnover() {
            return false;
        }

        protected virtual string ImageURLKeyword {
            get { return "/src/"; }
        }

        protected virtual bool IsImage(HTMLTag linkTag) {
            string url = General.GetAbsoluteURL(_url, HttpUtility.HtmlDecode(linkTag.GetAttributeValueOrEmpty("href")));
            return url != null && url.IndexOf(ImageURLKeyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public virtual List<ImageInfo> GetImages(List<ReplaceInfo> replaceList, List<ThumbnailInfo> thumbnailList, bool local = false) {
            HashSet<string> imageFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> thumbnailFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<ImageInfo> imageList = new List<ImageInfo>();

            foreach (HTMLTag linkTag in _htmlParser.FindStartTags("a")) {
                HTMLAttribute attribute = linkTag.GetAttribute("href");
                string url;
                HTMLTag linkEndTag = FindImageLinkEndTag(linkTag, attribute, out url);
                if (linkEndTag == null) continue;

                ImageInfo image = CreateLinkedImage(url);
                if (image == null) continue;
                if (replaceList != null) {
                    AddAttributeReplace(replaceList, attribute, ReplaceType.ImageLinkHref, image.FileName);
                }

                ThumbnailInfo thumb = CreateLinkedThumbnail(linkTag, linkEndTag, replaceList);

                AddNewImage(imageList, imageFileNames, image);
                AddNewThumbnail(thumbnailList, thumbnailFileNames, thumb);
            }

            return imageList;
        }

        // Returns the end tag of an image link, or null if the tag is not a resolvable image link.
        private HTMLTag FindImageLinkEndTag(HTMLTag linkTag, HTMLAttribute hrefAttribute, out string url) {
            url = null;
            if (hrefAttribute == null) return null;
            url = General.GetAbsoluteURL(_url, HttpUtility.HtmlDecode(hrefAttribute.Value));
            if (url == null || !IsImage(linkTag)) return null;
            return _htmlParser.FindCorrespondingEndTag(linkTag);
        }

        private ImageInfo CreateLinkedImage(string url) {
            ImageInfo image = new ImageInfo { Poster = String.Empty };

            image.URL = url;
            if (image.URL == null || image.FileName.Length == 0) return null;
            // A URL embedded in a redirect link is downloaded directly, with the redirect link as
            // the referer. Any other image is downloaded with the page as the referer.
            int pos = Math.Max(
                image.URL.LastIndexOf("http://", StringComparison.OrdinalIgnoreCase),
                image.URL.LastIndexOf("https://", StringComparison.OrdinalIgnoreCase));
            if (pos <= 0) {
                image.Referer = _url;
            }
            else {
                image.Referer = image.URL;
                image.URL = image.URL.Substring(pos);
            }
            return image;
        }

        private ThumbnailInfo CreateLinkedThumbnail(HTMLTag linkTag, HTMLTag linkEndTag, List<ReplaceInfo> replaceList) {
            HTMLTag imageTag = _htmlParser.FindStartTag(linkTag, linkEndTag, "img");
            if (imageTag == null) return null;
            HTMLAttribute attribute = imageTag.GetAttribute("src");
            if (attribute == null) return null;
            ThumbnailInfo thumb = CreateThumbnail(attribute.Value);
            if (thumb.URL == null) return null;
            if (replaceList != null) {
                AddAttributeReplace(replaceList, attribute, ReplaceType.ImageSrc, thumb.FileName);
            }
            return thumb;
        }

        private static void AddNewImage(List<ImageInfo> imageList, HashSet<string> imageFileNames, ImageInfo image) {
            if (imageFileNames.Contains(image.FileName)) return;
            imageList.Add(image);
            imageFileNames.Add(image.FileName);
        }

        private static void AddNewThumbnail(List<ThumbnailInfo> thumbnailList, HashSet<string> thumbnailFileNames, ThumbnailInfo thumb) {
            if (thumb == null || thumbnailFileNames.Contains(thumb.FileName)) return;
            thumbnailList.Add(thumb);
            thumbnailFileNames.Add(thumb.FileName);
        }

        // Tags that locate one posted file: the element that describes the file, the link around
        // the thumbnail, the link whose href is the full image, and the thumbnail image.
        protected class FileTags {
            public HTMLTagRange InfoTagRange { get; set; }
            public HTMLTagRange ThumbLinkTagRange { get; set; }
            public HTMLTag LinkStartTag { get; set; }
            public HTMLTag ThumbImageTag { get; set; }
            public string ImageURL { get; set; }
            public string ThumbURL { get; set; }
        }

        // Reads the image and thumbnail URLs. Returns null if the file tags or either URL is missing.
        protected static FileTags ReadFileURLs(FileTags file) {
            if (file == null) return null;
            file.ImageURL = file.LinkStartTag.GetAttributeValue("href");
            if (file.ImageURL == null) return null;
            file.ThumbURL = file.ThumbImageTag.GetAttributeValue("src");
            return file.ThumbURL != null ? file : null;
        }

        protected ThumbnailInfo CreateThumbnail(string thumbURL) {
            return new ThumbnailInfo {
                URL = General.GetAbsoluteURL(_url, HttpUtility.HtmlDecode(thumbURL)),
                Referer = _url
            };
        }

        protected static bool IsMissingThumbnailData(ThumbnailInfo thumb) {
            return thumb.URL == null || thumb.FileName.Length == 0;
        }

        protected static bool IsMissingFileName(ImageInfo image) {
            return String.IsNullOrEmpty(image.URL) || image.FileName.Length == 0;
        }

        // Also true if the page gives a hash that could not be decoded.
        protected static bool IsMissingImageData(ImageInfo image) {
            return IsMissingFileName(image) || (image.HashType != HashType.None && image.Hash == null);
        }

        protected static bool IsMissingFileNameOrHash(ImageInfo image) {
            return IsMissingFileName(image) || image.Hash == null;
        }

        // Returns the tag ranges of the elements with the class inside the container, or none if
        // the container is missing.
        protected IEnumerable<HTMLTagRange> FindTagRangesWithClass(HTMLTagRange containingTagRange, string tagName, string className) {
            if (containingTagRange == null) return new HTMLTagRange[0];
            return Enumerable.Where(Enumerable.Select(Enumerable.Where(_htmlParser.FindStartTags(containingTagRange, tagName),
                t => HTMLParser.ClassAttributeValueHas(t, className)), t => _htmlParser.CreateTagRange(t)), r => r != null);
        }

        protected HTMLTag FindThreadDivStartTag() {
            return Enumerable.FirstOrDefault(Enumerable.Where(_htmlParser.FindStartTags("div"), t => HTMLParser.ClassAttributeValueHas(t, "thread")));
        }

        // Returns the tag ranges with their ids in document order. Skips tag ranges without an id,
        // and keeps only the first tag range of each id.
        protected static List<KeyValuePair<string, HTMLTagRange>> GetTagRangesWithUniqueID(IEnumerable<HTMLTagRange> tagRanges) {
            List<KeyValuePair<string, HTMLTagRange>> result = new List<KeyValuePair<string, HTMLTagRange>>();
            HashSet<string> ids = new HashSet<string>();
            foreach (HTMLTagRange tagRange in tagRanges) {
                string id = tagRange.StartTag.GetAttributeValue("id");
                if (id == null || !ids.Add(id)) continue;
                result.Add(new KeyValuePair<string, HTMLTagRange>(id, tagRange));
            }
            return result;
        }

        protected static bool IsResurrected(Dictionary<string, HTMLTagRange> resurrectedTagRanges, HTMLTagRange tagRange) {
            string id = tagRange.StartTag.GetAttributeValue("id");
            return id != null && resurrectedTagRanges.ContainsKey(id);
        }

        // Returns where the "[Deleted]" marker goes in the HTML of a dead post: after the post's
        // checkbox, or after the post's start tag if it has no checkbox.
        protected static int GetDeletedMarkerOffset(HTMLParser parser, HTMLTagRange postTagRange) {
            HTMLTag markerAfterTag = parser.FindStartTag(postTagRange, "input") ?? postTagRange.StartTag;
            return markerAfterTag.EndOffset - postTagRange.Offset;
        }

        // Returns an href attribute with the absolute URL of the link, so that a quote link to a
        // thread that is not followed still points at the live page in the saved copy.
        protected string GetLiveLinkHrefAttribute(string href) {
            return "href=\"" + HttpUtility.HtmlAttributeEncode(General.GetAbsoluteURL(_url, HttpUtility.HtmlDecode(href))) + "\"";
        }

        protected string GetTitleOrInnerHTML(HTMLTagRange tagRange) {
            return tagRange.StartTag.GetAttributeValue("title") ?? _htmlParser.GetInnerHTML(tagRange);
        }

        protected string GetPosterFromNameAndTrip(HTMLTagRange nameTagRange, HTMLTagRange tripTagRange) {
            string name = _htmlParser.GetInnerHTML(nameTagRange);
            return FormatPoster(name, tripTagRange != null ? _htmlParser.GetInnerHTML(tripTagRange) : null);
        }

        // Returns the name with its tripcode, or the name alone unless it is the default "Anonymous".
        protected static string FormatPoster(string name, string trip) {
            if (trip != null) return name + trip;
            return name != "Anonymous" ? name : String.Empty;
        }

        // Adds the thumbnail, except that only the first spoiler placeholder thumbnail is added.
        protected static void AddThumbnail(List<ThumbnailInfo> thumbnailList, ThumbnailInfo thumb, bool isSpoiler, ref bool seenSpoiler) {
            if (isSpoiler && seenSpoiler) return;
            thumbnailList.Add(thumb);
            if (isSpoiler) seenSpoiler = true;
        }

        protected static void AddAttributeReplace(List<ReplaceInfo> replaceList, HTMLAttribute attribute, ReplaceType type, string tag) {
            if (attribute == null) return;
            replaceList.Add(
                new ReplaceInfo {
                    Offset = attribute.Offset,
                    Length = attribute.Length,
                    Type = type,
                    Tag = tag
                });
        }

        // Adds replacements for the image link, the thumbnail link and the thumbnail image of a file.
        protected static void AddFileReplaces(List<ReplaceInfo> replaceList, FileTags file, ImageInfo image, ThumbnailInfo thumb) {
            if (replaceList == null) return;
            AddAttributeReplace(replaceList, file.LinkStartTag.GetAttribute("href"), ReplaceType.ImageLinkHref, image.FileName);
            AddAttributeReplace(replaceList, file.ThumbLinkTagRange.StartTag.GetAttribute("href"), ReplaceType.ImageLinkHref, image.FileName);
            AddAttributeReplace(replaceList, file.ThumbImageTag.GetAttribute("src"), ReplaceType.ImageSrc, thumb.FileName);
        }

        // Adds replacements that rewrite the file attributes of a resurrected post with their original values.
        protected static void AddResurrectedFileReplaces(List<ReplaceInfo> replaceList, FileTags file) {
            AddResurrectedAttributeReplace(replaceList, file.LinkStartTag.GetAttribute("href"), ReplaceType.ImageLinkHref, "href");
            AddResurrectedAttributeReplace(replaceList, file.ThumbLinkTagRange.StartTag.GetAttribute("href"), ReplaceType.ImageLinkHref, "href");
            AddResurrectedAttributeReplace(replaceList, file.ThumbImageTag.GetAttribute("src"), ReplaceType.ImageSrc, "src");
        }

        private static void AddResurrectedAttributeReplace(List<ReplaceInfo> replaceList, HTMLAttribute attribute, ReplaceType type, string attributeName) {
            if (attribute == null) return;
            replaceList.Add(
                new ReplaceInfo {
                    Offset = attribute.Offset,
                    Length = attribute.Length,
                    Type = type,
                    Tag = String.Empty,
                    Value = attributeName + "=\"" + General.HtmlAttributeEncode(attribute.Value, false) + "\""
                });
        }

        // Applies the replacements to the current page and parses the result.
        protected void ApplyReplaces(List<ReplaceInfo> replaceList) {
            StringBuilder sb = new StringBuilder();
            using (StringWriter sw = new StringWriter(sb)) {
                General.WriteReplacedString(_htmlParser.PreprocessedHTML, replaceList, sw);
            }
            _htmlParser = new HTMLParser(sb.ToString());
        }

        public virtual HashSet<string> GetCrossLinks(List<ReplaceInfo> replaceList, bool interBoardAutoFollow) {
            return new HashSet<string>();
        }

        public virtual void ResurrectDeadPosts(HTMLParser previousParser, List<ReplaceInfo> replaceList) {
        }

        public virtual string GetNextPageURL() {
            return null;
        }

        // False if the downloaded page is recognizably not a thread (e.g. an error, ban or captcha
        // page served with 200 OK). Sites whose markup is not known count every page as a thread.
        public virtual bool IsThreadPage() {
            return true;
        }

        // The OfflinePageScript site whose markup this site's saved pages have, or null to save
        // pages without the script. Sites whose markup is not known get no script.
        public virtual string GetOfflinePageScriptSite() {
            return null;
        }
    }

    public class FourChanSiteHelper : FourChanLookAlikeSiteHelper {
        public override string GetThreadName() {
            if (HasSlug()) {
                return GetThreadName(Settings.SlugType);
            }
            if (Settings.UseSlug == true) {
                try {
                    HTMLParser parser = new HTMLParser(General.DownloadPageToString(_url, Guarded));
                    HTMLTag canonicalLinkTag = Enumerable.FirstOrDefault(Enumerable.Where(parser.FindStartTags(parser.CreateTagRange(parser.FindStartTag("head")), "link"), t => t.GetAttributeValueOrEmpty("rel").Equals("canonical")));
                    return GetThreadName(canonicalLinkTag.GetAttributeValueOrEmpty("href"), Settings.SlugType);
                }
                catch {
                    return GetThreadID();
                }
            }
            return GetThreadID();
        }

        protected override string GetThreadName(string url, SlugType slugType) {
            if (ShouldIgnoreSlug(url)) return GetThreadID();
            string[] urlSplit = SplitURL(url);
            switch (slugType) {
                case SlugType.First:
                    return urlSplit[urlSplit.Length - 1] + "_" + urlSplit[urlSplit.Length - 2];
                case SlugType.Last:
                    return urlSplit[urlSplit.Length - 2] + "_" + urlSplit[urlSplit.Length - 1];
                case SlugType.Only:
                    return urlSplit[urlSplit.Length - 1];
                default:
                    return urlSplit[urlSplit.Length - 2];
            }
        }

        private bool ShouldIgnoreSlug(string url) {
            return Settings.UseSlug != true || !HasSlug(url);
        }

        public override string GetThreadID() {
            string[] urlSplit = SplitURL();
            // A URL without "://" (e.g. a saved page path) has no segments
            if (urlSplit.Length == 0) return String.Empty;
            return HasSlug() ? urlSplit[urlSplit.Length - 2] : urlSplit[urlSplit.Length - 1];
        }

        protected override bool HasSlug(string url) {
            return url.Contains("/thread/") && SplitURL(url).Length == 5;
        }

        public override HashSet<string> GetCrossLinks(List<ReplaceInfo> replaceList, bool interBoardAutoFollow) {
            HashSet<string> crossLinks = new HashSet<string>();

            foreach (HTMLTagRange postMessageTagRange in Enumerable.Where(Enumerable.Select(Enumerable.Where(_htmlParser.FindStartTags("blockquote"),
                t => HTMLParser.ClassAttributeValueHas(t, "postMessage")), t => _htmlParser.CreateTagRange(t)), r => r != null))
            {
                AddQuoteLinks(postMessageTagRange, crossLinks, replaceList, interBoardAutoFollow);
                AddDeadLinkReplaces(postMessageTagRange, replaceList);
            }
            return crossLinks;
        }

        private void AddQuoteLinks(HTMLTagRange postMessageTagRange, HashSet<string> crossLinks, List<ReplaceInfo> replaceList, bool interBoardAutoFollow) {
            foreach (HTMLTag quoteLinkTag in Enumerable.Where(_htmlParser.FindStartTags(postMessageTagRange, "a"),
                t => HTMLParser.ClassAttributeValueHas(t, "quotelink")))
            {
                HTMLAttribute attribute = quoteLinkTag.GetAttribute("href");
                if (attribute == null) continue;
                string href = RemoveFragment(attribute.Value);
                string url = General.GetAbsoluteURL(_url, href);
                if (url == null || IsSkippedQuoteLink(href, interBoardAutoFollow)) continue;
                crossLinks.Add(url);
                if (replaceList != null) {
                    replaceList.Add(
                        new ReplaceInfo {
                            Offset = attribute.Offset,
                            Length = attribute.Length,
                            Type = ReplaceType.QuoteLinkHref,
                            Tag = href.Replace("/thread", "").Insert(0, GetSiteName()),
                            Value = GetLiveLinkHrefAttribute(attribute.Value)
                        });
                }
            }
        }

        private static string RemoveFragment(string url) {
            return url.Substring(0, url.Contains("#") ? url.IndexOf('#') : url.Length);
        }

        private bool IsSkippedQuoteLink(string href, bool interBoardAutoFollow) {
            return !href.Contains("/thread/") || (!interBoardAutoFollow && GetBoardName() != href.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)[0]);
        }

        private void AddDeadLinkReplaces(HTMLTagRange postMessageTagRange, List<ReplaceInfo> replaceList) {
            if (replaceList == null) return;
            foreach (HTMLTagRange deadLinkTagRange in FindTagRangesWithClass(postMessageTagRange, "span", "deadlink")) {
                string tag = GetDeadLinkTag(HttpUtility.HtmlDecode(_htmlParser.GetInnerHTML(deadLinkTagRange)));
                if (tag == null) continue;
                replaceList.Add(
                    new ReplaceInfo {
                        Offset = deadLinkTagRange.Offset,
                        Length = deadLinkTagRange.Length,
                        Type = ReplaceType.DeadLink,
                        Tag = tag,
                        Value = _htmlParser.GetHTML(deadLinkTagRange)
                    });
            }
        }

        // Returns the site/board/post tag of a dead link such as ">>123" or ">>>/g/123", or null if
        // the text has no post number.
        private string GetDeadLinkTag(string deadLinkInnerHTML) {
            if (deadLinkInnerHTML.Contains(">>>")) {
                string[] split = deadLinkInnerHTML.Split('/');
                return split.Length >= 3 ? String.Join("/", new[] { GetSiteName(), split[1], split[2] }) : null;
            }
            return deadLinkInnerHTML.Length > 2 ? String.Join("/", new[] { GetSiteName(), GetBoardName(), deadLinkInnerHTML.Substring(2) }) : null;
        }

        // Leaves the page unchanged if it has no thread div, e.g. an error page.
        public override void ResurrectDeadPosts(HTMLParser previousParser, List<ReplaceInfo> replaceList) {
            if (previousParser == null || FindThreadDivStartTag() == null) return;
            Dictionary<string, HTMLTagRange> resurrectedPostContainers = new Dictionary<string, HTMLTagRange>();

            ApplyReplaces(GetDeadPostReplaces(previousParser, resurrectedPostContainers));
            ApplyReplaces(GetDeadLinkReplaces(resurrectedPostContainers));

            if (replaceList == null) return;
            AddResurrectedImageReplaces(replaceList, resurrectedPostContainers);
        }

        // Returns replacements that insert the post containers missing from the current page, and
        // records each inserted container in resurrectedPostContainers.
        private List<ReplaceInfo> GetDeadPostReplaces(HTMLParser previousParser, Dictionary<string, HTMLTagRange> resurrectedPostContainers) {
            List<ReplaceInfo> deadPostReplaceList = new List<ReplaceInfo>();
            Dictionary<string, HTMLTagRange> newPostContainers = new Dictionary<string, HTMLTagRange>();
            foreach (KeyValuePair<string, HTMLTagRange> postContainer in GetTagRangesWithUniqueID(Enumerable.Where(Enumerable.Select(Enumerable.Where(_htmlParser.FindStartTags("div"),
                t => HTMLParser.ClassAttributeValueHas(t, "postContainer")), t => _htmlParser.CreateTagRange(t)), r => r != null)))
            {
                newPostContainers.Add(postContainer.Key, postContainer.Value);
            }

            HTMLTagRange lastExistingPostContainerTagRange = null;
            foreach (KeyValuePair<string, HTMLTagRange> previousPostContainer in GetTagRangesWithUniqueID(Enumerable.Where(Enumerable.Select(Enumerable.Where(previousParser.FindStartTags("div"),
                t => HTMLParser.ClassAttributeValueHas(t, "postContainer")), t => previousParser.CreateTagRange(t)), r => r != null)))
            {
                HTMLTagRange tempTagRange;
                if (newPostContainers.TryGetValue(previousPostContainer.Key, out tempTagRange)) {
                    lastExistingPostContainerTagRange = tempTagRange;
                    continue;
                }
                deadPostReplaceList.Add(CreateDeadPostReplace(previousParser, previousPostContainer.Value, lastExistingPostContainerTagRange));
                resurrectedPostContainers.Add(previousPostContainer.Key, previousPostContainer.Value);
            }
            return deadPostReplaceList;
        }

        private ReplaceInfo CreateDeadPostReplace(HTMLParser previousParser, HTMLTagRange previousPostContainerTagRange, HTMLTagRange lastExistingPostContainerTagRange) {
            int offset = lastExistingPostContainerTagRange != null ? lastExistingPostContainerTagRange.EndOffset : FindThreadDivStartTag().EndOffset;
            string value = previousParser.GetHTML(previousPostContainerTagRange);
            if (!value.Contains("<strong style=\"color: #FF0000\">[Deleted]</strong>")) {
                value = value.Insert(GetDeletedMarkerOffset(previousParser, previousPostContainerTagRange), "<strong style=\"color: #FF0000\">[Deleted]</strong>");
            }
            return new ReplaceInfo {
                Offset = offset,
                Length = 0,
                Type = ReplaceType.DeadPost,
                Tag = previousPostContainerTagRange.StartTag.GetAttributeValue("id"),
                Value = value
            };
        }

        // Returns replacements that turn dead links to resurrected posts back into quote links.
        private List<ReplaceInfo> GetDeadLinkReplaces(Dictionary<string, HTMLTagRange> resurrectedPostContainers) {
            List<ReplaceInfo> deadLinkReplaceList = new List<ReplaceInfo>();
            foreach (HTMLTagRange deadLinkTagRange in Enumerable.Where(Enumerable.Select(Enumerable.Where(_htmlParser.FindStartTags("span"),
                    t => HTMLParser.ClassAttributeValueHas(t, "deadlink")), t => _htmlParser.CreateTagRange(t)), r => r != null))
            {
                string deadLinkInnerHTML = HttpUtility.HtmlDecode(_htmlParser.GetInnerHTML(deadLinkTagRange));
                if (deadLinkInnerHTML.Length <= 2 || deadLinkInnerHTML.Contains(">>>")) continue;
                string deadLinkID = deadLinkInnerHTML.Substring(2);
                if (resurrectedPostContainers.ContainsKey("pc" + deadLinkID)) {
                    deadLinkReplaceList.Add(
                        new ReplaceInfo {
                            Offset = deadLinkTagRange.Offset,
                            Length = deadLinkTagRange.Length,
                            Type = ReplaceType.DeadLink,
                            Tag = "pc" + deadLinkID,
                            Value = "<a class=\"quotelink\" href=\"#p" + deadLinkID + "\">>>" + deadLinkID + "</a>"
                        });
                }
            }
            return deadLinkReplaceList;
        }

        private void AddResurrectedImageReplaces(List<ReplaceInfo> replaceList, Dictionary<string, HTMLTagRange> resurrectedPostContainers) {
            foreach (HTMLTagRange postContainerTagRange in Enumerable.Where(Enumerable.Select(Enumerable.Where(_htmlParser.FindStartTags("div"),
                t => HTMLParser.ClassAttributeValueHas(t, "postContainer")), t => _htmlParser.CreateTagRange(t)), r => r != null))
            {
                if (!IsResurrected(resurrectedPostContainers, postContainerTagRange)) continue;

                FileTags file = FindFileTags(postContainerTagRange, "div");
                if (file == null) continue;

                AddResurrectedFileReplaces(replaceList, file);
            }
        }

        public override bool IsBoardHighTurnover() {
            return String.Equals(GetBoardName(), "b", StringComparison.OrdinalIgnoreCase);
        }
    }

    public class FourChanLookAlikeSiteHelper : SiteHelper {
        public override bool IsThreadPage() {
            return FindThreadDivStartTag() != null;
        }

        public override string GetOfflinePageScriptSite() {
            return OfflinePageScript.FourChan;
        }

        public override List<ImageInfo> GetImages(List<ReplaceInfo> replaceList, List<ThumbnailInfo> thumbnailList, bool local = false) {
            List<ImageInfo> imageList = new List<ImageInfo>();
            bool seenSpoiler = false;

            foreach (HTMLTagRange postTagRange in Enumerable.Where(Enumerable.Select(Enumerable.Where(_htmlParser.FindStartTags("div"),
                t => HTMLParser.ClassAttributeValueHas(t, "post")), t => _htmlParser.CreateTagRange(t)), r => r != null))
            {
                FileTags file = ReadFileURLs(FindFileTags(postTagRange, "div", "span"));
                if (file == null) continue;

                bool isSpoiler = HTMLParser.ClassAttributeValueHas(file.ThumbLinkTagRange.StartTag, "imgspoiler");

                ImageInfo image = CreateImage(postTagRange, file, isSpoiler);
                if (IsMissingImageData(image)) continue;

                ThumbnailInfo thumb = CreateThumbnail(file.ThumbURL);
                if (IsMissingThumbnailData(thumb)) continue;

                AddFileReplaces(replaceList, file, image, thumb);

                imageList.Add(image);
                AddThumbnail(thumbnailList, thumb, isSpoiler, ref seenSpoiler);
            }

            return imageList;
        }

        // Finds the file text, its link, the thumbnail link and the thumbnail image in a post.
        // Returns null if any of them is missing.
        protected FileTags FindFileTags(HTMLTagRange postTagRange, params string[] fileTextTagNames) {
            HTMLTagRange fileTextTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(postTagRange, fileTextTagNames), t => HTMLParser.ClassAttributeValueHas(t, "fileText"))));
            if (fileTextTagRange == null) return null;

            HTMLTagRange fileThumbLinkTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(postTagRange, "a"), t => HTMLParser.ClassAttributeValueHas(t, "fileThumb"))));
            if (fileThumbLinkTagRange == null) return null;

            HTMLTag fileTextLinkStartTag = _htmlParser.FindStartTag(fileTextTagRange, "a");
            if (fileTextLinkStartTag == null) return null;

            HTMLTag fileThumbImageTag = _htmlParser.FindStartTag(fileThumbLinkTagRange, "img");
            if (fileThumbImageTag == null) return null;

            return new FileTags {
                InfoTagRange = fileTextTagRange,
                ThumbLinkTagRange = fileThumbLinkTagRange,
                LinkStartTag = fileTextLinkStartTag,
                ThumbImageTag = fileThumbImageTag
            };
        }

        private ImageInfo CreateImage(HTMLTagRange postTagRange, FileTags file, bool isSpoiler) {
            string originalFileName = GetOriginalFileName(file, isSpoiler);
            string imageMD5 = file.ThumbImageTag.GetAttributeValue("data-md5");
            string poster = GetPoster(postTagRange);

            return new ImageInfo {
                URL = General.GetAbsoluteURL(_url, HttpUtility.HtmlDecode(file.ImageURL)),
                Referer = _url,
                HashType = imageMD5 != null ? HashType.MD5 : HashType.None,
                Hash = imageMD5 != null ? General.TryBase64Decode(imageMD5) : null,
                OriginalFileName = General.CleanFileName(HttpUtility.HtmlDecode(originalFileName) ?? ""),
                Poster = General.CleanFolderName(poster)
            };
        }

        private string GetOriginalFileName(FileTags file, bool isSpoiler) {
            if (isSpoiler) {
                return file.InfoTagRange.StartTag.GetAttributeValue("title");
            }
            return file.LinkStartTag.GetAttributeValue("title") ?? _htmlParser.GetInnerHTML(_htmlParser.CreateTagRange(file.LinkStartTag));
        }

        private string GetPoster(HTMLTagRange postTagRange) {
            HTMLTagRange nameBlockSpanTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(postTagRange, "span"), t => HTMLParser.ClassAttributeValueHas(t, "nameBlock"))));
            if (nameBlockSpanTagRange == null) return String.Empty;

            HTMLTagRange nameSpanTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(nameBlockSpanTagRange, "span"), t => HTMLParser.ClassAttributeValueHas(t, "name"))));

            HTMLTagRange posterTripSpanTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(nameBlockSpanTagRange, "span"), t => HTMLParser.ClassAttributeValueHas(t, "postertrip"))));

            HTMLTagRange idSpanTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(nameBlockSpanTagRange, "span"), t => HTMLParser.ClassAttributeValueHas(t, "hand"))));

            if (idSpanTagRange != null) return _htmlParser.GetInnerHTML(idSpanTagRange);
            if (nameSpanTagRange == null) return String.Empty;
            return GetPosterFromNameAndTrip(nameSpanTagRange, posterTripSpanTagRange);
        }
    }

    public class InfinitechanSiteHelper : SiteHelper {
        public override bool IsThreadPage() {
            return FindThreadDivStartTag() != null;
        }

        public override string GetOfflinePageScriptSite() {
            return OfflinePageScript.Vichan;
        }

        public override List<ImageInfo> GetImages(List<ReplaceInfo> replaceList, List<ThumbnailInfo> thumbnailList, bool local = false) {
            List<ImageInfo> imageList = new List<ImageInfo>();
            bool seenSpoiler = false;

            foreach (HTMLTagRange postTagRange in Enumerable.Where(Enumerable.Select(Enumerable.Where(_htmlParser.FindStartTags("div"),
                t => HTMLParser.ClassAttributeValueHas(t, "has-file")), t => _htmlParser.CreateTagRange(t)), r => r != null))
            {
                string poster = GetPoster(postTagRange);
                HTMLTagRange filesDivTagRange = FindFilesDivTagRange(postTagRange);

                foreach (HTMLTagRange fileDivTagRange in FindTagRangesWithClass(filesDivTagRange, "div", "file")) {
                    FileTags file = ReadFileURLs(FindFileTags(fileDivTagRange));
                    if (file == null) continue;

                    bool isSpoiler = file.ThumbURL == "/static/spoiler.png";

                    ImageInfo image = CreateImage(file, poster);
                    if (image == null) continue;

                    ThumbnailInfo thumb = CreateThumbnail(file.ThumbURL);
                    if (IsMissingThumbnailData(thumb)) continue;

                    AddFileReplaces(replaceList, file, image, thumb);

                    imageList.Add(image);
                    AddThumbnail(thumbnailList, thumb, isSpoiler, ref seenSpoiler);
                }
            }

            return imageList;
        }

        private string GetPoster(HTMLTagRange postTagRange) {
            HTMLTagRange nameTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(postTagRange, "span"), t => HTMLParser.ClassAttributeValueHas(t, "name"))));
            if (nameTagRange == null) return String.Empty;

            HTMLTagRange tripSpanTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(postTagRange, "span"), t => HTMLParser.ClassAttributeValueHas(t, "trip"))));

            return GetPosterFromNameAndTrip(nameTagRange, tripSpanTagRange);
        }

        // The OP's files sit in the thread div; a reply's files sit in the reply.
        private HTMLTagRange FindFilesDivTagRange(HTMLTagRange postTagRange) {
            bool isOP = HTMLParser.ClassAttributeValueHas(postTagRange.StartTag, "op");

            HTMLTagRange threadDivTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags("div"), t => HTMLParser.ClassAttributeValueHas(t, "thread"))));

            return _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(isOP ? threadDivTagRange : postTagRange, "div"), t => HTMLParser.ClassAttributeValueHas(t, "files"))));
        }

        // Finds the file info paragraph, its link, the thumbnail link and the thumbnail image in a
        // file div. Returns null if any of them is missing.
        private FileTags FindFileTags(HTMLTagRange fileDivTagRange) {
            HTMLTagRange fileInfoParagraphTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(fileDivTagRange, "p"), t => HTMLParser.ClassAttributeValueHas(t, "fileinfo"))));
            if (fileInfoParagraphTagRange == null) return null;

            HTMLTagRange fileThumbLinkTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(fileDivTagRange, "a"), t => t.GetAttributeValueOrEmpty("target") == "_blank")));
            if (fileThumbLinkTagRange == null) return null;

            HTMLTag fileInfoLinkStartTag = _htmlParser.FindStartTag(fileInfoParagraphTagRange, "a");
            if (fileInfoLinkStartTag == null) return null;

            HTMLTag fileThumbImageTag = _htmlParser.FindStartTag(fileThumbLinkTagRange, "img");
            if (fileThumbImageTag == null) return null;

            return new FileTags {
                InfoTagRange = fileInfoParagraphTagRange,
                ThumbLinkTagRange = fileThumbLinkTagRange,
                LinkStartTag = fileInfoLinkStartTag,
                ThumbImageTag = fileThumbImageTag
            };
        }

        // Returns null if the original file name, the MD5 or a usable file name is missing.
        private ImageInfo CreateImage(FileTags file, string poster) {
            HTMLTagRange postFileNameSpanTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(file.InfoTagRange, "span"), t => HTMLParser.ClassAttributeValueHas(t, "postfilename"))));
            if (postFileNameSpanTagRange == null) return null;

            string originalFileName = GetTitleOrInnerHTML(postFileNameSpanTagRange);

            string imageMD5 = file.ThumbImageTag.GetAttributeValue("data-md5");
            if (imageMD5 == null) return null;

            ImageInfo image = new ImageInfo {
                URL = General.GetAbsoluteURL(_url, HttpUtility.HtmlDecode(file.ImageURL)),
                Referer = _url,
                OriginalFileName = General.CleanFileName(HttpUtility.HtmlDecode(originalFileName)),
                HashType = HashType.MD5,
                Hash = General.TryBase64Decode(imageMD5),
                Poster = General.CleanFolderName(poster)
            };
            return IsMissingFileNameOrHash(image) ? null : image;
        }

        public override HashSet<string> GetCrossLinks(List<ReplaceInfo> replaceList, bool interBoardAutoFollow) {
            HashSet<string> crossLinks = new HashSet<string>();

            foreach (HTMLTagRange bodyDivTagRange in Enumerable.Where(Enumerable.Select(Enumerable.Where(_htmlParser.FindStartTags("div"),
                t => HTMLParser.ClassAttributeValueHas(t, "body")), t => _htmlParser.CreateTagRange(t)), r => r != null))
            {
                foreach (HTMLTag quoteLinkTag in Enumerable.Where(_htmlParser.FindStartTags(bodyDivTagRange, "a"),
                    t => new Regex(@"^/[0-9a-zA-Z+$_\u0080-\uFFFF]{1,58}/res/\d+\.html#\d+$").IsMatch(t.GetAttributeValueOrEmpty("href"))))
                {
                    HTMLAttribute attribute = quoteLinkTag.GetAttribute("href");
                    string href = attribute.Value.Remove(attribute.Value.IndexOf('#'));
                    string[] urlSplit = href.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    string url = General.GetAbsoluteURL(_url, href);
                    if (url == null || IsSkippedQuoteLink(urlSplit, interBoardAutoFollow)) continue;
                    crossLinks.Add(url);
                    if (replaceList != null) {
                        replaceList.Add(
                            new ReplaceInfo {
                                Offset = attribute.Offset,
                                Length = attribute.Length,
                                Type = ReplaceType.QuoteLinkHref,
                                Tag = href.Replace("/res", "").Replace(".html", "").Insert(0, GetSiteName()),
                                Value = GetLiveLinkHrefAttribute(attribute.Value)
                            });
                    }
                }
            }
            return crossLinks;
        }

        // Skips links to this thread, and links to other boards unless inter-board links are followed.
        private bool IsSkippedQuoteLink(string[] urlSplit, bool interBoardAutoFollow) {
            return (urlSplit[0] == GetBoardName() && urlSplit[2] == GetThreadID() + ".html") || (!interBoardAutoFollow && GetBoardName() != urlSplit[0]);
        }

        // Leaves the page unchanged if it has no thread div, e.g. an error page.
        public override void ResurrectDeadPosts(HTMLParser previousParser, List<ReplaceInfo> replaceList) {
            if (previousParser == null || FindThreadDivStartTag() == null) return;
            Dictionary<string, HTMLTagRange> resurrectedPosts = new Dictionary<string, HTMLTagRange>();

            ApplyReplaces(GetDeadPostReplaces(previousParser, resurrectedPosts));

            if (replaceList == null) return;
            AddResurrectedImageReplaces(replaceList, resurrectedPosts);
        }

        // Returns replacements that insert the posts missing from the current page, and records each
        // inserted post in resurrectedPosts.
        private List<ReplaceInfo> GetDeadPostReplaces(HTMLParser previousParser, Dictionary<string, HTMLTagRange> resurrectedPosts) {
            List<ReplaceInfo> deadPostReplaceList = new List<ReplaceInfo>();
            Dictionary<string, HTMLTagRange> newPosts = new Dictionary<string, HTMLTagRange>();
            foreach (KeyValuePair<string, HTMLTagRange> post in GetTagRangesWithUniqueID(Enumerable.Where(Enumerable.Select(Enumerable.Where(_htmlParser.FindStartTags("div"),
                t => HTMLParser.ClassAttributeValueHas(t, "post")), t => _htmlParser.CreateTagRange(t)), r => r != null)))
            {
                newPosts.Add(post.Key, post.Value);
            }

            HTMLTagRange lastExistingPostTagRange = null;
            foreach (KeyValuePair<string, HTMLTagRange> previousPost in GetTagRangesWithUniqueID(Enumerable.Where(Enumerable.Select(Enumerable.Where(previousParser.FindStartTags("div"),
                t => HTMLParser.ClassAttributeValueHas(t, "post")), t => previousParser.CreateTagRange(t)), r => r != null)))
            {
                HTMLTagRange tempTagRange;
                if (newPosts.TryGetValue(previousPost.Key, out tempTagRange)) {
                    lastExistingPostTagRange = tempTagRange;
                    continue;
                }
                deadPostReplaceList.Add(CreateDeadPostReplace(previousParser, previousPost.Value, lastExistingPostTagRange));
                resurrectedPosts.Add(previousPost.Key, previousPost.Value);
            }
            return deadPostReplaceList;
        }

        private ReplaceInfo CreateDeadPostReplace(HTMLParser previousParser, HTMLTagRange previousPostTagRange, HTMLTagRange lastExistingPostTagRange) {
            int offset = lastExistingPostTagRange != null ? lastExistingPostTagRange.EndOffset : FindThreadDivStartTag().EndOffset;
            string value = previousParser.GetHTML(previousPostTagRange);
            if (!value.Contains("<strong style=\"color: #FF0000\">[Deleted]</strong> ")) {
                value = value.Insert(GetDeletedMarkerOffset(previousParser, previousPostTagRange), "<strong style=\"color: #FF0000\">[Deleted]</strong> ");
            }
            return new ReplaceInfo {
                Offset = offset,
                Length = 0,
                Type = ReplaceType.DeadPost,
                Tag = previousPostTagRange.StartTag.GetAttributeValue("id"),
                Value = value.Insert(0, "<br/>")
            };
        }

        private void AddResurrectedImageReplaces(List<ReplaceInfo> replaceList, Dictionary<string, HTMLTagRange> resurrectedPosts) {
            foreach (HTMLTagRange postTagRange in Enumerable.Where(Enumerable.Select(Enumerable.Where(_htmlParser.FindStartTags("div"),
                t => HTMLParser.ClassAttributeValueHas(t, "has-file")), t => _htmlParser.CreateTagRange(t)), r => r != null))
            {
                if (!IsResurrected(resurrectedPosts, postTagRange)) continue;

                HTMLTagRange filesDivTagRange = FindFilesDivTagRange(postTagRange);

                foreach (HTMLTagRange fileDivTagRange in FindTagRangesWithClass(filesDivTagRange, "div", "file")) {
                    FileTags file = FindFileTags(fileDivTagRange);
                    if (file == null) continue;

                    AddResurrectedFileReplaces(replaceList, file);
                }
            }
        }

        public override bool IsBoardHighTurnover() {
            return String.Equals(GetBoardName(), "v", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(GetBoardName(), "b", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(GetBoardName(), "pol", StringComparison.OrdinalIgnoreCase);
        }
    }

    public class FuukaSiteHelper : SiteHelper {
        public override string GetOfflinePageScriptSite() {
            return OfflinePageScript.Fuuka;
        }

        protected override bool IsImage(HTMLTag linkTag) {
            return Enumerable.FirstOrDefault(Enumerable.Where(_htmlParser.FindStartTags(_htmlParser.CreateTagRange(linkTag), "img"), t => HTMLParser.ClassAttributeValueHas(t, "thumb"))) != null;
        }

        public override List<ImageInfo> GetImages(List<ReplaceInfo> replaceList, List<ThumbnailInfo> thumbnailList, bool local = false) {
            List<ImageInfo> imageList = new List<ImageInfo>();

            // A post can be a td with a div of the same id inside it; only the td is used
            foreach (KeyValuePair<string, HTMLTagRange> post in GetTagRangesWithUniqueID(Enumerable.Where(Enumerable.Select(Enumerable.Where(_htmlParser.FindStartTags("td", "div"),
                t => new Regex("^p\\d+$").IsMatch(t.GetAttributeValueOrEmpty("id"))), t => _htmlParser.CreateTagRange(t)), r => r != null)))
            {
                HTMLTagRange postTagRange = post.Value;
                HTMLTagRange labelTagRange = _htmlParser.CreateTagRange(_htmlParser.FindStartTag(postTagRange, "label"));
                if (labelTagRange == null) continue;

                FileTags file = ReadFileURLs(FindFileTags(postTagRange));
                if (file == null) continue;

                ImageInfo image = CreateImage(labelTagRange, file);
                if (image == null) continue;

                ThumbnailInfo thumb = CreateThumbnail(file.ThumbURL);
                if (IsMissingThumbnailData(thumb)) continue;

                AddImageReplaces(replaceList, file, image, thumb);

                imageList.Add(image);
                thumbnailList.Add(thumb);
            }

            return imageList;
        }

        // Finds the file info span, the image link and its thumbnail image. Returns null if any of
        // them is missing.
        private FileTags FindFileTags(HTMLTagRange postTagRange) {
            HTMLTagRange postHeaderRange = FindFileInfoTagRange(postTagRange);
            if (postHeaderRange == null) return null;

            HTMLTagRange imageLinkTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(_htmlParser.FindStartTags(postTagRange, "a"), IsImage)));
            if (imageLinkTagRange == null) return null;

            HTMLTag thumbImageTag = _htmlParser.FindStartTag(imageLinkTagRange, "img");
            if (thumbImageTag == null) return null;

            return new FileTags {
                InfoTagRange = postHeaderRange,
                ThumbLinkTagRange = imageLinkTagRange,
                LinkStartTag = imageLinkTagRange.StartTag,
                ThumbImageTag = thumbImageTag
            };
        }

        // The file info is the span with the fileinfo class. On a reply the first span is the
        // poster name, so the first span is only used when no span has the class.
        private HTMLTagRange FindFileInfoTagRange(HTMLTagRange postTagRange) {
            HTMLTag fileInfoStartTag = Enumerable.FirstOrDefault(Enumerable.Where(_htmlParser.FindStartTags(postTagRange, "span"),
                t => HTMLParser.ClassAttributeValueHas(t, "fileinfo")));
            return _htmlParser.CreateTagRange(fileInfoStartTag ?? _htmlParser.FindStartTag(postTagRange, "span"));
        }

        // Returns null if the file info is incomplete, or the image has no usable file name or an
        // MD5 that cannot be decoded.
        private ImageInfo CreateImage(HTMLTagRange labelTagRange, FileTags file) {
            string[] fileInfoSplit = _htmlParser.GetInnerHTML(file.InfoTagRange).Split(new[] { ',' }, 3);
            if (fileInfoSplit.Length < 3) return null;

            string fileInfo = fileInfoSplit[2].Trim();
            string originalFileName = GetOriginalFileName(fileInfo);
            string imageMD5 = GetImageMD5(fileInfo, file);
            string poster = GetPoster(labelTagRange);

            ImageInfo image = new ImageInfo {
                URL = General.GetAbsoluteURL(_url, HttpUtility.HtmlDecode(file.ImageURL)),
                Referer = _url,
                OriginalFileName = General.CleanFileName(HttpUtility.HtmlDecode(originalFileName)),
                HashType = imageMD5 != null ? HashType.MD5 : HashType.None,
                Hash = General.TryBase64Decode(imageMD5),
                Poster = General.CleanFolderName(HttpUtility.HtmlDecode(poster))
            };
            return IsMissingImageData(image) ? null : image;
        }

        // The file info ends with the file name, optionally followed by the MD5 in an HTML comment.
        private static string GetOriginalFileName(string fileInfo) {
            if (!fileInfo.EndsWith("-->")) return fileInfo;
            int hashIndex = fileInfo.LastIndexOf("<!--", StringComparison.Ordinal);
            return fileInfo.Remove(hashIndex).Trim();
        }

        private string GetImageMD5(string fileInfo, FileTags file) {
            if (!fileInfo.EndsWith("-->")) return GetSimilarImageMD5(file);
            int hashIndex = fileInfo.LastIndexOf("<!--", StringComparison.Ordinal);
            return fileInfo.Substring(hashIndex).Replace("<!--", "").Replace("-->", "").Trim();
        }

        // Reads the MD5 from the "same image" search link (/<board>/image/<md5>) between the post
        // header and the image link. The MD5 is URL-safe or standard base64, so it can contain "/"
        // and is everything after the /image/ segment, percent-decoded.
        private string GetSimilarImageMD5(FileTags file) {
            HTMLTag similarImageLinkStartTag = Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(file.InfoTagRange.EndTag, file.LinkStartTag, "a"), t => t.GetAttributeValueOrEmpty("href").Contains("/image/")));
            if (similarImageLinkStartTag == null) return null;

            string href = HttpUtility.HtmlDecode(similarImageLinkStartTag.GetAttributeValueOrEmpty("href"));
            string encodedMD5 = href.Substring(href.IndexOf("/image/", StringComparison.Ordinal) + "/image/".Length);
            string imageMD5 = Uri.UnescapeDataString(encodedMD5).Replace('-', '+').Replace('_', '/');
            return imageMD5.PadRight(imageMD5.Length + (4 - imageMD5.Length % 4) % 4, '=');
        }

        private string GetPoster(HTMLTagRange labelTagRange) {
            HTMLTagRange posterNameSpanTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(labelTagRange, "span"), t => HTMLParser.ClassAttributeValueHas(t, "postername"))));

            HTMLTagRange posterTripSpanTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(labelTagRange, "span"), t => HTMLParser.ClassAttributeValueHas(t, "postertrip"))));

            if (posterNameSpanTagRange == null) return String.Empty;
            string name = _htmlParser.GetInnerHTML(_htmlParser.CreateTagRange(_htmlParser.FindStartTag(posterNameSpanTagRange, "span")) ?? posterNameSpanTagRange).Replace("\r", "").Replace("\n", "").Replace("&nbsp;", "").Trim();
            string trip = posterTripSpanTagRange != null ? _htmlParser.GetInnerHTML(posterTripSpanTagRange).Replace("&nbsp;", "").Trim() : null;
            return FormatPoster(name, trip);
        }

        private static void AddImageReplaces(List<ReplaceInfo> replaceList, FileTags file, ImageInfo image, ThumbnailInfo thumb) {
            if (replaceList == null) return;
            AddAttributeReplace(replaceList, file.LinkStartTag.GetAttribute("href"), ReplaceType.ImageLinkHref, image.FileName);
            AddAttributeReplace(replaceList, file.ThumbImageTag.GetAttribute("src"), ReplaceType.ImageSrc, thumb.FileName);
        }
    }

    public class FoolFuukaSiteHelper : SiteHelper {
        public override string GetOfflinePageScriptSite() {
            return OfflinePageScript.FoolFuuka;
        }

        public override List<ImageInfo> GetImages(List<ReplaceInfo> replaceList, List<ThumbnailInfo> thumbnailList, bool local = false) {
            List<ImageInfo> imageList = new List<ImageInfo>();

            foreach (HTMLTagRange postTagRange in Enumerable.Where(Enumerable.Select(Enumerable.Where(_htmlParser.FindStartTags("article"),
                IsPostWithImage), t => _htmlParser.CreateTagRange(t)), r => r != null))
            {
                FileTags file = FindFile(postTagRange);
                if (file == null) continue;

                HTMLTagRange fileNameLinkTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                    _htmlParser.FindStartTags(file.InfoTagRange, "a"), t => HTMLParser.ClassAttributeValueHas(t, "post_file_filename"))));

                ImageInfo image = CreateImage(postTagRange, file, fileNameLinkTagRange);
                if (image == null) continue;

                ThumbnailInfo thumb = CreateThumbnail(file.ThumbURL);
                if (IsMissingThumbnailData(thumb)) continue;

                AddImageReplaces(replaceList, postTagRange, file, fileNameLinkTagRange, image, thumb);

                imageList.Add(image);
                thumbnailList.Add(thumb);
            }

            return imageList;
        }

        private static bool IsPostWithImage(HTMLTag tag) {
            return HTMLParser.ClassAttributeValueHas(tag, "has_image") || (HTMLParser.ClassAttributeValueHas(tag, "thread") && tag.GetAttribute("id") != null);
        }

        // Finds the image link, its thumbnail image and the post_file div. Returns null if any of
        // them or either URL is missing.
        private FileTags FindFile(HTMLTagRange postTagRange) {
            FileTags file = ReadFileURLs(FindFileTags(postTagRange));
            if (file == null) return null;

            file.InfoTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(postTagRange, "div"), t => HTMLParser.ClassAttributeValueHas(t, "post_file"))));
            return file.InfoTagRange != null ? file : null;
        }

        private FileTags FindFileTags(HTMLTagRange postTagRange) {
            HTMLTagRange imageLinkTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(postTagRange, "a"), t => HTMLParser.ClassAttributeValueHas(t, "thread_image_link"))));
            if (imageLinkTagRange == null) return null;

            HTMLTag thumbImageTag = _htmlParser.FindStartTag(imageLinkTagRange, "img");
            if (thumbImageTag == null) return null;

            return new FileTags {
                ThumbLinkTagRange = imageLinkTagRange,
                LinkStartTag = imageLinkTagRange.StartTag,
                ThumbImageTag = thumbImageTag
            };
        }

        // Returns null if the MD5, the poster data or a usable file name or hash is missing.
        private ImageInfo CreateImage(HTMLTagRange postTagRange, FileTags file, HTMLTagRange fileNameLinkTagRange) {
            string originalFileName = GetOriginalFileName(file.InfoTagRange, fileNameLinkTagRange);

            string imageMD5 = file.ThumbImageTag.GetAttributeValue("data-md5");
            if (imageMD5 == null) return null;

            HTMLTagRange posterDataSpanTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(postTagRange, "span"), t => HTMLParser.ClassAttributeValueHas(t, "post_poster_data"))));
            if (posterDataSpanTagRange == null) return null;

            string poster = GetPoster(posterDataSpanTagRange);

            ImageInfo image = new ImageInfo {
                URL = General.GetAbsoluteURL(_url, HttpUtility.HtmlDecode(file.ImageURL)),
                Referer = _url,
                OriginalFileName = General.CleanFileName(HttpUtility.HtmlDecode(originalFileName)),
                HashType = HashType.MD5,
                Hash = General.TryBase64Decode(imageMD5),
                Poster = General.CleanFolderName(HttpUtility.HtmlDecode(poster))
            };
            return IsMissingFileNameOrHash(image) ? null : image;
        }

        private string GetOriginalFileName(HTMLTagRange postFileTagRange, HTMLTagRange fileNameLinkTagRange) {
            if (fileNameLinkTagRange != null) return GetTitleOrInnerHTML(fileNameLinkTagRange);

            HTMLTagRange fileNameSpanTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(postFileTagRange, "span"), t => HTMLParser.ClassAttributeValueHas(t, "post_file_filename"))));
            if (fileNameSpanTagRange != null) return GetTitleOrInnerHTML(fileNameSpanTagRange);

            return GetUnlabeledFileName(postFileTagRange);
        }

        // Reads the file name from post_file markup that has no post_file_filename element.
        private string GetUnlabeledFileName(HTMLTagRange postFileTagRange) {
            HTMLTag postFileControlsEndTag = _htmlParser.FindCorrespondingEndTag(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(postFileTagRange, "span"), t => HTMLParser.ClassAttributeValueHas(t, "post_file_controls"))));
            HTMLTag postFileMetadataStartTag = Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(postFileTagRange, "span"), t => HTMLParser.ClassAttributeValueHas(t, "post_file_metadata")));
            if (postFileControlsEndTag != null && postFileMetadataStartTag != null) {
                return _htmlParser.GetInnerHTML(postFileControlsEndTag, postFileMetadataStartTag).Trim().TrimEnd(',');
            }
            if (postFileControlsEndTag == null && postFileMetadataStartTag == null) {
                return GetFileNameAfterSecondComma(postFileTagRange);
            }
            return String.Empty;
        }

        private string GetFileNameAfterSecondComma(HTMLTagRange postFileTagRange) {
            string[] postFileSplit = _htmlParser.GetInnerHTML(postFileTagRange).Split(new[] { ',' }, 3);
            return postFileSplit.Length == 3 ? postFileSplit[2].Trim() : String.Empty;
        }

        private string GetPoster(HTMLTagRange posterDataSpanTagRange) {
            HTMLTagRange authorSpanTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(posterDataSpanTagRange, "span"), t => HTMLParser.ClassAttributeValueHas(t, "post_author"))));

            HTMLTagRange tripcodeSpanTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(posterDataSpanTagRange, "span"), t => HTMLParser.ClassAttributeValueHas(t, "post_tripcode"))));

            HTMLTagRange idSpanTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(posterDataSpanTagRange, "span"), t => HTMLParser.ClassAttributeValueHas(t, "poster_hash"))));

            if (idSpanTagRange != null) return _htmlParser.GetInnerHTML(idSpanTagRange).Replace("ID:", "");
            if (authorSpanTagRange == null) return String.Empty;
            string name = _htmlParser.GetInnerHTML(authorSpanTagRange);
            return FormatPoster(name, GetNonEmptyInnerHTML(tripcodeSpanTagRange));
        }

        private string GetNonEmptyInnerHTML(HTMLTagRange tagRange) {
            if (tagRange == null) return null;
            string innerHTML = _htmlParser.GetInnerHTML(tagRange);
            return String.IsNullOrEmpty(innerHTML) ? null : innerHTML;
        }

        private void AddImageReplaces(List<ReplaceInfo> replaceList, HTMLTagRange postTagRange, FileTags file, HTMLTagRange fileNameLinkTagRange, ImageInfo image, ThumbnailInfo thumb) {
            if (replaceList == null) return;
            AddAttributeReplace(replaceList, file.LinkStartTag.GetAttribute("href"), ReplaceType.ImageLinkHref, image.FileName);
            if (fileNameLinkTagRange != null) {
                AddAttributeReplace(replaceList, fileNameLinkTagRange.StartTag.GetAttribute("href"), ReplaceType.ImageLinkHref, image.FileName);
            }
            AddOtherImageLinkReplaces(replaceList, postTagRange, file, fileNameLinkTagRange, image);
            AddAttributeReplace(replaceList, file.ThumbImageTag.GetAttribute("src"), ReplaceType.ImageSrc, thumb.FileName);
        }

        // Some markup (e.g. desuarchive) has more plain links to the full image, in
        // post_file_controls and in the post header. Links that point elsewhere (search, view
        // same, report) are kept.
        private void AddOtherImageLinkReplaces(List<ReplaceInfo> replaceList, HTMLTagRange postTagRange, FileTags file, HTMLTagRange fileNameLinkTagRange, ImageInfo image) {
            foreach (HTMLTag linkTag in Enumerable.Where(_htmlParser.FindStartTags(postTagRange, "a"), t => IsOtherImageLink(t, file, fileNameLinkTagRange, image.URL))) {
                AddAttributeReplace(replaceList, linkTag.GetAttribute("href"), ReplaceType.ImageLinkHref, image.FileName);
            }
        }

        // True if the link is not one of the links AddImageReplaces rewrites itself, and its URL
        // resolves to the full image URL
        private bool IsOtherImageLink(HTMLTag linkTag, FileTags file, HTMLTagRange fileNameLinkTagRange, string imageURL) {
            if (linkTag == file.LinkStartTag) return false;
            if (fileNameLinkTagRange != null && linkTag == fileNameLinkTagRange.StartTag) return false;
            return IsLinkTo(linkTag, imageURL);
        }

        private bool IsLinkTo(HTMLTag linkTag, string url) {
            string href = linkTag.GetAttributeValue("href");
            return href != null && String.Equals(General.GetAbsoluteURL(_url, HttpUtility.HtmlDecode(href)), url, StringComparison.Ordinal);
        }
    }

    public class LynxChanSiteHelper : SiteHelper {
        public override string GetOfflinePageScriptSite() {
            return OfflinePageScript.LynxChan;
        }

        public override List<ImageInfo> GetImages(List<ReplaceInfo> replaceList, List<ThumbnailInfo> thumbnailList, bool local = false) {
            List<ImageInfo> imageList = new List<ImageInfo>();
            bool seenSpoiler = false;

            foreach (HTMLTagRange postTagRange in Enumerable.Where(Enumerable.Select(Enumerable.Where(_htmlParser.FindStartTags("div"),
                IsPostCell), t => _htmlParser.CreateTagRange(t)), r => r != null))
            {
                HTMLTagRange posterDataSpanTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                    _htmlParser.FindStartTags(postTagRange, "div"), IsPostHeader)));
                if (posterDataSpanTagRange == null) continue;

                string poster = GetPoster(posterDataSpanTagRange);

                HTMLTagRange filesDivTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                    _htmlParser.FindStartTags(postTagRange, "div"), t => HTMLParser.ClassAttributeValueHas(t, "panelUploads"))));

                foreach (HTMLTagRange fileDivTagRange in FindTagRangesWithClass(filesDivTagRange, "figure", "uploadCell")) {
                    FileTags file = FindFile(fileDivTagRange);
                    if (file == null) continue;

                    bool isSpoiler = file.ThumbURL.EndsWith(".spoiler");

                    ImageInfo image = CreateImage(file, poster);
                    if (IsMissingFileName(image)) continue;

                    ThumbnailInfo thumb = CreateThumbnail(file.ThumbURL);
                    if (IsMissingThumbnailData(thumb)) continue;

                    AddImageReplaces(replaceList, file, image, thumb);

                    imageList.Add(image);
                    AddThumbnail(thumbnailList, thumb, isSpoiler, ref seenSpoiler);
                }
            }

            return imageList;
        }

        private static bool IsPostCell(HTMLTag tag) {
            return HTMLParser.ClassAttributeValueHas(tag, "postCell") || HTMLParser.ClassAttributeValueHas(tag, "opCell");
        }

        private static bool IsPostHeader(HTMLTag tag) {
            return HTMLParser.ClassAttributeValueHas(tag, "opHead") || HTMLParser.ClassAttributeValueHas(tag, "innerPost");
        }

        private string GetPoster(HTMLTagRange posterDataSpanTagRange) {
            HTMLTagRange nameTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(posterDataSpanTagRange, "a"), t => HTMLParser.ClassAttributeValueHas(t, "linkName"))));
            if (nameTagRange == null) return String.Empty;
            return FormatPoster(_htmlParser.GetInnerHTML(nameTagRange), null);
        }

        // Finds the upload's links, thumbnail image and original name link. Returns null if any of
        // them or either URL is missing.
        private FileTags FindFile(HTMLTagRange fileDivTagRange) {
            FileTags file = ReadFileURLs(FindFileTags(fileDivTagRange));
            if (file == null) return null;

            file.InfoTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(fileDivTagRange, "a"), t => HTMLParser.ClassAttributeValueHas(t, "originalNameLink"))));
            return file.InfoTagRange != null ? file : null;
        }

        private FileTags FindFileTags(HTMLTagRange fileDivTagRange) {
            HTMLTagRange fileThumbLinkTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(fileDivTagRange, "a"), t => HTMLParser.ClassAttributeValueHas(t, "imgLink"))));
            if (fileThumbLinkTagRange == null) return null;

            HTMLTagRange fileInfoLinkTagRange = _htmlParser.CreateTagRange(Enumerable.FirstOrDefault(Enumerable.Where(
                _htmlParser.FindStartTags(fileDivTagRange, "a"), t => HTMLParser.ClassAttributeValueHas(t, "nameLink"))));
            if (fileInfoLinkTagRange == null) return null;

            HTMLTag fileThumbImageTag = _htmlParser.FindStartTag(fileThumbLinkTagRange, "img");
            if (fileThumbImageTag == null) return null;

            return new FileTags {
                ThumbLinkTagRange = fileThumbLinkTagRange,
                LinkStartTag = fileInfoLinkTagRange.StartTag,
                ThumbImageTag = fileThumbImageTag
            };
        }

        private ImageInfo CreateImage(FileTags file, string poster) {
            string originalFileName = GetTitleOrInnerHTML(file.InfoTagRange);

            return new ImageInfo {
                URL = General.GetAbsoluteURL(_url, HttpUtility.HtmlDecode(file.ImageURL)),
                Referer = _url,
                OriginalFileName = General.CleanFileName(HttpUtility.HtmlDecode(originalFileName)),
                Poster = General.CleanFolderName(poster)
            };
        }

        private static void AddImageReplaces(List<ReplaceInfo> replaceList, FileTags file, ImageInfo image, ThumbnailInfo thumb) {
            if (replaceList == null) return;
            AddFileReplaces(replaceList, file, image, thumb);
            AddAttributeReplace(replaceList, file.InfoTagRange.StartTag.GetAttribute("href"), ReplaceType.ImageLinkHref, image.FileName);
        }
    }
}