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

        private static WorkScheduler _workScheduler = new WorkScheduler();

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

        static ThreadWatcher() {
            // HttpWebRequest uses ThreadPool for asynchronous calls
            General.EnsureThreadPoolMaxThreads(500, 1000);

            // Shouldn't matter since the limit is supposed to be per connection group
            ServicePointManager.DefaultConnectionLimit = Int32.MaxValue;

            // Ignore invalid certificates (workaround for Mono)
            ServicePointManager.ServerCertificateValidationCallback = (s, cert, chain, errors) => true;

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
                           !String.Equals(General.GetLastDirectory(_threadDownloadDirectory), General.CleanFileName(_description), StringComparison.Ordinal);
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
                    (!String.IsNullOrEmpty(Settings.ParentThreadDescriptionFormat) && _description.EndsWith(Settings.ParentThreadDescriptionFormat.Replace("{Parent}", ParentThread.Description))))
                {
                    return String.Empty;
                }
                return Settings.ParentThreadDescriptionFormat.Replace("{Parent}", ParentThread.Description);
            }
        }

        public object Tag {
            get { lock (_settingsSync) { return _tag; } }
            set { lock (_settingsSync) { _tag = value; } }
        }

        public Dictionary<string, ThreadWatcher> ChildThreads {
            get { lock (_settingsSync) { return _childThreads; } }
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
                _hasRun = true;
                _hasInitialized = false;
                _nextCheckWorkItem = _workScheduler.AddItem(TickCount.Now, Check, PageHost);
            }
        }

        public void Stop(StopReason reason) {
            bool stoppingNow = false;
            bool checkFinished = false;
            List<Action> downloadAborters = null;
            lock (_settingsSync) {
                if (!IsStopping) {
                    stoppingNow = true;
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
            }

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

            lock (_settingsSync) {
                _reparseFinishedEvent.Set();
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
            Process(pageInfo, siteHelper, threadDir, imageDir, thumbDir, completedImages, completedThumbs);
            OnStopStatus(new StopStatusEventArgs(StopReason));
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

        private void Check() {
            try {
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

                if (Settings.SaveThumbnails != false) {
                    DownloadPendingThumbnails(pendingThumbs, thumbDir);
                    ProcessFreshPages(siteHelper, threadDir, imageDir, thumbDir);
                }

                if (OneTimeDownload) {
                    Stop(StopReason.DownloadComplete);
                }
            }
            catch (Exception ex) {
                Stop(StopReason.Other);
                Logger.Log(ex.ToString());
            }

            EndCheck();
        }

        private void BeginCheck(SiteHelper siteHelper) {
            try {
                lock (_settingsSync) {
                    _nextCheckWorkItem = null;
                    _checkFinishedEvent.Reset();
                    _isWaiting = false;

                    if (!_hasInitialized) {
                        InitializeCheckState(siteHelper);
                    }
                }
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

            HTMLParser pageParser = DownloadPage(pageInfo);
            if (pageParser == null) return;

            siteHelper.SetURL(pageInfo.URL);
            siteHelper.SetHTMLParser(pageParser);
            siteHelper.ResurrectDeadPosts(previousParser, pageInfo.ReplaceList);

            if (AutoFollow) {
                AddCrossLinkedThreads(siteHelper, pageInfo);
            }

            List<ThumbnailInfo> thumbs = new List<ThumbnailInfo>();
            List<ImageInfo> images = siteHelper.GetImages(pageInfo.ReplaceList, thumbs);
            if (_completedImages.Count == 0) {
                AddExistingImages(images, imageDir);
                AddExistingThumbnails(thumbs, thumbDir);
            }
            EnqueuePendingImages(images, pendingImages);
            EnqueuePendingThumbnails(thumbs, pendingThumbs);

            UpdateNextPage(siteHelper.GetNextPageURL(), pageIndex);
        }

        // Blocks until the download ends; returns null if the page wasn't downloaded
        private HTMLParser DownloadPage(PageInfo pageInfo) {
            HTMLParser pageParser = null;
            ManualResetEvent downloadEndEvent = new ManualResetEvent(false);
            DownloadPageEndCallback downloadEnd = (result, content, lastModifiedTime, encoding) => {
                if (result == DownloadResult.Completed) {
                    pageInfo.IsFresh = true;
                    pageParser = new HTMLParser(content);
                    pageInfo.CacheTime = lastModifiedTime;
                    pageInfo.Encoding = encoding;
                    pageInfo.ReplaceList = (Settings.SaveThumbnails != false) ? new List<ReplaceInfo>() : null;
                }
                downloadEndEvent.Set();
            };
            DownloadPageAsync(pageInfo.Path, pageInfo.URL, PageAuth, pageInfo.CacheTime, downloadEnd);
            downloadEndEvent.WaitOne();
            downloadEndEvent.Close();
            return pageParser;
        }

        private void AddCrossLinkedThreads(SiteHelper siteHelper, PageInfo pageInfo) {
            foreach (string crossLink in siteHelper.GetCrossLinks(pageInfo.ReplaceList, Settings.InterBoardAutoFollow != false)) {
                SiteHelper crossLinkSiteHelper = SiteHelpers.GetInstance((new Uri(crossLink)).Host);
                crossLinkSiteHelper.SetURL(crossLink);
                string crossLinkID = crossLinkSiteHelper.GetPageID();
                if (!RootThread.DescendantThreads.ContainsKey(crossLinkID) && RootThread.PageID != crossLinkID) OnAddThread(new AddThreadEventArgs(crossLink));
            }
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

        private void EnqueuePendingImages(List<ImageInfo> images, Queue<ImageInfo> pendingImages) {
            foreach (ImageInfo image in images) {
                if (!_completedImages.ContainsKey(image.FileName)) {
                    pendingImages.Enqueue(image);
                }
            }
        }

        private void EnqueuePendingThumbnails(List<ThumbnailInfo> thumbs, Queue<ThumbnailInfo> pendingThumbs) {
            foreach (ThumbnailInfo thumb in thumbs) {
                if (!_completedThumbs.ContainsKey(thumb.FileName)) {
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
        }

        private void InitializeMaxFileNameLengthBaseDir(string imageDir) {
            if (_maxFileNameLengthBaseDir == 0) {
                _maxFileNameLengthBaseDir = General.GetMaximumFileNameLength(imageDir);
            }
        }

        private void StartImageDownload(ImageInfo image, string imageDir, DownloadProgressCounts counts, List<ManualResetEvent> downloadEndEvents) {
            string savePath = GetImageSavePath(image, imageDir);
            string saveFileName = Path.GetFileName(savePath);
            _imageDiskFileNames.Add(saveFileName);

            HashType hashType = (Settings.VerifyImageHashes != false) ? image.HashType : HashType.None;
            ManualResetEvent downloadEndEvent = new ManualResetEvent(false);
            DownloadFileEndCallback onDownloadEnd = (result) => {
                if (IsCompletedOrSkipped(result)) {
                    RecordCompletedImage(image, saveFileName, result, counts);
                }
                downloadEndEvent.Set();
            };
            downloadEndEvents.Add(downloadEndEvent);
            DownloadFileAsync(savePath, image.URL, ImageAuth, image.Referer, hashType, image.Hash, onDownloadEnd);
        }

        private string GetImageSavePath(ImageInfo image, string imageDir) {
            UpdateMaxFileNameLength(image, imageDir);
            string savePath = GetUnusedImageSavePath(image, imageDir, false);
            if (Path.GetFileName(savePath).Length > _maxFileNameLength) {
                // Path too long, fall back to the URL file name
                savePath = GetUnusedImageSavePath(image, imageDir, true);
            }
            return savePath;
        }

        private void UpdateMaxFileNameLength(ImageInfo image, string imageDir) {
            if (!ShouldSortIntoPosterFolder(image)) {
                _maxFileNameLength = _maxFileNameLengthBaseDir;
                return;
            }
            try {
                Directory.CreateDirectory(Path.Combine(imageDir, image.Poster));
            }
            catch (Exception ex) {
                Stop(StopReason.IOError);
                Logger.Log(ex.ToString());
            }
            _maxFileNameLength = General.GetMaximumFileNameLength(Path.Combine(imageDir, image.Poster));
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

        private void RecordCompletedImage(ImageInfo image, string saveFileName, DownloadResult result, DownloadProgressCounts counts) {
            lock (_completedImages) {
                _completedImages[image.FileName] = new DownloadInfo {
                    Folder = GetImageFolder(image),
                    FileName = saveFileName,
                    Skipped = (result == DownloadResult.Skipped)
                };
                counts.Record(result);
                OnDownloadStatus(new DownloadStatusEventArgs(DownloadType.Image, counts.Completed, counts.Total));
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
                    Process(pageInfo, siteHelper, threadDir, imageDir, thumbDir, _completedImages, _completedThumbs);
                }
            }
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

        private void Process(PageInfo pageInfo, SiteHelper siteHelper, string threadDir, string imageDir, string thumbDir, Dictionary<string, DownloadInfo> completedImages, Dictionary<string, DownloadInfo> completedThumbs) {
            HTMLParser htmlParser = siteHelper.GetHTMLParser();
            for (int i = 0; i < pageInfo.ReplaceList.Count; i++) {
                ReplaceInfo replace = pageInfo.ReplaceList[i];
                ApplyDownloadPathReplace(replace, threadDir, imageDir, thumbDir, completedImages, completedThumbs);
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
            if (replace.Type == ReplaceType.ImageLinkHref && completedImages.TryGetValue(replace.Tag, out downloadInfo)) {
                replace.Value = "href=\"" + General.HtmlAttributeEncode(GetRelativeDownloadPath(downloadInfo, imageDir, threadDir), false) + "\"";
            }
            if (replace.Type == ReplaceType.ImageSrc && completedThumbs.TryGetValue(replace.Tag, out downloadInfo)) {
                replace.Value = "src=\"" + General.HtmlAttributeEncode(GetRelativeDownloadPath(downloadInfo, thumbDir, threadDir), false) + "\"";
            }
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
            PageDownload download = new PageDownload(this, path, url, auth, cacheLastModifiedTime, onDownloadEnd);
            download.TryDownload();
        }

        private void DownloadFileAsync(string path, string url, string auth, string referer, HashType hashType, byte[] correctHash, DownloadFileEndCallback onDownloadEnd) {
            FileDownload download = new FileDownload(this, path, url, auth, referer, hashType, correctHash, onDownloadEnd);
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
            return hashType != HashType.None && !General.ArraysAreEqual(hash, correctHash) &&
                   (prevHash == null || !General.ArraysAreEqual(hash, prevHash));
        }

        // Shared between a download's completion callbacks; guarded by the caller's lock
        private sealed class DownloadProgressCounts {
            public int Completed;
            public int Total;

            public void Record(DownloadResult result) {
                if (result != DownloadResult.Skipped) {
                    Completed++;
                }
                else {
                    Total--;
                }
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
            private long? _prevDownloadedFileSize;

            public PageDownload(ThreadWatcher watcher, string path, string url, string auth, DateTime? cacheLastModifiedTime, DownloadPageEndCallback onDownloadEnd) {
                _watcher = watcher;
                _path = path;
                _url = url;
                _auth = auth;
                _cacheLastModifiedTime = cacheLastModifiedTime;
                _onDownloadEnd = onDownloadEnd;
                _connectionManager = ConnectionManager.GetInstance(url);
                _connectionGroupName = _connectionManager.ObtainConnectionGroupName();
                _backupPath = path + ".bak";
            }

            public void TryDownload() {
                Attempt attempt = new Attempt(this);
                _tryNumber++;
                if (_watcher.IsStopping || _tryNumber > _maxDownloadTries) {
                    attempt.EndTryDownload(DownloadResult.RetryLater);
                    return;
                }
                attempt.Start();
            }

            private void Retry() {
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
                    _download._connectionManager.ReleaseConnectionGroupName(_download._connectionGroupName);
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
                        RestoreBackup();
                    }
                    lock (_watcher._downloadAborters) {
                        _watcher._downloadAborters.Remove(_downloadID);
                        _removedDownloadAborter = true;
                    }
                }

                private void RestoreBackup() {
                    TryDeleteFile(_download._path);
                    if (File.Exists(_download._backupPath)) {
                        TryMoveFile(_download._backupPath, _download._path);
                    }
                }

                private void BackupExistingPage() {
                    if (!File.Exists(_download._path)) return;
                    if (File.Exists(_download._backupPath)) {
                        TryDeleteFile(_download._backupPath);
                    }
                    TryMoveFile(_download._path, _download._backupPath);
                }

                private void OnResponse(HttpWebResponse response) {
                    BackupExistingPage();
                    _fileStream = new FileStream(_download._path, FileMode.Create, FileAccess.Write, FileShare.Read);
                    if (response.ContentLength != -1) {
                        _totalFileSize = response.ContentLength;
                        _fileStream.SetLength(_totalFileSize.Value);
                    }
                    _createdFile = true;
                    _memoryStream = new MemoryStream();
                    _httpContentType = response.ContentType;
                    _lastModifiedTime = General.GetResponseLastModifiedTime(response);
                    _watcher.OnDownloadStart(new DownloadStartEventArgs(_downloadID, _download._url, _download._tryNumber, _totalFileSize));
                }

                private void OnDownloadChunk(byte[] data, int dataLength) {
                    _fileStream.Write(data, 0, dataLength);
                    _memoryStream.Write(data, 0, dataLength);
                    _downloadedFileSize += dataLength;
                    _watcher.OnDownloadProgress(new DownloadProgressEventArgs(_downloadID, _downloadedFileSize));
                }

                private void OnComplete() {
                    byte[] pageBytes = _memoryStream.ToArray();
                    if (IsSizeMismatch(_totalFileSize, _downloadedFileSize)) {
                        _fileStream.SetLength(_downloadedFileSize);
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
                    if (ex is HTTP304Exception) {
                        // Page not modified, skip
                        EndTryDownload(DownloadResult.Skipped);
                    }
                    else if (ex is HTTP404Exception) {
                        // Page not found, stop
                        _watcher.Stop(StopReason.PageNotFound);
                        EndTryDownload(DownloadResult.Skipped);
                    }
                    else if (IsFatalIOException(ex)) {
                        // Fatal IO error, stop
                        _watcher.Stop(StopReason.IOError);
                        EndTryDownload(DownloadResult.Skipped);
                    }
                    else {
                        // Other error, retry
                        _download.Retry();
                    }
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
            private byte[] _prevHash;
            private long? _prevDownloadedFileSize;

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
                _connectionGroupName = _connectionManager.ObtainConnectionGroupName();
            }

            public void TryDownload() {
                Attempt attempt = new Attempt(this);
                _tryNumber++;
                if (_watcher.IsStopping || _tryNumber > _maxDownloadTries) {
                    attempt.EndTryDownload(DownloadResult.RetryLater);
                    return;
                }
                attempt.Start();
            }

            private void Retry() {
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
                    _download._connectionManager.ReleaseConnectionGroupName(_download._connectionGroupName);
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
                    _fileStream = new FileStream(_download._path, FileMode.Create, FileAccess.Write, FileShare.Read);
                    if (response.ContentLength != -1) {
                        _totalFileSize = response.ContentLength;
                        _fileStream.SetLength(_totalFileSize.Value);
                    }
                    _createdFile = true;
                    if (_download._hashType != HashType.None) {
                        _hashStream = new HashGeneratorStream(_download._hashType);
                    }
                    _watcher.OnDownloadStart(new DownloadStartEventArgs(_downloadID, _download._url, _download._tryNumber, _totalFileSize));
                }

                private void OnDownloadChunk(byte[] data, int dataLength) {
                    _fileStream.Write(data, 0, dataLength);
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
                        _fileStream.SetLength(_downloadedFileSize);
                    }
                    bool incorrectHash = IsIncorrectHash(_download._hashType, hash, _download._correctHash, _download._prevHash);
                    bool incompleteDownload = IsIncompleteDownload(_totalFileSize, _downloadedFileSize, _download._prevDownloadedFileSize);
                    if (incorrectHash || incompleteDownload) {
                        // Corrupt download, retry
                        _download._prevHash = hash;
                        _download._prevDownloadedFileSize = _downloadedFileSize;
                        throw new Exception("Download is corrupt.");
                    }
                    Cleanup(true);
                    _watcher.OnDownloadEnd(new DownloadEndEventArgs(_downloadID, _downloadedFileSize, true));
                    EndTryDownload(DownloadResult.Completed);
                }

                private void OnException(Exception ex) {
                    Cleanup(false);
                    _watcher.OnDownloadEnd(new DownloadEndEventArgs(_downloadID, _downloadedFileSize, false));
                    if (ex is DirectoryNotFoundException || ex is UnauthorizedAccessException) {
                        // Fatal IO error, stop
                        _watcher.Stop(StopReason.IOError);
                        EndTryDownload(DownloadResult.Skipped);
                    }
                    else if (ex is HTTP404Exception || ex is IOException) {
                        // Fatal problem with this file, skip
                        EndTryDownload(DownloadResult.Skipped);
                    }
                    else {
                        // Other error, retry
                        _download.Retry();
                    }
                }
            }
        }
    }
}