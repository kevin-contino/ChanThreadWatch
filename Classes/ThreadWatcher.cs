using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web;

namespace JDP {
    public class ThreadWatcher {
        private const int _maxDownloadTries = 3;

        // Checks in a row that download the whole page again to retry a file that failed
        private const int _maxRefetchesForFailedFile = 3;

        // A download's file is preallocated to the size the server announces, which reduces
        // fragmentation; announced sizes above this are only partly preallocated, so a wrong or
        // hostile Content-Length cannot reserve a large amount of disk space up front.
        private const long _maxPreallocateBytes = 64L * 1024 * 1024;

        // Files larger than this are not saved. Image boards limit uploads to tens of megabytes
        // (4chan: 4 MB images, 6 MB webm), so 512 MB leaves room for video archives while a broken
        // or hostile server cannot fill the disk with one endless response.
        internal const long DefaultMaxFileBytes = 512L * 1024 * 1024;

        // Auto-follow adds the threads that a thread links to, and those threads can add more.
        // This caps the threads one root watcher can add, so a chain of cross-links cannot keep
        // adding watchers without limit.
        internal const int DefaultMaxDescendantThreads = 100;

        private static WorkScheduler _workScheduler = new WorkScheduler();

        // Settable by tests only
        internal static long MaxFileBytes { get; set; } = DefaultMaxFileBytes;

        // Settable by tests only
        internal static int MaxDescendantThreads { get; set; } = DefaultMaxDescendantThreads;

        // Parses downloaded thread pages; replaceable by tests only
        internal static Func<string, HTMLParser> PageParserFactory { get; set; } = html => new HTMLParser(html);

        // Runs with the URL just before each request is started; replaceable by tests only
        internal static System.Action<string> BeforeRequestStart { get; set; } = url => { };

        private WorkScheduler.WorkItem _nextCheckWorkItem;
        private object _settingsSync = new object();
        private bool _isStopping;
        private StopReason _stopReason;
        private Dictionary<long, Action> _downloadAborters = new Dictionary<long, Action>();
        private bool _hasRun;
        private bool _hasInitialized;
        private ManualResetEvent _checkFinishedEvent = new ManualResetEvent(true);
        private ManualResetEvent _reparseFinishedEvent = new ManualResetEvent(true);
        private bool _isWaiting;
        private string _pageURL;
        private string _pageAuth;
        private string _imageAuth;
        private bool _oneTimeDownload;
        private int _checkIntervalSeconds;
        private int _minCheckIntervalSeconds;
        private string _mainDownloadDirectory = Settings.AbsoluteDownloadDirectory;
        private string _threadDownloadDirectory;
        private long _nextCheckTicks;
        private string _description = String.Empty;
        private object _tag;
        private SiteHelper _siteHelper;
        private Dictionary<string, ThreadWatcher> _childThreads = new Dictionary<string, ThreadWatcher>();
        private string _pageID;
        private string _category = String.Empty;
        private bool _autoFollow;
        private string _checkError;
        private int _failedFileCount;
        private bool _hasFilesToRetry;
        private bool _loggedDescendantLimit;
        // Separate from _settingsSync: counting descendants takes their locks, and a child can
        // take its parent's _settingsSync while holding its own
        private readonly object _descendantSlotSync = new object();
        private int _reservedDescendantSlots;
        private string _stopError;
        private string _reparseError;
        // Checks in a row that each file (by URL) failed in; guarded by itself
        private readonly Dictionary<string, int> _fileFailureCheckCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        // The host whose rate limit held back downloads of this thread, or null if none did; the
        // downloads are retried in the first check after the host's pause
        private ConnectionManager _rateLimitedConnection;

        static ThreadWatcher() {
            // HttpWebRequest uses ThreadPool for asynchronous calls
            General.EnsureThreadPoolMaxThreads(500, 1000);

            // Shouldn't matter since the limit is supposed to be per connection group
            ServicePointManager.DefaultConnectionLimit = Int32.MaxValue;

            // Enable TLS 1.2 on supported environments
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        }

        public ThreadWatcher(string pageURL) {
            _pageURL = pageURL;
            _siteHelper = SiteHelpers.GetInstance(PageHost);
            _siteHelper.SetURL(PageURL);
            _pageID = _siteHelper.GetPageID();
            _threadName = _siteHelper.GetThreadName();
        }

        public string PageURL {
            get { return _pageURL; }
        }

        public string PageHost {
            get { return (new Uri(PageURL)).Host; }
        }

        public string PageID {
            get { return _pageID; }
        }

        public string ThreadName {
            get { return _threadName; }
        }

        public SiteHelper SiteHelper {
            get { return _siteHelper; }
        }

        public string PageAuth {
            get { return _pageAuth; }
            set { SetSetting(out _pageAuth, value, true, false); }
        }

        public string ImageAuth {
            get { return _imageAuth; }
            set { SetSetting(out _imageAuth, value, true, false); }
        }

        public bool OneTimeDownload {
            get { return _oneTimeDownload; }
            set { SetSetting(out _oneTimeDownload, value, true, false); }
        }

        public bool AutoFollow {
            get { return _autoFollow; }
            set { SetSetting(out _autoFollow, value, true, false); }
        }

        public bool DoNotRename { get; set; }

        public int CheckIntervalSeconds {
            get { lock (_settingsSync) { return _checkIntervalSeconds; } }
            set {
                lock (_settingsSync) {
                    int newCheckIntervalSeconds = (_hasInitialized && value < _minCheckIntervalSeconds) ?
                        _minCheckIntervalSeconds : value;
                    int changeAmount = newCheckIntervalSeconds - _checkIntervalSeconds;
                    _checkIntervalSeconds = newCheckIntervalSeconds;
                    NextCheckTicks += changeAmount * 1000;
                }
            }
        }

        public string MainDownloadDirectory {
            get { lock (_settingsSync) { return _mainDownloadDirectory; } }
        }

        public string ThreadDownloadDirectory {
            get { lock (_settingsSync) { return _threadDownloadDirectory; } }
            set { SetSetting(out _threadDownloadDirectory, value, false, false); }
        }

        private bool ThreadDownloadDirectoryPendingRename {
            get { return ThreadDownloadDirectoryPendingDescriptionRename || ThreadDownloadDirectoryPendingCategoryRename; }
        }

        private bool ThreadDownloadDirectoryPendingDescriptionRename {
            get {
                lock (_settingsSync) {
                    return !String.IsNullOrEmpty(_threadDownloadDirectory) &&
                           Settings.RenameDownloadFolderWithDescription == true &&
                           !String.IsNullOrEmpty(_description) &&
                           !String.Equals(General.GetLastDirectory(_threadDownloadDirectory), General.CleanFileName(_description + ParentThreadFormattedDescription), StringComparison.Ordinal);
                }
            }
        }

        private bool ThreadDownloadDirectoryPendingCategoryRename {
            get {
                lock (_settingsSync) {
                    if (String.IsNullOrEmpty(_threadDownloadDirectory) || Settings.RenameDownloadFolderWithCategory != true) {
                        return false;
                    }
                    string categoryPath = General.RemoveLastDirectory(_threadDownloadDirectory);
                    string categoryName = categoryPath != _mainDownloadDirectory ? General.GetLastDirectory(categoryPath) : String.Empty;
                    return !String.Equals(categoryName, General.CleanFileName(_category), StringComparison.Ordinal);
                }
            }
        }

        public int MillisecondsUntilNextCheck {
            get { return Math.Max((int)(NextCheckTicks - TickCount.Now), 0); }
            set { NextCheckTicks = TickCount.Now + value; }
        }

        private long NextCheckTicks {
            get { lock (_settingsSync) { return _nextCheckTicks; } }
            set {
                lock (_settingsSync) {
                    _nextCheckTicks = value;
                    if (_nextCheckWorkItem != null) {
                        _nextCheckWorkItem.RunAtTicks = _nextCheckTicks;
                    }
                }
            }
        }

        public string Description {
            get { lock (_settingsSync) { return _description; } }
            set {
                lock (_settingsSync) {
                    _description = value;
                }
                if (ThreadDownloadDirectoryPendingDescriptionRename) {
                    TryRenameThreadDownloadDirectory(false);
                }
                if (Settings.RenameDownloadFolderWithParentThreadDescription == true) {
                    foreach (ThreadWatcher descendantThread in RootThread.DescendantThreads.Values) {
                        descendantThread.TryRenameThreadDownloadDirectory(false);
                    }
                }
            }
        }

        public string ParentThreadFormattedDescription {
            get {
                if (ParentThread == null || Settings.RenameDownloadFolderWithParentThreadDescription != true ||
                    (!String.IsNullOrEmpty(ParentThreadDescriptionFormat) && _description.EndsWith(ParentThreadDescriptionFormat.Replace("{Parent}", ParentThread.Description))))
                {
                    return String.Empty;
                }
                return ParentThreadDescriptionFormat.Replace("{Parent}", ParentThread.Description);
            }
        }

        // The settings file may lack the format (e.g. after a hand edit), so fall back to the default
        private static string ParentThreadDescriptionFormat {
            get { return Settings.ParentThreadDescriptionFormat ?? Settings.DefaultParentThreadDescriptionFormat; }
        }

        public object Tag {
            get { lock (_settingsSync) { return _tag; } }
            set { lock (_settingsSync) { _tag = value; } }
        }

        // A snapshot, so callers can enumerate it while other threads add children
        public Dictionary<string, ThreadWatcher> ChildThreads {
            get { lock (_settingsSync) { return new Dictionary<string, ThreadWatcher>(_childThreads); } }
        }

        // Replaces a child with the same page ID, as the thread list does when it registers a
        // new watcher for a page. Returns false if a child with that page ID was replaced.
        public bool AddChildThread(ThreadWatcher childThread) {
            lock (_settingsSync) {
                bool isNew = !_childThreads.ContainsKey(childThread.PageID);
                _childThreads[childThread.PageID] = childThread;
                return isNew;
            }
        }

        // Reserves room for one thread that auto-follow is about to add under this root
        // watcher. Threads are added later on the UI thread, so the reservations that have not
        // been released yet count against the limit too. Returns false if the limit is reached.
        public bool TryReserveDescendantSlot() {
            lock (_descendantSlotSync) {
                if (DescendantThreads.Count + _reservedDescendantSlots >= MaxDescendantThreads) return false;
                _reservedDescendantSlots++;
                return true;
            }
        }

        // Releases a reservation once the thread has been added (or rejected)
        public void ReleaseDescendantSlot() {
            lock (_descendantSlotSync) {
                if (_reservedDescendantSlots > 0) _reservedDescendantSlots--;
            }
        }

        // The settings of a thread that auto-follow adds from this one. Credentials are only
        // passed on to a thread with the same origin as this one.
        public ThreadInfo CreateChildThreadInfo(string childURL, DateTime addedOn, bool autoFollow) {
            return new ThreadInfo {
                URL = childURL,
                PageAuth = General.GetAuthForURL(PageAuth, PageURL, childURL),
                ImageAuth = General.GetAuthForURL(ImageAuth, PageURL, childURL),
                CheckIntervalSeconds = CheckIntervalSeconds,
                OneTimeDownload = OneTimeDownload,
                SaveDir = null,
                Description = String.Empty,
                StopReason = null,
                ExtraData = new WatcherExtraData {
                    AddedOn = addedOn,
                    AddedFrom = PageID
                },
                Category = Category,
                AutoFollow = autoFollow
            };
        }

        // The error of the last check (e.g. "HTTP 403 Forbidden"), or null if the check went fine
        public string CheckError {
            get { lock (_settingsSync) { return _checkError; } }
        }

        // The error that caused the watcher to stop (e.g. a one-time download whose page failed),
        // or null if it stopped for another reason
        public string StopError {
            get { lock (_settingsSync) { return _stopError; } }
        }

        // The error that made the last reparse fail, or null if it went fine
        public string ReparseError {
            get { lock (_settingsSync) { return _reparseError; } }
        }

        // The images and thumbnails that failed in the last check
        public int FailedFileCount {
            get { lock (_settingsSync) { return _failedFileCount; } }
        }

        // The host whose rate limit holds back downloads of this thread, or null if none does
        public string RateLimitedHost {
            get { lock (_settingsSync) { return _rateLimitedConnection != null ? _rateLimitedConnection.Host : null; } }
        }

        // RateLimitedHost while its pause lasts, or null once the pause is over (the held back
        // downloads then wait for the next check)
        public string RateLimitPausedHost {
            get {
                ConnectionManager connection;
                lock (_settingsSync) { connection = _rateLimitedConnection; }
                return connection != null && connection.IsPaused ? connection.Host : null;
            }
        }

        // When the rate limit pause of RateLimitedHost ends (now if there is none); the held back
        // downloads are retried in the first check after it
        public DateTime RateLimitResumeTime {
            get {
                long resumeTicks;
                lock (_settingsSync) { resumeTicks = _rateLimitedConnection != null ? _rateLimitedConnection.PausedUntilTicks : TickCount.Now; }
                return DateTime.Now.AddMilliseconds(resumeTicks - TickCount.Now);
            }
        }

        public Dictionary<string, ThreadWatcher> DescendantThreads {
            get {
                Dictionary<string, ThreadWatcher> dictionary = new Dictionary<string, ThreadWatcher>();
                foreach (ThreadWatcher childThread in ChildThreads.Values) {
                    if (!dictionary.ContainsKey(childThread.PageID)) dictionary.Add(childThread.PageID, childThread);
                }
                foreach (ThreadWatcher childThread in ChildThreads.Values) {
                    foreach (ThreadWatcher descendantThread in childThread.DescendantThreads.Values) {
                        if (!dictionary.ContainsKey(descendantThread.PageID)) dictionary.Add(descendantThread.PageID, descendantThread);
                    }
                }
                return dictionary;
            }
        }

        public ThreadWatcher ParentThread { get; set; }

        public ThreadWatcher RootThread {
            get {
                ThreadWatcher thread = this;
                while (thread.IsCrossLink) {
                    thread = thread.ParentThread;
                }
                return thread;
            }
        }

        public bool IsCrossLink {
            get { return ParentThread != null; }
        }

        public string Category {
            get { lock (_settingsSync) { return _category; } }
            set {
                lock (_settingsSync) {
                    _category = value;
                }
                if (ThreadDownloadDirectoryPendingCategoryRename) {
                    TryRenameThreadDownloadDirectory(false);
                }
            }
        }

        private void SetSetting<T>(out T field, T value, bool canChangeAfterRunning, bool canChangeWhileRunning) {
            lock (_settingsSync) {
                if (!canChangeAfterRunning && _hasRun) {
                    throw new Exception("This setting cannot be changed after the watcher has run.");
                }
                if (!canChangeWhileRunning && IsRunning) {
                    throw new Exception("This setting cannot be changed while the watcher is running.");
                }
                field = value;
            }
        }

        public void Start() {
            lock (_settingsSync) {
                if (IsRunning) {
                    throw new Exception("The watcher is already running.");
                }
                _isStopping = false;
                _stopReason = StopReason.Other;
                _stopError = null;
                _reparseError = null;
                _hasRun = true;
                _hasInitialized = false;
                _nextCheckWorkItem = _workScheduler.AddItem(TickCount.Now, Check, PageHost);
            }
        }

        public void Stop(StopReason reason) {
            Stop(reason, null);
        }

        // stopError is the error that caused the stop, shown with it; null for other stops
        private void Stop(StopReason reason, string stopError) {
            bool stoppingNow = false;
            bool checkFinished = false;
            List<Action> downloadAborters = null;
            lock (_settingsSync) {
                if (!IsStopping) {
                    stoppingNow = true;
                    _stopError = stopError;
                    checkFinished = BeginStopping(reason, out downloadAborters);
                }
            }
            if (!stoppingNow) return;
            if (checkFinished) {
                OnStopStatus(new StopStatusEventArgs(reason));
            }
            else {
                AbortDownloads(downloadAborters);
            }
        }

        // Must be called while holding _settingsSync. Returns whether the check has finished;
        // if it hasn't, outputs the aborters of the downloads that are still in progress.
        private bool BeginStopping(StopReason reason, out List<Action> downloadAborters) {
            downloadAborters = null;
            _isStopping = true;
            _stopReason = reason;
            _hasRun = true;
            if (_nextCheckWorkItem != null) {
                _workScheduler.RemoveItem(_nextCheckWorkItem);
                _nextCheckWorkItem = null;
            }
            bool checkFinished = _checkFinishedEvent.WaitOne(0, false);
            if (checkFinished) {
                _isWaiting = false;
            }
            else {
                lock (_downloadAborters) {
                    downloadAborters = new List<Action>(_downloadAborters.Values);
                }
            }
            return checkFinished;
        }

        private static void AbortDownloads(List<Action> downloadAborters) {
            foreach (Action abortDownload in downloadAborters) {
                abortDownload();
            }
        }

        public void BeginReparse() {
            _workScheduler.AddItem(TickCount.Now, Reparse);
        }

        private void Reparse() {
            lock (_settingsSync) {
                _reparseFinishedEvent.Reset();
                _reparseError = null;
            }
            try {
                ReparsePages();
            }
            catch (Exception ex) {
                lock (_settingsSync) {
                    _reparseError = DescribeReparseError(ex);
                }
                Logger.Log("Reparse of " + _pageURL + " failed:" + Environment.NewLine + ex);
            }
            finally {
                // Always set, or shutdown would wait for the reparse forever
                lock (_settingsSync) {
                    _reparseFinishedEvent.Set();
                }
                // Replaces the reparse progress with the stop status, and the reparse error if any
                OnStopStatus(new StopStatusEventArgs(StopReason));
            }
        }

        // A short description of why a reparse failed, shown in the status. File errors (e.g.
        // access denied) keep their message; any other error is a bug whose message (e.g.
        // "Value cannot be null") means nothing to the user, so it is only in the log.
        internal static string DescribeReparseError(Exception ex) {
            if (!(ex is IOException || ex is UnauthorizedAccessException)) return "unexpected error, details in the log file";
            return String.Join(" ", ex.Message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
        }

        private void ReparsePages() {
            List<PageInfo> pageList = new List<PageInfo> {
                new PageInfo {
                    URL = _pageURL
                }
            };

            string threadDir = ThreadDownloadDirectory;
            string imageDir = ThreadDownloadDirectory;
            string thumbDir = Path.Combine(ThreadDownloadDirectory, "thumbs");

            for (int pageIndex = 0; pageIndex < pageList.Count; pageIndex++) {
                ReparsePage(pageList, pageIndex, threadDir, imageDir, thumbDir);
            }
        }

        private void ReparsePage(List<PageInfo> pageList, int pageIndex, string threadDir, string imageDir, string thumbDir) {
            PageInfo pageInfo = pageList[pageIndex];
            pageInfo.Path = Path.Combine(threadDir, GetPageFileName(_threadName, pageIndex));
            if (!File.Exists(pageInfo.Path)) return;

            pageInfo.Encoding = DetectSavedPageEncoding(pageInfo.Path);

            HTMLParser parser = TryLoadHTMLParser(pageInfo.Path);
            if (parser == null) return;
            SiteHelper siteHelper = SiteHelpers.GetInstance(PageHost);
            siteHelper.SetHTMLParser(parser);
            siteHelper.SetURL(pageInfo.Path);

            OnReparseStatus(new ReparseStatusEventArgs(ReparseType.Page, pageIndex + 1, pageList.Count));

            ReparsePageImages(pageInfo, siteHelper, threadDir, imageDir, thumbDir);
        }

        private void ReparsePageImages(PageInfo pageInfo, SiteHelper siteHelper, string threadDir, string imageDir, string thumbDir) {
            pageInfo.ReplaceList = new List<ReplaceInfo>();
            List<ThumbnailInfo> thumbs = new List<ThumbnailInfo>();
            List<ImageInfo> images = siteHelper.GetImages(pageInfo.ReplaceList, thumbs, true);
            if (images.Count == 0) return;

            Dictionary<string, DownloadInfo> completedImages = new Dictionary<string, DownloadInfo>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, DownloadInfo> completedThumbs = new Dictionary<string, DownloadInfo>(StringComparer.OrdinalIgnoreCase);

            if (!TryCreateReparseThumbnailDirectory(thumbDir)) return;

            foreach (ThumbnailInfo thumb in thumbs) {
                completedThumbs[thumb.FileName] = new DownloadInfo {
                    FileName = thumb.FileName
                };
            }

            int maxFileNameLengthBaseDir = General.GetMaximumFileNameLength(imageDir);
            foreach (ImageInfo image in images) {
                ReparseImage(image, imageDir, maxFileNameLengthBaseDir, completedImages, images.Count);
            }
            siteHelper.SetURL(pageInfo.URL);
            Process(pageInfo, siteHelper, threadDir, imageDir, thumbDir, completedImages, completedThumbs, null);
        }

        private static bool TryCreateReparseThumbnailDirectory(string thumbDir) {
            if (Directory.Exists(thumbDir)) return true;
            try {
                Directory.CreateDirectory(thumbDir);
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
                return false;
            }
            return true;
        }

        private void ReparseImage(ImageInfo image, string imageDir, int maxFileNameLengthBaseDir, Dictionary<string, DownloadInfo> completedImages, int imageCount) {
            OnReparseStatus(new ReparseStatusEventArgs(ReparseType.Image, completedImages.Count, imageCount));
            string currentPath = new Uri(image.URL).LocalPath;
            if (!File.Exists(currentPath)) return;

            int maxFileNameLength;
            if (!TryGetReparseMaxFileNameLength(image, imageDir, maxFileNameLengthBaseDir, out maxFileNameLength)) {
                completedImages[image.FileName] = new DownloadInfo {
                    Folder = GetImageFolder(image),
                    FileName = Path.GetFileName(currentPath)
                };
                return;
            }

            string savePath = GetReparseImageSavePath(image, imageDir, currentPath, false);
            if (Path.GetFileName(savePath).Length > maxFileNameLength) {
                // Path too long, fall back to the URL file name
                savePath = GetReparseImageSavePath(image, imageDir, currentPath, true);
            }
            string saveFileName = Path.GetFileName(savePath);

            if (String.IsNullOrEmpty(saveFileName)) return;
            completedImages[image.FileName] = new DownloadInfo {
                Folder = GetImageFolder(image),
                FileName = saveFileName
            };

            MoveReparsedImage(currentPath, savePath);
        }

        // Returns false if the poster folder could not be created
        private static bool TryGetReparseMaxFileNameLength(ImageInfo image, string imageDir, int maxFileNameLengthBaseDir, out int maxFileNameLength) {
            maxFileNameLength = maxFileNameLengthBaseDir;
            if (!ShouldSortIntoPosterFolder(image)) return true;
            try {
                Directory.CreateDirectory(Path.Combine(imageDir, image.Poster));
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
                return false;
            }
            maxFileNameLength = General.GetMaximumFileNameLength(Path.Combine(imageDir, image.Poster));
            return true;
        }

        private static string GetReparseImageSavePath(ImageInfo image, string imageDir, string currentPath, bool pathTooLong) {
            string baseFileName = ChooseSaveBaseFileName(image, Settings.UseOriginalFileNames == true, pathTooLong);
            string saveFileNameNoExtension = Path.GetFileNameWithoutExtension(baseFileName);
            string saveExtension = Path.GetExtension(baseFileName);

            int iSuffix = 1;
            string savePath;
            do {
                savePath = Path.Combine(Path.Combine(imageDir, GetImageFolder(image)), GetNumberedFileName(saveFileNameNoExtension, iSuffix, saveExtension));
                iSuffix++;
            } while (currentPath != savePath && File.Exists(savePath));
            return savePath;
        }

        private void MoveReparsedImage(string currentPath, string savePath) {
            try {
                File.Move(currentPath, savePath);
                string imageFolder = General.RemoveLastDirectory(currentPath);
                if (imageFolder != ThreadDownloadDirectory && IsDirectoryEmpty(imageFolder)) {
                    Directory.Delete(imageFolder);
                }
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
        }

        public void WaitUntilStopped() {
            WaitUntilStopped(Timeout.Infinite);
        }

        public bool WaitUntilStopped(int timeout) {
            return _checkFinishedEvent.WaitOne(timeout, false);
        }

        public bool WaitReparse(int timeout = Timeout.Infinite) {
            return _reparseFinishedEvent.WaitOne(timeout, false);
        }

        public bool IsRunning {
            get {
                lock (_settingsSync) {
                    return !_checkFinishedEvent.WaitOne(0, false) || _nextCheckWorkItem != null;
                }
            }
        }

        public bool IsWaiting {
            get { lock (_settingsSync) { return _isWaiting; } }
        }

        public bool IsStopping {
            get { lock (_settingsSync) { return _isStopping; } }
        }

        public bool IsReparsing {
            get {
                lock (_settingsSync) {
                    return !_reparseFinishedEvent.WaitOne(0, false);
                }
            }
        }

        public StopReason StopReason {
            get { lock (_settingsSync) { return _stopReason; } }
        }

        public event EventHandler<ThreadWatcher, DownloadStatusEventArgs> DownloadStatus;

        public event EventHandler<ThreadWatcher, EventArgs> WaitStatus;

        public event EventHandler<ThreadWatcher, StopStatusEventArgs> StopStatus;

        public event EventHandler<ThreadWatcher, ReparseStatusEventArgs> ReparseStatus;

        public event EventHandler<ThreadWatcher, EventArgs> ThreadDownloadDirectoryRename;

        public event EventHandler<ThreadWatcher, DownloadStartEventArgs> DownloadStart;

        public event EventHandler<ThreadWatcher, DownloadProgressEventArgs> DownloadProgress;

        public event EventHandler<ThreadWatcher, DownloadEndEventArgs> DownloadEnd;

        public event EventHandler<ThreadWatcher, AddThreadEventArgs> AddThread;

        private void OnDownloadStatus(DownloadStatusEventArgs e) {
            var evt = DownloadStatus;
            if (evt != null)
                try { evt(this, e); }
                catch { }
        }

        private void OnWaitStatus(EventArgs e) {
            var evt = WaitStatus;
            if (evt != null)
                try { evt(this, e); }
                catch { }
        }

        private void OnStopStatus(StopStatusEventArgs e) {
            var evt = StopStatus;
            if (evt != null)
                try { evt(this, e); }
                catch { }
        }

        private void OnReparseStatus(ReparseStatusEventArgs e) {
            var evt = ReparseStatus;
            if (evt != null)
                try { evt(this, e); }
                catch { }
        }

        private void OnThreadDownloadDirectoryRename(EventArgs e) {
            var evt = ThreadDownloadDirectoryRename;
            if (evt != null)
                try { evt(this, e); }
                catch { }
        }

        private void OnDownloadStart(DownloadStartEventArgs e) {
            var evt = DownloadStart;
            if (evt != null)
                try { evt(this, e); }
                catch { }
        }

        private void OnDownloadProgress(DownloadProgressEventArgs e) {
            var evt = DownloadProgress;
            if (evt != null)
                try { evt(this, e); }
                catch { }
        }

        private void OnDownloadEnd(DownloadEndEventArgs e) {
            var evt = DownloadEnd;
            if (evt != null)
                try { evt(this, e); }
                catch { }
        }

        private void OnAddThread(AddThreadEventArgs e) {
            var evt = AddThread;
            if (evt != null)
                try { evt(this, e); }
                catch { }
        }

        private List<PageInfo> _pageList;
        private HashSet<string> _imageDiskFileNames;
        private Dictionary<string, DownloadInfo> _completedImages;
        private Dictionary<string, DownloadInfo> _completedThumbs;
        private int _maxFileNameLength;
        private int _maxFileNameLengthBaseDir;
        private string _threadName;
        // Read once per check, so that changing the setting during a check can't leave a page
        // with no replace list to be processed, or a page with one unprocessed
        private bool _saveThumbnails;

        private void Check() {
            try {
                _saveThumbnails = Settings.SaveThumbnails != false;
                SiteHelper siteHelper = SiteHelpers.GetInstance(PageHost);

                BeginCheck(siteHelper);

                string threadDir = ThreadDownloadDirectory;
                string imageDir = ThreadDownloadDirectory;
                string thumbDir = Path.Combine(ThreadDownloadDirectory, "thumbs");

                Queue<ImageInfo> pendingImages = new Queue<ImageInfo>();
                Queue<ThumbnailInfo> pendingThumbs = new Queue<ThumbnailInfo>();

                DownloadPages(siteHelper, threadDir, imageDir, thumbDir, pendingImages, pendingThumbs);

                MillisecondsUntilNextCheck = CheckIntervalSeconds * 1000;

                DownloadPendingImages(pendingImages, imageDir);

                if (_saveThumbnails) {
                    DownloadPendingThumbnails(pendingThumbs, thumbDir);
                    ProcessFreshPages(siteHelper, threadDir, imageDir, thumbDir);
                }

                RefetchPagesIfFilesFailed();

                if (OneTimeDownload) {
                    StopOneTimeDownload();
                }
            }
            catch (Exception ex) {
                Stop(StopReason.Other);
                Logger.Log(ex.ToString());
            }

            EndCheck();
        }

        // A one-time download whose page could not be downloaded did not complete. One with
        // downloads held back by a rate limit keeps checking until they have been retried.
        private void StopOneTimeDownload() {
            if (RateLimitedHost != null) return;
            string checkError = CheckError;
            Stop(checkError != null ? StopReason.Other : StopReason.DownloadComplete, checkError);
        }

        // Pages are normally requested with If-Modified-Since, and an unchanged page (304) queues
        // no files. Dropping the cache time makes the next check download the pages again, so
        // that it retries the files that failed and links them in the saved page.
        private void RefetchPagesIfFilesFailed() {
            lock (_settingsSync) {
                if (!_hasFilesToRetry) return;
            }
            DropPageCacheTimes();
        }

        private void DropPageCacheTimes() {
            foreach (PageInfo pageInfo in _pageList) {
                pageInfo.CacheTime = null;
            }
        }

        // Once the rate limit pause is over (including any extension by other downloads), the
        // pages are downloaded in full (not only if modified), so that the files held back by it
        // are queued and retried
        private void RetryRateLimitedDownloadsIfResumed() {
            lock (_settingsSync) {
                if (_rateLimitedConnection == null || _rateLimitedConnection.IsPaused) return;
                _rateLimitedConnection = null;
            }
            DropPageCacheTimes();
        }

        // Holds back this download until the pause of the host in the exception (which may be a
        // meta refresh target rather than the host first requested) is over. A rate limit answer
        // pauses all requests to the host, from every watcher, since a server limits the client
        // as a whole, for as long as the server asked.
        private void HandleRateLimit(RateLimitException ex) {
            ConnectionManager connectionManager = ConnectionManager.GetInstanceForHost(ex.Host);
            HTTPRateLimitedException answer = ex as HTTPRateLimitedException;
            if (answer != null) connectionManager.PauseAndLog(answer.RetryAfter);
            RecordRateLimitedDownload(connectionManager);
        }

        // Reports an unexpected failure to start a request; never throws, so the caller can still
        // end the download
        private void ReportRequestStartFailure(string url, Exception ex, bool isPage) {
            try {
                if (isPage) ReportPageFailure(url, ex);
                else ReportFileFailure(url, ex);
            }
            catch (Exception reportEx) {
                General.LogQuietly(reportEx.ToString());
            }
        }

        // A download that was held back by a rate limit; of several paused hosts, keeps the one
        // whose pause ends last
        private void RecordRateLimitedDownload(ConnectionManager connectionManager) {
            lock (_settingsSync) {
                if (_rateLimitedConnection != null && _rateLimitedConnection.PausedUntilTicks >= connectionManager.PausedUntilTicks) return;
                _rateLimitedConnection = connectionManager;
            }
        }

        private void BeginCheck(SiteHelper siteHelper) {
            try {
                lock (_settingsSync) {
                    _nextCheckWorkItem = null;
                    _checkFinishedEvent.Reset();
                    _isWaiting = false;
                    _checkError = null;
                    _failedFileCount = 0;
                    _hasFilesToRetry = false;

                    if (!_hasInitialized) {
                        InitializeCheckState(siteHelper);
                    }
                }
                RetryRateLimitedDownloadsIfResumed();
            }
            catch (Exception ex) {
                if (IsFatalIOException(ex)) {
                    Stop(StopReason.IOError);
                    Logger.Log(ex.ToString());
                }
                else throw;
            }
        }

        // Must be called while holding _settingsSync
        private void InitializeCheckState(SiteHelper siteHelper) {
            siteHelper.SetURL(_pageURL);

            _pageList = new List<PageInfo> {
                new PageInfo {
                    URL = _pageURL
                }
            };
            _imageDiskFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _completedImages = new Dictionary<string, DownloadInfo>(StringComparer.OrdinalIgnoreCase);
            _completedThumbs = new Dictionary<string, DownloadInfo>(StringComparer.OrdinalIgnoreCase);
            _maxFileNameLengthBaseDir = 0;

            if (String.IsNullOrEmpty(_threadDownloadDirectory)) {
                _threadDownloadDirectory = Path.Combine(
                    GetCategoryDownloadDirectory(),
                    General.CleanFileName(String.Format("{0}_{1}_{2}{3}", siteHelper.GetSiteName(), siteHelper.GetBoardName(), _threadName, ParentThreadFormattedDescription)));
            }
            if (!Directory.Exists(_threadDownloadDirectory)) {
                Directory.CreateDirectory(_threadDownloadDirectory);
            }
            if (String.IsNullOrEmpty(_description)) {
                _description = General.CleanFileName(String.Format("{0}_{1}_{2}", siteHelper.GetSiteName(), siteHelper.GetBoardName(), _threadName));
            }
            _minCheckIntervalSeconds = siteHelper.IsBoardHighTurnover() ? 30 : 60;
            _checkIntervalSeconds = Math.Max(_checkIntervalSeconds, _minCheckIntervalSeconds);

            _hasInitialized = true;
        }

        // Must be called while holding _settingsSync
        private string GetCategoryDownloadDirectory() {
            return Path.Combine(_mainDownloadDirectory, Settings.RenameDownloadFolderWithCategory == true ? General.CleanFileName(_category) : String.Empty);
        }

        private void DownloadPages(SiteHelper siteHelper, string threadDir, string imageDir, string thumbDir, Queue<ImageInfo> pendingImages, Queue<ThumbnailInfo> pendingThumbs) {
            foreach (PageInfo pageInfo in _pageList) {
                // Reset the fresh flag on all of the pages before downloading starts so that
                // they're valid even if stopping before all the pages have been downloaded
                pageInfo.IsFresh = false;
            }

            int pageIndex = 0;
            OnDownloadStatus(new DownloadStatusEventArgs(DownloadType.Page, 0, _pageList.Count));
            while (pageIndex < _pageList.Count && !IsStopping) {
                CheckPage(siteHelper, pageIndex, threadDir, imageDir, thumbDir, pendingImages, pendingThumbs);
                pageIndex++;
                OnDownloadStatus(new DownloadStatusEventArgs(DownloadType.Page, pageIndex, _pageList.Count));
            }
        }

        private void CheckPage(SiteHelper siteHelper, int pageIndex, string threadDir, string imageDir, string thumbDir, Queue<ImageInfo> pendingImages, Queue<ThumbnailInfo> pendingThumbs) {
            string saveFileName = GetPageFileName(_threadName, pageIndex);
            HTMLParser previousParser = null;

            PageInfo pageInfo = _pageList[pageIndex];
            pageInfo.Path = Path.Combine(threadDir, saveFileName);

            if (File.Exists(pageInfo.Path)) {
                previousParser = TryLoadHTMLParser(pageInfo.Path);
            }

            if (!DownloadThreadPage(siteHelper, pageInfo)) return;

            SaveUnprocessedPage(siteHelper.GetHTMLParser(), pageInfo);

            siteHelper.ResurrectDeadPosts(previousParser, pageInfo.ReplaceList);

            if (AutoFollow) {
                AddCrossLinkedThreads(siteHelper, pageInfo);
            }

            EnqueuePageFiles(siteHelper, pageInfo, imageDir, thumbDir, pendingImages, pendingThumbs);

            UpdateNextPage(siteHelper.GetNextPageURL(), pageIndex);
        }

        // Downloads the page and hands it to the site helper. Returns false if no thread page
        // was downloaded (an error, an unchanged page, or a page that is not a thread).
        private bool DownloadThreadPage(SiteHelper siteHelper, PageInfo pageInfo) {
            DateTime? previousCacheTime = pageInfo.CacheTime;
            DownloadedPage page = DownloadPage(pageInfo);
            if (page == null) return false;

            HTMLParser pageParser = TryParsePage(page.Content, pageInfo.URL);
            if (pageParser == null) {
                RejectPage(pageInfo, previousCacheTime, "page could not be read");
                return false;
            }
            ApplyDownloadedPage(pageInfo, page, _saveThumbnails);
            siteHelper.SetURL(pageInfo.URL);
            siteHelper.SetHTMLParser(pageParser);
            if (siteHelper.IsThreadPage()) return true;
            RejectPage(pageInfo, previousCacheTime, "not a thread page");
            return false;
        }

        // A page without a replace list (thumbnails off) is not processed after the check, so the
        // page as downloaded is saved now with only its active content removed
        private static void SaveUnprocessedPage(HTMLParser htmlParser, PageInfo pageInfo) {
            if (pageInfo.ReplaceList != null) return;
            using (StreamWriter sw = new StreamWriter(pageInfo.Path, false, pageInfo.Encoding)) {
                General.WriteReplacedString(htmlParser.PreprocessedHTML, General.GetActiveContentReplaces(htmlParser), sw);
            }
            DeleteBackupIfPageComplete(htmlParser, pageInfo.Path);
        }

        private void EnqueuePageFiles(SiteHelper siteHelper, PageInfo pageInfo, string imageDir, string thumbDir, Queue<ImageInfo> pendingImages, Queue<ThumbnailInfo> pendingThumbs) {
            List<ThumbnailInfo> thumbs = new List<ThumbnailInfo>();
            List<ImageInfo> images = siteHelper.GetImages(pageInfo.ReplaceList, thumbs);
            if (_completedImages.Count == 0) {
                AddExistingImages(images, imageDir);
                AddExistingThumbnails(thumbs, thumbDir);
            }
            EnqueuePendingImages(images, pendingImages);
            EnqueuePendingThumbnails(thumbs, pendingThumbs);
        }

        // Blocks until the download ends; returns null if the page wasn't downloaded. The page is
        // parsed afterwards on the check thread, not in the download callback, so that a parse
        // error cannot keep the end event from being set.
        private DownloadedPage DownloadPage(PageInfo pageInfo) {
            DownloadedPage page = null;
            ManualResetEvent downloadEndEvent = new ManualResetEvent(false);
            DownloadPageEndCallback downloadEnd = (result, content, lastModifiedTime, encoding) => {
                try {
                    if (result == DownloadResult.Completed) {
                        page = new DownloadedPage { Content = content, LastModifiedTime = lastModifiedTime, Encoding = encoding };
                    }
                }
                finally {
                    downloadEndEvent.Set();
                }
            };
            DownloadPageAsync(pageInfo.Path, pageInfo.URL, PageAuth, pageInfo.CacheTime, downloadEnd);
            downloadEndEvent.WaitOne();
            downloadEndEvent.Close();
            return page;
        }

        // Returns null (and logs the exception) if the page could not be parsed
        private static HTMLParser TryParsePage(string content, string url) {
            try {
                return PageParserFactory(content);
            }
            catch (Exception ex) {
                Logger.Log("Error parsing page " + url + ":" + Environment.NewLine + ex);
                return null;
            }
        }

        private static void ApplyDownloadedPage(PageInfo pageInfo, DownloadedPage page, bool saveThumbnails) {
            pageInfo.IsFresh = true;
            pageInfo.CacheTime = page.LastModifiedTime;
            pageInfo.Encoding = page.Encoding;
            pageInfo.ReplaceList = saveThumbnails ? new List<ReplaceInfo>() : null;
        }

        // A page that can't be used (an error, ban or captcha page served with 200 OK, or one
        // that can't be parsed): keep the saved copy of the thread and its cache time, and
        // report the problem
        private void RejectPage(PageInfo pageInfo, DateTime? previousCacheTime, string description) {
            pageInfo.IsFresh = false;
            pageInfo.CacheTime = previousCacheTime;
            RestorePageBackup(pageInfo.Path, pageInfo.Path + ".bak");
            SetCheckError(description, pageInfo.URL, null);
        }

        // Each AddThread event holds a reservation on the root watcher (TryReserveDescendantSlot)
        // that the handler must release with ReleaseDescendantSlot once it has added or rejected
        // the thread
        private void AddCrossLinkedThreads(SiteHelper siteHelper, PageInfo pageInfo) {
            Dictionary<string, ThreadWatcher> descendantThreads = RootThread.DescendantThreads;
            foreach (string crossLink in siteHelper.GetCrossLinks(pageInfo.ReplaceList, Settings.InterBoardAutoFollow != false)) {
                if (!IsNewCrossLink(TryGetCrossLinkPageID(crossLink), descendantThreads)) continue;
                if (!RootThread.TryReserveDescendantSlot()) {
                    LogDescendantLimitReached();
                    return;
                }
                OnAddThread(new AddThreadEventArgs(crossLink));
            }
        }

        private bool IsNewCrossLink(string crossLinkID, Dictionary<string, ThreadWatcher> descendantThreads) {
            return crossLinkID != null && !descendantThreads.ContainsKey(crossLinkID) && RootThread.PageID != crossLinkID;
        }

        // Returns null if the link is not a valid absolute URL of a thread
        private static string TryGetCrossLinkPageID(string crossLink) {
            try {
                SiteHelper crossLinkSiteHelper = SiteHelpers.GetInstance((new Uri(crossLink)).Host);
                crossLinkSiteHelper.SetURL(crossLink);
                return crossLinkSiteHelper.GetPageID();
            }
            catch (Exception ex) {
                Logger.Log("Skipped invalid cross-link " + crossLink + ": " + ex.Message);
                return null;
            }
        }

        private void LogDescendantLimitReached() {
            lock (_settingsSync) {
                if (_loggedDescendantLimit) return;
                _loggedDescendantLimit = true;
            }
            Logger.Log(String.Format("Auto-follow limit of {0} threads reached for {1}; further cross-linked threads are not added.", MaxDescendantThreads, RootThread.PageURL));
        }

        private void AddExistingImages(List<ImageInfo> images, string imageDir) {
            foreach (ImageInfo image in images) {
                if (TryAddExistingImage(image, image.OriginalFileName, imageDir)) continue;
                TryAddExistingImage(image, image.FileName, imageDir);
            }
        }

        private bool TryAddExistingImage(ImageInfo image, string baseFileName, string imageDir) {
            string baseFileNameNoExtension = Path.GetFileNameWithoutExtension(baseFileName);
            string baseExtension = Path.GetExtension(baseFileName);
            string fileName = GetUnusedFileName(baseFileNameNoExtension, baseExtension, _imageDiskFileNames);
            string path = Path.Combine(Path.Combine(imageDir, GetImageFolder(image)), fileName);
            if (!File.Exists(path)) return false;
            _imageDiskFileNames.Add(fileName);
            _completedImages[image.FileName] = new DownloadInfo {
                Folder = GetImageFolder(image),
                FileName = fileName,
                Skipped = false
            };
            return true;
        }

        private void AddExistingThumbnails(List<ThumbnailInfo> thumbs, string thumbDir) {
            foreach (ThumbnailInfo thumb in thumbs) {
                string path = Path.Combine(thumbDir, thumb.FileName);
                if (File.Exists(path)) {
                    _completedThumbs[thumb.FileName] = new DownloadInfo {
                        FileName = thumb.FileName,
                        Skipped = false
                    };
                }
            }
        }

        // Files without a usable file name (e.g. a URL ending in "/..") are never saved
        private void EnqueuePendingImages(List<ImageInfo> images, Queue<ImageInfo> pendingImages) {
            foreach (ImageInfo image in images) {
                if (image.FileName.Length != 0 && !_completedImages.ContainsKey(image.FileName)) {
                    pendingImages.Enqueue(image);
                }
            }
        }

        private void EnqueuePendingThumbnails(List<ThumbnailInfo> thumbs, Queue<ThumbnailInfo> pendingThumbs) {
            foreach (ThumbnailInfo thumb in thumbs) {
                if (thumb.FileName.Length != 0 && !_completedThumbs.ContainsKey(thumb.FileName)) {
                    pendingThumbs.Enqueue(thumb);
                }
            }
        }

        private void UpdateNextPage(string nextPageURL, int pageIndex) {
            if (String.IsNullOrEmpty(nextPageURL)) {
                RemovePagesAfter(pageIndex);
                return;
            }
            PageInfo nextPageInfo = new PageInfo {
                URL = nextPageURL
            };
            if (pageIndex == _pageList.Count - 1) {
                _pageList.Add(nextPageInfo);
            }
            else if (_pageList[pageIndex + 1].URL != nextPageURL) {
                _pageList[pageIndex + 1] = nextPageInfo;
            }
        }

        private void RemovePagesAfter(int pageIndex) {
            if (pageIndex < _pageList.Count - 1) {
                _pageList.RemoveRange(pageIndex + 1, _pageList.Count - (pageIndex + 1));
            }
        }

        private void DownloadPendingImages(Queue<ImageInfo> pendingImages, string imageDir) {
            if (pendingImages.Count == 0 || IsStopping) return;

            InitializeMaxFileNameLengthBaseDir(imageDir);

            List<ManualResetEvent> downloadEndEvents = new List<ManualResetEvent>();
            int completedImageCount = CountNotSkipped(_completedImages);
            DownloadProgressCounts counts = new DownloadProgressCounts {
                Completed = completedImageCount,
                Total = completedImageCount + pendingImages.Count
            };
            OnDownloadStatus(new DownloadStatusEventArgs(DownloadType.Image, counts.Completed, counts.Total));
            while (pendingImages.Count != 0 && !IsStopping) {
                StartImageDownload(pendingImages.Dequeue(), imageDir, counts, downloadEndEvents);
            }
            WaitForDownloadEnds(downloadEndEvents);
            ReleaseUnwrittenFileNames(counts.UnwrittenFileNames);
        }

        // Frees the names reserved for files that were not written, so that a later try saves
        // the file under its own name rather than a numbered one
        private void ReleaseUnwrittenFileNames(List<string> fileNames) {
            foreach (string fileName in fileNames) {
                _imageDiskFileNames.Remove(fileName);
            }
        }

        private void InitializeMaxFileNameLengthBaseDir(string imageDir) {
            if (_maxFileNameLengthBaseDir == 0) {
                _maxFileNameLengthBaseDir = General.GetMaximumFileNameLength(imageDir);
            }
        }

        private void StartImageDownload(ImageInfo image, string imageDir, DownloadProgressCounts counts, List<ManualResetEvent> downloadEndEvents) {
            string savePath = GetImageSavePath(image, imageDir);
            string saveFileName = Path.GetFileName(savePath ?? String.Empty);
            if (saveFileName.Length == 0) return;
            _imageDiskFileNames.Add(saveFileName);

            HashType hashType = (Settings.VerifyImageHashes != false) ? image.HashType : HashType.None;
            ManualResetEvent downloadEndEvent = new ManualResetEvent(false);
            DownloadFileEndCallback onDownloadEnd = (result) => {
                RecordImageResult(image, saveFileName, result, counts);
                downloadEndEvent.Set();
            };
            downloadEndEvents.Add(downloadEndEvent);
            DownloadFileAsync(savePath, image.URL, ImageAuth, image.Referer, hashType, image.Hash, onDownloadEnd);
        }

        // Returns null if the poster folder could not be created
        private string GetImageSavePath(ImageInfo image, string imageDir) {
            if (!UpdateMaxFileNameLength(image, imageDir)) return null;
            string savePath = GetUnusedImageSavePath(image, imageDir, false);
            if (Path.GetFileName(savePath).Length > _maxFileNameLength) {
                // Path too long, fall back to the URL file name
                savePath = GetUnusedImageSavePath(image, imageDir, true);
            }
            return savePath;
        }

        // Returns false (and stops the watcher) if the poster folder could not be created
        private bool UpdateMaxFileNameLength(ImageInfo image, string imageDir) {
            if (!ShouldSortIntoPosterFolder(image)) {
                _maxFileNameLength = _maxFileNameLengthBaseDir;
                return true;
            }
            try {
                Directory.CreateDirectory(Path.Combine(imageDir, image.Poster));
            }
            catch (Exception ex) {
                Stop(StopReason.IOError);
                Logger.Log(ex.ToString());
                return false;
            }
            _maxFileNameLength = General.GetMaximumFileNameLength(Path.Combine(imageDir, image.Poster));
            return true;
        }

        private string GetUnusedImageSavePath(ImageInfo image, string imageDir, bool pathTooLong) {
            string baseFileName = ChooseSaveBaseFileName(image, Settings.UseOriginalFileNames == true, pathTooLong);
            string saveFileNameNoExtension = Path.GetFileNameWithoutExtension(baseFileName);
            string saveExtension = Path.GetExtension(baseFileName);

            int iSuffix = 1;
            string savePath;
            do {
                savePath = Path.Combine(Path.Combine(imageDir, GetImageFolder(image)), GetNumberedFileName(saveFileNameNoExtension, iSuffix, saveExtension));
                iSuffix++;
            } while (_imageDiskFileNames.Contains(Path.GetFileName(savePath)));
            return savePath;
        }

        private void RecordImageResult(ImageInfo image, string saveFileName, DownloadResult result, DownloadProgressCounts counts) {
            lock (_completedImages) {
                if (result != DownloadResult.Completed) {
                    counts.UnwrittenFileNames.Add(saveFileName);
                }
                if (IsCompletedOrSkipped(result)) {
                    RecordCompletedImage(image, saveFileName, result, counts);
                }
                RecordFileRetryState(image.URL, result);
            }
        }

        // Must be called while holding the _completedImages lock
        private void RecordCompletedImage(ImageInfo image, string saveFileName, DownloadResult result, DownloadProgressCounts counts) {
            _completedImages[image.FileName] = new DownloadInfo {
                Folder = GetImageFolder(image),
                FileName = saveFileName,
                Skipped = (result == DownloadResult.Skipped)
            };
            counts.Record(result);
            OnDownloadStatus(new DownloadStatusEventArgs(DownloadType.Image, counts.Completed, counts.Total));
        }

        // A file that did not finish makes the next check download the page again so that the
        // file is retried, but only for a few checks in a row; after that it is retried only when
        // the page changes, so a file that always fails doesn't turn off If-Modified-Since.
        // A file held back by a rate limit is retried after the pause and does not count as failed.
        private void RecordFileRetryState(string url, DownloadResult result) {
            if (result == DownloadResult.RateLimited) return;
            if (result != DownloadResult.RetryLater) {
                lock (_fileFailureCheckCounts) _fileFailureCheckCounts.Remove(url);
                return;
            }
            if (CountFailedCheck(url) <= _maxRefetchesForFailedFile) {
                MarkFilesToRetry();
            }
        }

        private int CountFailedCheck(string url) {
            int count;
            lock (_fileFailureCheckCounts) {
                _fileFailureCheckCounts.TryGetValue(url, out count);
                _fileFailureCheckCounts[url] = ++count;
            }
            if (count == _maxRefetchesForFailedFile + 1) {
                Logger.Log("Giving up retrying " + url + " until the page changes.");
            }
            return count;
        }

        private void MarkFilesToRetry() {
            lock (_settingsSync) {
                _hasFilesToRetry = true;
            }
        }

        private void DownloadPendingThumbnails(Queue<ThumbnailInfo> pendingThumbs, string thumbDir) {
            if (pendingThumbs.Count == 0 || IsStopping) return;

            CreateThumbnailDirectory(thumbDir);

            List<ManualResetEvent> downloadEndEvents = new List<ManualResetEvent>();
            int completedThumbCount = CountNotSkipped(_completedThumbs);
            DownloadProgressCounts counts = new DownloadProgressCounts {
                Completed = completedThumbCount,
                Total = completedThumbCount + pendingThumbs.Count
            };
            OnDownloadStatus(new DownloadStatusEventArgs(DownloadType.Thumbnail, counts.Completed, counts.Total));
            while (pendingThumbs.Count != 0 && !IsStopping) {
                StartThumbnailDownload(pendingThumbs.Dequeue(), thumbDir, counts, downloadEndEvents);
            }
            WaitForDownloadEnds(downloadEndEvents);
        }

        private void CreateThumbnailDirectory(string thumbDir) {
            if (Directory.Exists(thumbDir)) return;
            try {
                Directory.CreateDirectory(thumbDir);
            }
            catch (Exception ex) {
                Stop(StopReason.IOError);
                Logger.Log(ex.ToString());
            }
        }

        private void StartThumbnailDownload(ThumbnailInfo thumb, string thumbDir, DownloadProgressCounts counts, List<ManualResetEvent> downloadEndEvents) {
            string savePath = Path.Combine(thumbDir, thumb.FileName);

            ManualResetEvent downloadEndEvent = new ManualResetEvent(false);
            DownloadFileEndCallback onDownloadEnd = (result) => {
                if (IsCompletedOrSkipped(result)) {
                    RecordCompletedThumbnail(thumb, result, counts);
                }
                RecordFileRetryState(thumb.URL, result);
                downloadEndEvent.Set();
            };
            downloadEndEvents.Add(downloadEndEvent);
            DownloadFileAsync(savePath, thumb.URL, PageAuth, thumb.Referer, HashType.None, null, onDownloadEnd);
        }

        private void RecordCompletedThumbnail(ThumbnailInfo thumb, DownloadResult result, DownloadProgressCounts counts) {
            lock (_completedThumbs) {
                _completedThumbs[thumb.FileName] = new DownloadInfo {
                    FileName = thumb.FileName,
                    Skipped = (result == DownloadResult.Skipped)
                };
                counts.Record(result);
                OnDownloadStatus(new DownloadStatusEventArgs(DownloadType.Thumbnail, counts.Completed, counts.Total));
            }
        }

        private void ProcessFreshPages(SiteHelper siteHelper, string threadDir, string imageDir, string thumbDir) {
            if (IsStopping && StopReason == StopReason.IOError) return;
            foreach (PageInfo pageInfo in _pageList) {
                if (pageInfo.IsFresh) {
                    Process(pageInfo, siteHelper, threadDir, imageDir, thumbDir, _completedImages, _completedThumbs, pageInfo.URL);
                }
            }
        }

        // Records a problem with the thread page; the first one in a check is the one shown
        private void SetCheckError(string description, string url, Exception ex) {
            lock (_settingsSync) {
                if (_checkError == null) _checkError = description;
            }
            Logger.Log("Error downloading page " + url + ": " + description + (ex != null ? Environment.NewLine + ex : String.Empty));
        }

        private void ReportPageFailure(string url, Exception ex) {
            SetCheckError(DescribeDownloadError(ex, url), url, ex);
        }

        private void ReportFileFailure(string url, Exception ex) {
            lock (_settingsSync) {
                _failedFileCount++;
            }
            Logger.Log("Error downloading file " + url + ": " + DescribeDownloadError(ex, url) + Environment.NewLine + ex);
        }

        private static readonly Dictionary<WebExceptionStatus, string> _webExceptionStatusTexts = new Dictionary<WebExceptionStatus, string> {
            { WebExceptionStatus.TrustFailure, "certificate not trusted for {0}" },
            { WebExceptionStatus.SecureChannelFailure, "secure connection failed for {0}" },
            { WebExceptionStatus.NameResolutionFailure, "host not found: {0}" },
            { WebExceptionStatus.ConnectFailure, "cannot connect to {0}" },
            { WebExceptionStatus.Timeout, "timed out connecting to {0}" }
        };

        // A short, plain description of why a download failed, e.g. "HTTP 403 Forbidden"
        internal static string DescribeDownloadError(Exception ex, string url) {
            WebException webEx = ex as WebException;
            if (webEx == null) return DescribeNonWebError(ex);
            if (webEx.Status == WebExceptionStatus.ProtocolError) return DescribeHTTPError(webEx);
            string format;
            if (_webExceptionStatusTexts.TryGetValue(webEx.Status, out format)) return String.Format(format, new Uri(url).Host);
            return webEx.Message;
        }

        private static string DescribeNonWebError(Exception ex) {
            if (ex is IOException) return "connection lost";
            return ex.Message.TrimEnd('.');
        }

        private static string DescribeHTTPError(WebException webEx) {
            HttpWebResponse response = webEx.Response as HttpWebResponse;
            if (response == null) return webEx.Message;
            return String.Format("HTTP {0} {1}", (int)response.StatusCode, response.StatusDescription).TrimEnd();
        }

        private void EndCheck() {
            if (ThreadDownloadDirectoryPendingRename) {
                TryRenameThreadDownloadDirectory(true);
            }
            lock (_settingsSync) {
                _checkFinishedEvent.Set();
                if (!IsStopping) {
                    _nextCheckWorkItem = _workScheduler.AddItem(NextCheckTicks, Check, PageHost);
                    _isWaiting = MillisecondsUntilNextCheck > 0;
                }
            }
            RaiseCheckEndStatus();
        }

        private void RaiseCheckEndStatus() {
            if (IsStopping) {
                OnStopStatus(new StopStatusEventArgs(StopReason));
            }
            else if (IsWaiting) {
                OnWaitStatus(EventArgs.Empty);
            }
        }

        // liveLinkBaseURL is the URL that relative links to files not on disk are resolved
        // against, or null to keep such links as they are in the page
        private void Process(PageInfo pageInfo, SiteHelper siteHelper, string threadDir, string imageDir, string thumbDir, Dictionary<string, DownloadInfo> completedImages, Dictionary<string, DownloadInfo> completedThumbs, string liveLinkBaseURL) {
            HTMLParser htmlParser = siteHelper.GetHTMLParser();
            for (int i = 0; i < pageInfo.ReplaceList.Count; i++) {
                ReplaceInfo replace = pageInfo.ReplaceList[i];
                ApplyDownloadPathReplace(replace, threadDir, imageDir, thumbDir, completedImages, completedThumbs);
                ApplyMissingFileReplace(replace, htmlParser.PreprocessedHTML, liveLinkBaseURL);
                if (!ApplyThreadLinkReplace(replace, siteHelper)) {
                    pageInfo.ReplaceList.RemoveAt(i--);
                }
            }
            General.AddOtherReplaces(htmlParser, pageInfo.URL, pageInfo.ReplaceList);
            using (StreamWriter sw = new StreamWriter(pageInfo.Path, false, pageInfo.Encoding)) {
                General.WriteReplacedString(htmlParser.PreprocessedHTML, pageInfo.ReplaceList, sw);
            }
            DeleteBackupIfPageComplete(htmlParser, pageInfo.Path);
        }

        private static void ApplyDownloadPathReplace(ReplaceInfo replace, string threadDir, string imageDir, string thumbDir, Dictionary<string, DownloadInfo> completedImages, Dictionary<string, DownloadInfo> completedThumbs) {
            DownloadInfo downloadInfo;
            if (replace.Type == ReplaceType.ImageLinkHref && TryGetSavedFile(completedImages, replace.Tag, out downloadInfo)) {
                replace.Value = "href=\"" + General.HtmlAttributeEncode(GetRelativeDownloadPath(downloadInfo, imageDir, threadDir), false) + "\"";
            }
            if (replace.Type == ReplaceType.ImageSrc && TryGetSavedFile(completedThumbs, replace.Tag, out downloadInfo)) {
                replace.Value = "src=\"" + General.HtmlAttributeEncode(GetRelativeDownloadPath(downloadInfo, thumbDir, threadDir), false) + "\"";
            }
        }

        // Skipped downloads (e.g. 404) are not on disk, so they are not linked locally
        private static bool TryGetSavedFile(Dictionary<string, DownloadInfo> completedFiles, string fileName, out DownloadInfo downloadInfo) {
            return completedFiles.TryGetValue(fileName, out downloadInfo) && !downloadInfo.Skipped;
        }

        // A file link with no replacement value would lose its attribute when the page is
        // written, so a file that is not on disk keeps a link to where it is online
        private static void ApplyMissingFileReplace(ReplaceInfo replace, string html, string liveLinkBaseURL) {
            if (replace.Value != null || !IsFileLinkReplace(replace)) return;
            replace.Value = GetLiveFileAttribute(html.Substring(replace.Offset, replace.Length), liveLinkBaseURL);
        }

        private static bool IsFileLinkReplace(ReplaceInfo replace) {
            return replace.Type == ReplaceType.ImageLinkHref || replace.Type == ReplaceType.ImageSrc;
        }

        // Returns the attribute with its URL made absolute, or unchanged if it can't be resolved
        internal static string GetLiveFileAttribute(string attributeHTML, string baseURL) {
            HTMLAttribute attribute = ParseSingleAttribute(attributeHTML);
            string url = (attribute != null && baseURL != null) ? General.GetAbsoluteURL(baseURL, HttpUtility.HtmlDecode(attribute.Value)) : null;
            if (url == null) return attributeHTML;
            return attribute.Name + "=\"" + HttpUtility.HtmlAttributeEncode(url) + "\"";
        }

        private static HTMLAttribute ParseSingleAttribute(string attributeHTML) {
            HTMLTag tag = new HTMLParser("<a " + attributeHTML + ">").FindStartTag("a");
            return (tag != null && tag.Attributes.Count == 1) ? tag.Attributes[0] : null;
        }

        private static string GetRelativeDownloadPath(DownloadInfo downloadInfo, string fileDownloadDir, string threadDir) {
            return General.GetRelativeFilePath(Path.Combine(fileDownloadDir, downloadInfo.Path),
                threadDir).Replace(Path.DirectorySeparatorChar, '/');
        }

        // Returns false if the replace refers to a thread that hasn't been initialized yet and
        // should be removed
        private bool ApplyThreadLinkReplace(ReplaceInfo replace, SiteHelper siteHelper) {
            ThreadWatcher watcher;
            if (!RootThread.DescendantThreads.TryGetValue(replace.Tag, out watcher)) return true;
            if (!watcher._hasInitialized) return false;
            switch (replace.Type) {
                case ReplaceType.QuoteLinkHref:
                    replace.Value = "href=\"" + GetEncodedRelativeThreadPath(watcher) + "\"";
                    break;
                case ReplaceType.DeadLink:
                    string[] tagSplit = replace.Tag.Split('/');
                    string innerHTML = GetDeadLinkInnerHTML(tagSplit, siteHelper.GetBoardName());
                    replace.Value = "<a class=\"quotelink\" href=\"" + GetEncodedRelativeThreadPath(watcher) + "\">" + innerHTML + "</a>";
                    break;
            }
            return true;
        }

        private string GetEncodedRelativeThreadPath(ThreadWatcher watcher) {
            return HttpUtility.HtmlAttributeEncode(General.GetRelativeFilePath(Path.Combine(watcher.ThreadDownloadDirectory, General.CleanFileName(watcher.ThreadName) + ".html"), _threadDownloadDirectory));
        }

        private static string GetDeadLinkInnerHTML(string[] tagSplit, string boardName) {
            return String.Format(">>{0}{1}", boardName != tagSplit[1] ? ">/" + tagSplit[1] + "/" : String.Empty, tagSplit[2]);
        }

        private static void DeleteBackupIfPageComplete(HTMLParser htmlParser, string path) {
            if (htmlParser.FindEndTag("html") != null && File.Exists(path + ".bak")) {
                TryDeleteFile(path + ".bak");
            }
        }

        private void TryRenameThreadDownloadDirectory(bool calledFromCheck) {
            bool renamedDir = false;
            lock (_settingsSync) {
                if (IsThreadDownloadDirectoryRenameBlocked(calledFromCheck)) {
                    return;
                }
                try {
                    RenameThreadDownloadDirectory(ref renamedDir);
                }
                catch (Exception ex) {
                    Logger.Log(ex.ToString());
                }
            }
            if (renamedDir) {
                OnThreadDownloadDirectoryRename(EventArgs.Empty);
            }
        }

        // Must be called while holding _settingsSync
        private bool IsThreadDownloadDirectoryRenameBlocked(bool calledFromCheck) {
            return IsCheckOrReparseBlockingRename(calledFromCheck) ||
                   String.IsNullOrEmpty(_threadDownloadDirectory) ||
                   String.IsNullOrEmpty(_description) ||
                   IsStoppingForIOErrorOrExit();
        }

        // Must be called while holding _settingsSync
        private bool IsCheckOrReparseBlockingRename(bool calledFromCheck) {
            return (!calledFromCheck && !_checkFinishedEvent.WaitOne(0, false)) ||
                   !_reparseFinishedEvent.WaitOne(0, false);
        }

        private bool IsStoppingForIOErrorOrExit() {
            return IsStopping && (StopReason == StopReason.IOError || StopReason == StopReason.Exiting);
        }

        // Must be called while holding _settingsSync. renamedDir is passed by reference so that
        // it stays set if an exception is thrown after the directory has already been moved.
        private void RenameThreadDownloadDirectory(ref bool renamedDir) {
            if (DoNotRename) return;
            string destDir = Path.Combine(
                GetCategoryDownloadDirectory(),
                General.CleanFileName(_description + ParentThreadFormattedDescription));
            if (String.Equals(destDir, _threadDownloadDirectory, StringComparison.Ordinal)) return;

            destDir = FindAvailableRenameDirectory(destDir);
            if (destDir == null) return;

            if (String.Equals(destDir, _threadDownloadDirectory, StringComparison.OrdinalIgnoreCase)) {
                MoveThreadDownloadDirectoryToTemp(destDir);
                renamedDir = true;
            }
            CreateParentDirectory(destDir);
            Directory.Move(_threadDownloadDirectory, destDir);
            DeleteEmptyCategoryDirectory(General.RemoveLastDirectory(_threadDownloadDirectory));
            _threadDownloadDirectory = destDir;
            renamedDir = true;
        }

        // Returns null if the search reaches the current thread download directory
        private string FindAvailableRenameDirectory(string destDir) {
            int count = 2;
            string checkDir = destDir;
            while (Directory.Exists(checkDir)) {
                checkDir = $"{destDir} ({count++})";
                if (String.Equals(checkDir, _threadDownloadDirectory, StringComparison.Ordinal)) return null;
            }
            return checkDir;
        }

        private void MoveThreadDownloadDirectoryToTemp(string destDir) {
            string tempDir = $"{destDir} {Guid.NewGuid()}";
            Directory.Move(_threadDownloadDirectory, tempDir);
            _threadDownloadDirectory = tempDir;
        }

        private static void CreateParentDirectory(string dir) {
            if (!Directory.Exists(General.RemoveLastDirectory(dir))) Directory.CreateDirectory(General.RemoveLastDirectory(dir));
        }

        private void DeleteEmptyCategoryDirectory(string categoryPath) {
            if (categoryPath != MainDownloadDirectory && IsDirectoryEmpty(categoryPath)) {
                try { Directory.Delete(categoryPath); }
                catch { }
            }
        }

        private void DownloadPageAsync(string path, string url, string auth, DateTime? cacheLastModifiedTime, DownloadPageEndCallback onDownloadEnd) {
            PageDownload download = new PageDownload(this, path, url, General.GetAuthForURL(auth, PageURL, url), cacheLastModifiedTime, onDownloadEnd);
            download.TryDownload();
        }

        private void DownloadFileAsync(string path, string url, string auth, string referer, HashType hashType, byte[] correctHash, DownloadFileEndCallback onDownloadEnd) {
            FileDownload download = new FileDownload(this, path, url, General.GetAuthForURL(auth, PageURL, url), referer, hashType, correctHash, onDownloadEnd);
            download.TryDownload();
        }

        private static string GetPageFileName(string threadName, int pageIndex) {
            return GetNumberedFileName(General.CleanFileName(threadName), pageIndex + 1, ".html");
        }

        // The first number gets no suffix, e.g. "name.ext", "name_2.ext", "name_3.ext"
        private static string GetNumberedFileName(string fileNameNoExtension, int number, string extension) {
            return fileNameNoExtension + ((number == 1) ? String.Empty : ("_" + number)) + extension;
        }

        private static string GetUnusedFileName(string fileNameNoExtension, string extension, HashSet<string> usedFileNames) {
            int iSuffix = 1;
            string fileName;
            do {
                fileName = GetNumberedFileName(fileNameNoExtension, iSuffix, extension);
                iSuffix++;
            } while (usedFileNames.Contains(fileName));
            return fileName;
        }

        private static string ChooseSaveBaseFileName(ImageInfo image, bool useOriginalFileNames, bool pathTooLong) {
            if (useOriginalFileNames && !String.IsNullOrEmpty(image.OriginalFileName) && !pathTooLong) {
                return image.OriginalFileName;
            }
            return image.FileName;
        }

        private static string GetImageFolder(ImageInfo image) {
            return Settings.SortImagesByPoster == true ? image.Poster : String.Empty;
        }

        private static bool ShouldSortIntoPosterFolder(ImageInfo image) {
            return Settings.SortImagesByPoster == true && !String.IsNullOrEmpty(image.Poster);
        }

        private static Encoding DetectSavedPageEncoding(string path) {
            try {
                byte[] bytes = File.ReadAllBytes(path);
                return General.DetectHTMLEncoding(bytes, null);
            }
            catch {
                return Encoding.UTF8;
            }
        }

        // Returns null if the file couldn't be read or is empty
        private static HTMLParser TryLoadHTMLParser(string path) {
            string text = TryReadAllText(path);
            return !String.IsNullOrEmpty(text) ? new HTMLParser(text) : null;
        }

        private static string TryReadAllText(string path) {
            try { return File.ReadAllText(path); }
            catch { return null; }
        }

        private static bool IsDirectoryEmpty(string path) {
            return Directory.GetFiles(path).Length == 0 && Directory.GetDirectories(path).Length == 0;
        }

        private static void TryDeleteFile(string path) {
            try { File.Delete(path); }
            catch { }
        }

        private static void TryMoveFile(string sourcePath, string destPath) {
            try { File.Move(sourcePath, destPath); }
            catch { }
        }

        private static void CloseQuietly(Stream stream) {
            if (stream != null)
                try { stream.Close(); }
                catch { }
        }

        private static bool IsFatalIOException(Exception ex) {
            return ex is IOException || ex is UnauthorizedAccessException;
        }

        // A download that gave up waiting for a connection has none to release
        private static void ReleaseConnection(ConnectionManager connectionManager, string connectionGroupName) {
            if (connectionGroupName != null) connectionManager.ReleaseConnectionGroupName(connectionGroupName);
        }

        private static bool IsCompletedOrSkipped(DownloadResult result) {
            return result == DownloadResult.Completed || result == DownloadResult.Skipped;
        }

        private static int CountNotSkipped(Dictionary<string, DownloadInfo> downloads) {
            int count = 0;
            foreach (KeyValuePair<string, DownloadInfo> item in downloads) {
                if (!item.Value.Skipped) count++;
            }
            return count;
        }

        private static void WaitForDownloadEnds(List<ManualResetEvent> downloadEndEvents) {
            foreach (ManualResetEvent downloadEndEvent in downloadEndEvents) {
                downloadEndEvent.WaitOne();
                downloadEndEvent.Close();
            }
        }

        private static long NewDownloadID() {
            return (long)(General.BytesTo64BitXor(Guid.NewGuid().ToByteArray()) & 0x7FFFFFFFFFFFFFFFUL);
        }

        private static bool IsSizeMismatch(long? totalFileSize, long downloadedFileSize) {
            return totalFileSize != null && downloadedFileSize != totalFileSize;
        }

        // A size mismatch only counts as incomplete if it differs from the previous try's size
        private static bool IsIncompleteDownload(long? totalFileSize, long downloadedFileSize, long? prevDownloadedFileSize) {
            return IsSizeMismatch(totalFileSize, downloadedFileSize) &&
                   (prevDownloadedFileSize == null || downloadedFileSize != prevDownloadedFileSize);
        }

        // A hash mismatch only counts as incorrect if it differs from the previous try's hash
        private static bool IsIncorrectHash(HashType hashType, byte[] hash, byte[] correctHash, byte[] prevHash) {
            return IsHashMismatch(hashType, hash, correctHash) &&
                   (prevHash == null || !General.ArraysAreEqual(hash, prevHash));
        }

        private static bool IsHashMismatch(HashType hashType, byte[] hash, byte[] correctHash) {
            return hashType != HashType.None && !General.ArraysAreEqual(hash, correctHash);
        }

        // General.DownloadAsync throws PageTooLargeException for an HTML page over its size limit.
        // Matched by name so that this compiles whether or not that type exists yet.
        private static bool IsPageTooLarge(Exception ex) {
            return ex is PageTooLargeException;
        }

        // Moves the copy of the page saved before the download back in place
        private static void RestorePageBackup(string path, string backupPath) {
            TryDeleteFile(path);
            if (File.Exists(backupPath)) {
                TryMoveFile(backupPath, path);
            }
        }

        // Runs a local file operation, wrapping any IO failure in a LocalFileException so that it
        // is not mistaken for a network failure (which can also surface as an IOException)
        private static void RunFileOperation(Action operation) {
            try {
                operation();
            }
            catch (Exception ex) {
                if (IsFatalIOException(ex)) throw new LocalFileException(ex);
                throw;
            }
        }

        // Preallocates at most _maxPreallocateBytes, whatever size the server announces
        private static void PreallocateFile(FileStream fileStream, long? totalFileSize) {
            if (totalFileSize == null) return;
            fileStream.SetLength(Math.Min(totalFileSize.Value, _maxPreallocateBytes));
        }

        private static long? GetAnnouncedSize(HttpWebResponse response) {
            return response.ContentLength != -1 ? response.ContentLength : (long?)null;
        }

        private static void ThrowIfTooLarge(long? fileSize) {
            if (fileSize > MaxFileBytes) throw new FileTooLargeException();
        }

        // Shared between a download's completion callbacks; guarded by the caller's lock
        private sealed class DownloadProgressCounts {
            public int Completed;
            public int Total;
            public readonly List<string> UnwrittenFileNames = new List<string>();

            public void Record(DownloadResult result) {
                if (result != DownloadResult.Skipped) {
                    Completed++;
                }
                else {
                    Total--;
                }
            }
        }

        private sealed class DownloadedPage {
            public string Content;
            public DateTime? LastModifiedTime;
            public Encoding Encoding;
        }

        // A failure to create or write a file on the local disk
        private sealed class LocalFileException : Exception {
            public LocalFileException(Exception innerException) :
                base("cannot write file: " + innerException.Message, innerException)
            {
            }
        }

        private sealed class FileTooLargeException : Exception {
            public FileTooLargeException() :
                base(String.Format("file is larger than the limit of {0} bytes", MaxFileBytes))
            {
            }
        }

        // State shared by all of the tries of one page download
        private sealed class PageDownload {
            private readonly ThreadWatcher _watcher;
            private readonly string _path;
            private readonly string _url;
            private readonly string _auth;
            private readonly DateTime? _cacheLastModifiedTime;
            private readonly DownloadPageEndCallback _onDownloadEnd;
            private readonly ConnectionManager _connectionManager;
            private readonly string _backupPath;
            private string _connectionGroupName;
            private int _tryNumber;
            // Set once the download has ended, so that it never ends (and releases its connection) twice
            private int _ended;
            private long? _prevDownloadedFileSize;
            private Exception _lastError;

            public PageDownload(ThreadWatcher watcher, string path, string url, string auth, DateTime? cacheLastModifiedTime, DownloadPageEndCallback onDownloadEnd) {
                _watcher = watcher;
                _path = path;
                _url = url;
                _auth = auth;
                _cacheLastModifiedTime = cacheLastModifiedTime;
                _onDownloadEnd = onDownloadEnd;
                _connectionManager = ConnectionManager.GetInstance(url);
                // Gives up waiting for a connection (and takes none) if the watcher stops
                _connectionGroupName = _connectionManager.ObtainConnectionGroupName(() => watcher.IsStopping);
                _backupPath = path + ".bak";
            }

            public void TryDownload() {
                _tryNumber++;
                if (TryEndWithoutSending()) return;
                // Sent no sooner than the host's minimum interval after the previous request to it
                _connectionManager.StartRequest(StartAttempt);
            }

            // The watcher may have stopped, or the host been paused, while the request waited.
            // This may run on a scheduler thread, so an unexpected failure still ends the download.
            private void StartAttempt() {
                try {
                    BeforeRequestStart(_url);
                    if (TryEndWithoutSending()) return;
                    new Attempt(this).Start();
                }
                catch (Exception ex) {
                    _watcher.ReportRequestStartFailure(_url, ex, true);
                    new Attempt(this).EndTryDownload(DownloadResult.RetryLater);
                }
            }

            // Ends the download without sending a request if the watcher is stopping, the tries
            // are used up, or the host is rate limiting this client (nothing is sent to it until
            // the pause is over). Returns false if the request may be sent.
            private bool TryEndWithoutSending() {
                if (_watcher.IsStopping || _tryNumber > _maxDownloadTries) {
                    ReportIfOutOfTries();
                    new Attempt(this).EndTryDownload(DownloadResult.RetryLater);
                    return true;
                }
                if (!_connectionManager.IsPaused) return false;
                _watcher.RecordRateLimitedDownload(_connectionManager);
                new Attempt(this).EndTryDownload(DownloadResult.RateLimited);
                return true;
            }

            private void ReportIfOutOfTries() {
                if (_watcher.IsStopping || _lastError == null) return;
                _watcher.ReportPageFailure(_url, _lastError);
            }

            private void Retry(Exception ex) {
                _lastError = ex;
                _connectionGroupName = _connectionManager.SwapForFreshConnection(_connectionGroupName, _url);
                TryDownload();
            }

            // State of a single try
            private sealed class Attempt {
                private readonly PageDownload _download;
                private readonly ThreadWatcher _watcher;
                private string _httpContentType;
                private DateTime? _lastModifiedTime;
                private Encoding _encoding;
                private string _content;
                private long _downloadID;
                private FileStream _fileStream;
                private long? _totalFileSize;
                private long _downloadedFileSize;
                private bool _createdFile;
                private MemoryStream _memoryStream;
                private bool _removedDownloadAborter;

                public Attempt(PageDownload download) {
                    _download = download;
                    _watcher = download._watcher;
                }

                public void EndTryDownload(DownloadResult result) {
                    if (Interlocked.Exchange(ref _download._ended, 1) != 0) return;
                    ReleaseConnection(_download._connectionManager, _download._connectionGroupName);
                    _download._onDownloadEnd(result, _content, _lastModifiedTime, _encoding);
                }

                public void Start() {
                    _downloadID = NewDownloadID();
                    Action abortDownload = General.DownloadAsync(_download._url, _download._auth, null, _download._connectionGroupName, _download._cacheLastModifiedTime,
                        OnResponse, OnDownloadChunk, OnComplete, OnException);

                    lock (_watcher._downloadAborters) {
                        if (!_removedDownloadAborter) {
                            _watcher._downloadAborters[_downloadID] = abortDownload;
                        }
                    }
                }

                private void Cleanup(bool successful) {
                    CloseQuietly(_fileStream);
                    CloseQuietly(_memoryStream);
                    if (!successful && _createdFile) {
                        RestorePageBackup(_download._path, _download._backupPath);
                    }
                    lock (_watcher._downloadAborters) {
                        _watcher._downloadAborters.Remove(_downloadID);
                        _removedDownloadAborter = true;
                    }
                }

                // A backup that already exists is the last complete copy (the saved page is only
                // left incomplete when one exists), so it is kept and the page is overwritten.
                // Otherwise the page is moved to the backup; if that fails, File.Move throws and
                // the attempt fails as a local file error before the page is overwritten.
                private void BackupExistingPage() {
                    if (!File.Exists(_download._path) || File.Exists(_download._backupPath)) return;
                    File.Move(_download._path, _download._backupPath);
                }

                private void OnResponse(HttpWebResponse response) {
                    _totalFileSize = GetAnnouncedSize(response);
                    RunFileOperation(CreateFile);
                    _memoryStream = new MemoryStream();
                    _httpContentType = response.ContentType;
                    _lastModifiedTime = General.GetResponseLastModifiedTime(response);
                    _watcher.OnDownloadStart(new DownloadStartEventArgs(_downloadID, _download._url, _download._tryNumber, _totalFileSize));
                }

                private void CreateFile() {
                    BackupExistingPage();
                    _fileStream = new FileStream(_download._path, FileMode.Create, FileAccess.Write, FileShare.Read);
                    _createdFile = true;
                    PreallocateFile(_fileStream, _totalFileSize);
                }

                private void OnDownloadChunk(byte[] data, int dataLength) {
                    RunFileOperation(() => _fileStream.Write(data, 0, dataLength));
                    _memoryStream.Write(data, 0, dataLength);
                    _downloadedFileSize += dataLength;
                    _watcher.OnDownloadProgress(new DownloadProgressEventArgs(_downloadID, _downloadedFileSize));
                }

                private void OnComplete() {
                    byte[] pageBytes = _memoryStream.ToArray();
                    if (IsSizeMismatch(_totalFileSize, _downloadedFileSize)) {
                        RunFileOperation(() => _fileStream.SetLength(_downloadedFileSize));
                    }
                    if (IsIncompleteDownload(_totalFileSize, _downloadedFileSize, _download._prevDownloadedFileSize)) {
                        // Corrupt download, retry
                        _download._prevDownloadedFileSize = _downloadedFileSize;
                        throw new Exception("Download is corrupt.");
                    }
                    Cleanup(true);
                    _watcher.OnDownloadEnd(new DownloadEndEventArgs(_downloadID, _downloadedFileSize, true));
                    _encoding = General.DetectHTMLEncoding(pageBytes, _httpContentType);
                    _content = _encoding.GetString(pageBytes);
                    EndTryDownload(DownloadResult.Completed);
                }

                private void OnException(Exception ex) {
                    Cleanup(false);
                    _watcher.OnDownloadEnd(new DownloadEndEventArgs(_downloadID, _downloadedFileSize, false));
                    if (ex is RateLimitException) {
                        // Too many requests, or the host is paused: retry after the pause
                        _watcher.HandleRateLimit((RateLimitException)ex);
                        EndTryDownload(DownloadResult.RateLimited);
                    }
                    else if (!TryEndWithoutRetry(ex)) {
                        // Other error (HTTP status, TLS, network), retry
                        _download.Retry(ex);
                    }
                }

                // Ends the download if another try would not help; returns false for any other error
                private bool TryEndWithoutRetry(Exception ex) {
                    if (ex is HTTP304Exception) {
                        // Page not modified, skip
                    }
                    else if (ex is HTTP404Exception) {
                        // Page not found, stop
                        _watcher.Stop(StopReason.PageNotFound);
                    }
                    else if (ex is LocalFileException) {
                        // Fatal IO error, stop
                        Logger.Log("Error saving page " + _download._path + ":" + Environment.NewLine + ex.InnerException);
                        _watcher.Stop(StopReason.IOError);
                    }
                    else if (IsPageTooLarge(ex)) {
                        // Another try would get the same page, so report it now
                        _watcher.ReportPageFailure(_download._url, ex);
                    }
                    else {
                        return false;
                    }
                    EndTryDownload(DownloadResult.Skipped);
                    return true;
                }
            }
        }

        // State shared by all of the tries of one file download
        private sealed class FileDownload {
            private readonly ThreadWatcher _watcher;
            private readonly string _path;
            private readonly string _url;
            private readonly string _auth;
            private readonly string _referer;
            private readonly HashType _hashType;
            private readonly byte[] _correctHash;
            private readonly DownloadFileEndCallback _onDownloadEnd;
            private readonly ConnectionManager _connectionManager;
            private string _connectionGroupName;
            private int _tryNumber;
            // Set once the download has ended, so that it never ends (and releases its connection) twice
            private int _ended;
            private byte[] _prevHash;
            private long? _prevDownloadedFileSize;
            private Exception _lastError;

            public FileDownload(ThreadWatcher watcher, string path, string url, string auth, string referer, HashType hashType, byte[] correctHash, DownloadFileEndCallback onDownloadEnd) {
                _watcher = watcher;
                _path = path;
                _url = url;
                _auth = auth;
                _referer = referer;
                _hashType = hashType;
                _correctHash = correctHash;
                _onDownloadEnd = onDownloadEnd;
                _connectionManager = ConnectionManager.GetInstance(url);
                // Gives up waiting for a connection (and takes none) if the watcher stops
                _connectionGroupName = _connectionManager.ObtainConnectionGroupName(() => watcher.IsStopping);
            }

            public void TryDownload() {
                _tryNumber++;
                if (TryEndWithoutSending()) return;
                // Sent no sooner than the host's minimum interval after the previous request to it
                _connectionManager.StartRequest(StartAttempt);
            }

            // The watcher may have stopped, or the host been paused, while the request waited.
            // This may run on a scheduler thread, so an unexpected failure still ends the download.
            private void StartAttempt() {
                try {
                    BeforeRequestStart(_url);
                    if (TryEndWithoutSending()) return;
                    new Attempt(this).Start();
                }
                catch (Exception ex) {
                    _watcher.ReportRequestStartFailure(_url, ex, false);
                    new Attempt(this).EndTryDownload(DownloadResult.RetryLater);
                }
            }

            // Ends the download without sending a request if the watcher is stopping, the tries
            // are used up, or the host is rate limiting this client (nothing is sent to it until
            // the pause is over). Returns false if the request may be sent.
            private bool TryEndWithoutSending() {
                if (_watcher.IsStopping || _tryNumber > _maxDownloadTries) {
                    ReportIfOutOfTries();
                    new Attempt(this).EndTryDownload(DownloadResult.RetryLater);
                    return true;
                }
                if (!_connectionManager.IsPaused) return false;
                _watcher.RecordRateLimitedDownload(_connectionManager);
                new Attempt(this).EndTryDownload(DownloadResult.RateLimited);
                return true;
            }

            private void ReportIfOutOfTries() {
                if (_watcher.IsStopping || _lastError == null) return;
                _watcher.ReportFileFailure(_url, _lastError);
            }

            private void Retry(Exception ex) {
                _lastError = ex;
                _connectionGroupName = _connectionManager.SwapForFreshConnection(_connectionGroupName, _url);
                TryDownload();
            }

            // State of a single try
            private sealed class Attempt {
                private readonly FileDownload _download;
                private readonly ThreadWatcher _watcher;
                private long _downloadID;
                private FileStream _fileStream;
                private long? _totalFileSize;
                private long _downloadedFileSize;
                private bool _createdFile;
                private HashGeneratorStream _hashStream;
                private bool _removedDownloadAborter;

                public Attempt(FileDownload download) {
                    _download = download;
                    _watcher = download._watcher;
                }

                public void EndTryDownload(DownloadResult result) {
                    if (Interlocked.Exchange(ref _download._ended, 1) != 0) return;
                    ReleaseConnection(_download._connectionManager, _download._connectionGroupName);
                    _download._onDownloadEnd(result);
                }

                public void Start() {
                    _downloadID = NewDownloadID();
                    Action abortDownload = General.DownloadAsync(_download._url, _download._auth, _download._referer, _download._connectionGroupName, null,
                        OnResponse, OnDownloadChunk, OnComplete, OnException);

                    lock (_watcher._downloadAborters) {
                        if (!_removedDownloadAborter) {
                            _watcher._downloadAborters[_downloadID] = abortDownload;
                        }
                    }
                }

                private void Cleanup(bool successful) {
                    CloseQuietly(_fileStream);
                    CloseQuietly(_hashStream);
                    if (!successful && _createdFile) {
                        TryDeleteFile(_download._path);
                    }
                    lock (_watcher._downloadAborters) {
                        _watcher._downloadAborters.Remove(_downloadID);
                        _removedDownloadAborter = true;
                    }
                }

                private void OnResponse(HttpWebResponse response) {
                    // The host answers file requests normally again (a page answer does not count:
                    // a limit may apply to files only)
                    _download._connectionManager.ResetRateLimitBackoff();
                    _totalFileSize = GetAnnouncedSize(response);
                    ThrowIfTooLarge(_totalFileSize);
                    RunFileOperation(CreateFile);
                    if (_download._hashType != HashType.None) {
                        _hashStream = new HashGeneratorStream(_download._hashType);
                    }
                    _watcher.OnDownloadStart(new DownloadStartEventArgs(_downloadID, _download._url, _download._tryNumber, _totalFileSize));
                }

                private void CreateFile() {
                    _fileStream = new FileStream(_download._path, FileMode.Create, FileAccess.Write, FileShare.Read);
                    _createdFile = true;
                    PreallocateFile(_fileStream, _totalFileSize);
                }

                private void OnDownloadChunk(byte[] data, int dataLength) {
                    ThrowIfTooLarge(_downloadedFileSize + dataLength);
                    RunFileOperation(() => _fileStream.Write(data, 0, dataLength));
                    if (_hashStream != null) _hashStream.Write(data, 0, dataLength);
                    _downloadedFileSize += dataLength;
                    _watcher.OnDownloadProgress(new DownloadProgressEventArgs(_downloadID, _downloadedFileSize));
                }

                private byte[] GetDownloadedHash() {
                    return (_download._hashType != HashType.None) ? _hashStream.GetDataHash() : null;
                }

                private void OnComplete() {
                    byte[] hash = GetDownloadedHash();
                    if (IsSizeMismatch(_totalFileSize, _downloadedFileSize)) {
                        RunFileOperation(() => _fileStream.SetLength(_downloadedFileSize));
                    }
                    bool incorrectHash = IsIncorrectHash(_download._hashType, hash, _download._correctHash, _download._prevHash);
                    bool incompleteDownload = IsIncompleteDownload(_totalFileSize, _downloadedFileSize, _download._prevDownloadedFileSize);
                    if (incorrectHash || incompleteDownload) {
                        // Corrupt download, retry
                        _download._prevHash = hash;
                        _download._prevDownloadedFileSize = _downloadedFileSize;
                        throw new Exception("Download is corrupt.");
                    }
                    WarnIfHashMismatch(hash);
                    Cleanup(true);
                    _watcher.OnDownloadEnd(new DownloadEndEventArgs(_downloadID, _downloadedFileSize, true));
                    EndTryDownload(DownloadResult.Completed);
                }

                // Two tries that return the same bytes are accepted even if they don't match the
                // hash in the page, since some sites publish wrong hashes
                private void WarnIfHashMismatch(byte[] hash) {
                    if (!IsHashMismatch(_download._hashType, hash, _download._correctHash)) return;
                    Logger.Log("Warning: saved " + _download._path + " although it does not match the hash given by the page, because two downloads of " +
                        _download._url + " returned the same data.");
                }

                private void OnException(Exception ex) {
                    Cleanup(false);
                    _watcher.OnDownloadEnd(new DownloadEndEventArgs(_downloadID, _downloadedFileSize, false));
                    if (ex is LocalFileException) {
                        HandleLocalFileError(ex);
                    }
                    else if (ex is HTTP404Exception) {
                        // File deleted from the server, skip; the host is answering normally
                        _download._connectionManager.ResetRateLimitBackoff();
                        EndTryDownload(DownloadResult.Skipped);
                    }
                    else if (ex is RateLimitException) {
                        // Too many requests, or the host is paused: retry after the pause
                        _watcher.HandleRateLimit((RateLimitException)ex);
                        EndTryDownload(DownloadResult.RateLimited);
                    }
                    else if (ex is FileTooLargeException) {
                        _watcher.ReportFileFailure(_download._url, ex);
                        EndTryDownload(DownloadResult.Skipped);
                    }
                    else {
                        // Other error (HTTP status, TLS, network, corrupt data), retry
                        _download.Retry(ex);
                    }
                }

                private void HandleLocalFileError(Exception ex) {
                    Exception inner = ex.InnerException;
                    if (inner is DirectoryNotFoundException || inner is UnauthorizedAccessException) {
                        // Fatal IO error, stop
                        Logger.Log("Error saving file " + _download._path + ":" + Environment.NewLine + inner);
                        _watcher.Stop(StopReason.IOError);
                        EndTryDownload(DownloadResult.Skipped);
                    }
                    else {
                        // Problem with this one file (e.g. disk full, file in use), try it again
                        // in a later check
                        _watcher.ReportFileFailure(_download._url, ex);
                        EndTryDownload(DownloadResult.RetryLater);
                    }
                }
            }
        }
    }
}
