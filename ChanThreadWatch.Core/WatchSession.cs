using System;
using System.Collections.Generic;
using System.IO;

namespace JDP {
    // The watched threads, the thread list file and the blacklist, without any user interface.
    // Work that has to run on the owner's thread (the UI thread for the main form) is passed to
    // runOnOwnerThread, which must run it synchronously. AddThread must be called on that thread.
    // Threads that a watcher auto-follows are added through postToOwnerThread, which must queue the
    // work for the owner's thread and return without waiting, so a watcher's thread never blocks
    // on the owner's thread (which waits for the watchers when the program exits).
    // The events let the owner show the threads; they are raised on the owner's thread.
    internal class WatchSession {
        private readonly Action<Action> _runOnOwnerThread;
        private readonly Action<Action> _postToOwnerThread;
        // Null to follow Settings.GetSettingsDirectory(), which can change while running
        private readonly string _settingsDirectory;
        private readonly ThreadListStore _threadListStore = new ThreadListStore();
        // Locked on itself: the load reads it on a worker thread while the owner's thread adds threads
        private readonly Dictionary<string, ThreadWatcher> _watchers = new Dictionary<string, ThreadWatcher>();
        // Not locked: the load fills it on a worker thread while auto-follow can read it (known race, unchanged by the move)
        private readonly HashSet<string> _blacklist = new HashSet<string>();
        private bool _isLoadingThreadsFromFile;

        internal WatchSession(Action<Action> runOnOwnerThread, Action<Action> postToOwnerThread, string settingsDirectory = null) {
            if (runOnOwnerThread == null) throw new ArgumentNullException("runOnOwnerThread");
            if (postToOwnerThread == null) throw new ArgumentNullException("postToOwnerThread");
            _runOnOwnerThread = runOnOwnerThread;
            _postToOwnerThread = postToOwnerThread;
            _settingsDirectory = settingsDirectory;
        }

        // A new watcher was created by AddThread, before its settings are applied and it is registered
        internal event Action<ThreadWatcher> ThreadWatcherCreated;

        // AddThread registered the watcher, before starting or stopping it
        internal event Action<ThreadWatcher> ThreadWatcherAdded;

        // The loaded threads are about to be added
        internal event Action ThreadListLoadStarting;

        // A loaded thread was linked to its parent thread
        internal event Action<ThreadWatcher> AddedFromChanged;

        // RemoveThreads is removing the watcher: its pre-remove action has run, and it is
        // unregistered right after
        internal event Action<ThreadWatcher> ThreadWatcherRemoved;

        internal bool IsLoadingThreadsFromFile {
            get { return _isLoadingThreadsFromFile; }
        }

        // Set when the thread list has changed and should be saved
        internal bool SaveThreadListPending { get; set; }

        // A copy, so it can be enumerated while threads are added or removed
        internal List<ThreadWatcher> ThreadWatchers {
            get { lock (_watchers) { return new List<ThreadWatcher>(_watchers.Values); } }
        }

        private string SettingsDirectory {
            get { return _settingsDirectory ?? Settings.GetSettingsDirectory(); }
        }

        internal bool TryGetThreadWatcher(string pageID, out ThreadWatcher watcher) {
            lock (_watchers) {
                return _watchers.TryGetValue(pageID, out watcher);
            }
        }

        internal void UnregisterThreadWatcher(ThreadWatcher watcher) {
            lock (_watchers) {
                _watchers.Remove(watcher.PageID);
            }
        }

        internal static void EnsureDownloadFolderExists() {
            if ((Settings.DownloadFolder == null) || !Directory.Exists(Settings.AbsoluteDownloadDirectory)) {
                Settings.DownloadFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Watched Threads");
                Settings.DownloadFolderIsRelative = false;
            }
        }

        // Returns true if this is the first page download for the watcher.
        internal static bool TrackPageDownload(WatcherExtraData extraData, DownloadType downloadType) {
            if (downloadType != DownloadType.Page) return false;
            bool isInitialPageDownload = false;
            if (!extraData.HasDownloadedPage) {
                extraData.HasDownloadedPage = true;
                isInitialPageDownload = true;
            }
            extraData.PreviousDownloadWasPage = true;
            return isInitialPageDownload;
        }

        // Returns true if this is the first image download since the last page download.
        internal static bool TrackImageDownload(WatcherExtraData extraData, DownloadType downloadType) {
            if (downloadType != DownloadType.Image || !extraData.PreviousDownloadWasPage) return false;
            extraData.LastImageOn = DateTime.Now;
            extraData.PreviousDownloadWasPage = false;
            return true;
        }

        internal bool AddThread(ThreadInfo thread) {
            return AddThread(thread, false);
        }

        // Threads from the file start after the load links them to their parents; any other thread starts now,
        // including one auto-followed while the load is still running.
        private bool AddThread(ThreadInfo thread, bool isFromFile) {
            ThreadWatcher watcher = null;
            ThreadWatcher parentThread = null;
            SiteHelper siteHelper = SiteHelpers.GetInstance((new Uri(thread.URL)).Host);
            siteHelper.SetURL(thread.URL);
            string pageID = siteHelper.GetPageID();
            if (IsBlacklisted(pageID)) return false;

            if (TryGetThreadWatcher(pageID, out watcher)) {
                if (watcher.IsRunning) return false;
            }

            if (watcher == null) {
                watcher = CreateThreadWatcher(thread, out parentThread);
                watcher.AddThread += ThreadWatcher_AddThread;
                OnThreadWatcherCreated(watcher);
            }

            ApplyThreadInfo(watcher, thread);
            AttachExtraData(watcher, thread);
            RegisterThreadWatcher(watcher, parentThread);
            OnThreadWatcherAdded(watcher);

            StartOrStopAddedThread(watcher, thread, isFromFile);
            return true;
        }

        private void OnThreadWatcherCreated(ThreadWatcher watcher) {
            ThreadWatcherCreated?.Invoke(watcher);
        }

        private void OnThreadWatcherAdded(ThreadWatcher watcher) {
            ThreadWatcherAdded?.Invoke(watcher);
        }

        private ThreadWatcher CreateThreadWatcher(ThreadInfo thread, out ThreadWatcher parentThread) {
            parentThread = null;
            ThreadWatcher watcher = new ThreadWatcher(thread.URL);
            watcher.ThreadDownloadDirectory = thread.SaveDir;
            watcher.Description = thread.Description;
            if (_isLoadingThreadsFromFile) watcher.DoNotRename = true;
            watcher.Category = thread.Category;
            watcher.DoNotRename = false;
            if (thread.ExtraData != null && !String.IsNullOrEmpty(thread.ExtraData.AddedFrom)) {
                TryGetThreadWatcher(thread.ExtraData.AddedFrom, out parentThread);
                watcher.ParentThread = parentThread;
            }
            return watcher;
        }

        private static void ApplyThreadInfo(ThreadWatcher watcher, ThreadInfo thread) {
            watcher.PageAuth = thread.PageAuth;
            watcher.ImageAuth = thread.ImageAuth;
            watcher.CheckIntervalSeconds = thread.CheckIntervalSeconds;
            watcher.OneTimeDownload = thread.OneTimeDownload;
            watcher.AutoFollow = thread.AutoFollow;
        }

        private static void AttachExtraData(ThreadWatcher watcher, ThreadInfo thread) {
            if (thread.ExtraData == null) {
                thread.ExtraData = watcher.Tag as WatcherExtraData ?? new WatcherExtraData { AddedOn = DateTime.Now };
            }
            watcher.Tag = thread.ExtraData;
        }

        internal void RegisterThreadWatcher(ThreadWatcher watcher, ThreadWatcher parentThread) {
            if (parentThread != null) parentThread.AddChildThread(watcher);
            lock (_watchers) {
                if (!_watchers.ContainsKey(watcher.PageID)) {
                    _watchers.Add(watcher.PageID, watcher);
                }
                else {
                    _watchers[watcher.PageID] = watcher;
                }
            }
        }

        private static void StartOrStopAddedThread(ThreadWatcher watcher, ThreadInfo thread, bool isFromFile) {
            if (thread.StopReason == null && !isFromFile) {
                watcher.Start();
            }
            else if (thread.StopReason != null) {
                watcher.Stop(thread.StopReason.Value);
            }
        }

        // Called on the watcher's thread
        private void ThreadWatcher_AddThread(object sender, AddThreadEventArgs args) {
            ThreadWatcher watcher = (ThreadWatcher)sender;
            ThreadWatcher rootThread = watcher.RootThread;
            _postToOwnerThread(() => {
                try {
                    AddFollowedThread(watcher, args.PageURL);
                }
                finally {
                    // The watcher reserved room for this thread under its root before raising
                    // the event; the thread is now either added (and counted) or rejected
                    rootThread.ReleaseDescendantSlot();
                }
            });
        }

        private void AddFollowedThread(ThreadWatcher watcher, string pageURL) {
            ThreadInfo thread = watcher.CreateChildThreadInfo(pageURL, DateTime.Now, Settings.RecursiveAutoFollow != false);
            if (IsThreadWatched(thread.URL)) return;
            if (AddThread(thread)) {
                SaveThreadListPending = true;
            }
        }

        // Removes the given threads that this session watches and that are stopped and not reparsing, in the given order.
        // preRemoveAction runs on each one first; a failure is logged and the thread is removed anyway.
        internal void RemoveThreads(IEnumerable<ThreadWatcher> threads, Action<ThreadWatcher> preRemoveAction = null) {
            foreach (ThreadWatcher watcher in threads) {
                if (IsRegistered(watcher) && ShouldRemoveThread(watcher)) {
                    RunPreRemoveAction(preRemoveAction, watcher);
                    ThreadWatcherRemoved?.Invoke(watcher);
                    UnregisterThreadWatcher(watcher);
                }
            }
            SaveThreadListPending = true;
        }

        private bool IsRegistered(ThreadWatcher watcher) {
            ThreadWatcher registered;
            return TryGetThreadWatcher(watcher.PageID, out registered) && registered == watcher;
        }

        private static bool ShouldRemoveThread(ThreadWatcher watcher) {
            return !watcher.IsRunning && !watcher.IsReparsing;
        }

        private static void RunPreRemoveAction(Action<ThreadWatcher> preRemoveAction, ThreadWatcher watcher) {
            if (preRemoveAction == null) return;
            try { preRemoveAction(watcher); }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
        }

        // Removes the given threads like RemoveThreads, first moving their folders to the
        // completed folder if the settings ask for it
        internal void RemoveCompletedThreads(IEnumerable<ThreadWatcher> threads) {
            if (Settings.MoveToCompletedFolder != true) {
                RemoveThreads(threads);
            }
            else {
                EnsureCompletedFolderExists();
                RemoveThreads(threads, MoveThreadToCompletedFolder);
            }
        }

        private static void EnsureCompletedFolderExists() {
            if (!Directory.Exists(Settings.AbsoluteCompletedDirectory)) {
                Settings.CompletedFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Completed Threads");
                Settings.CompletedFolderIsRelative = false;
            }
        }

        internal static void MoveThreadToCompletedFolder(ThreadWatcher watcher) {
            string destDir = Path.Combine(Settings.AbsoluteCompletedDirectory, GetCompletedFolderName(watcher));
            if (Directory.Exists(watcher.ThreadDownloadDirectory)) {
                if (Directory.Exists(destDir)) {
                    Directory.Delete(destDir);
                }
                if (watcher.Category.Length != 0) {
                    Directory.CreateDirectory(General.RemoveLastDirectory(destDir));
                }
                Directory.Move(watcher.ThreadDownloadDirectory, destDir);
            }
            DeleteCategoryFolderIfEmpty(watcher);
        }

        // A thread folder on another root (another drive or network share) has no relative path, and
        // combining the absolute path would make the destination the thread folder itself
        private static string GetCompletedFolderName(ThreadWatcher watcher) {
            string relativeDir = General.GetRelativeDirectoryPath(watcher.ThreadDownloadDirectory, watcher.MainDownloadDirectory);
            return Path.IsPathRooted(relativeDir) ? General.GetLastDirectory(watcher.ThreadDownloadDirectory) : relativeDir;
        }

        private static void DeleteCategoryFolderIfEmpty(ThreadWatcher watcher) {
            string categoryPath = General.RemoveLastDirectory(watcher.ThreadDownloadDirectory);
            if (categoryPath != watcher.MainDownloadDirectory && Directory.GetFiles(categoryPath).Length == 0 && Directory.GetDirectories(categoryPath).Length == 0) {
                Directory.Delete(categoryPath);
            }
        }

        internal static void DeleteThreadFolder(ThreadWatcher watcher) {
            if (Directory.Exists(watcher.ThreadDownloadDirectory)) Directory.Delete(watcher.ThreadDownloadDirectory, true);
            DeleteCategoryFolderIfEmpty(watcher);
        }

        private string ThreadListPath {
            get { return Path.Combine(SettingsDirectory, Settings.ThreadsFileName); }
        }

        internal void SaveThreadList() {
            if (_isLoadingThreadsFromFile) return;
            try {
                // The thread list store refuses to save until the load has finished, and
                // writes atomically so a failure can't leave a partially written file.
                _threadListStore.Save(ThreadListPath, GetSavedThreadInfos());
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
        }

        private List<ThreadInfo> GetSavedThreadInfos() {
            List<ThreadInfo> threads = new List<ThreadInfo>();
            foreach (ThreadWatcher watcher in ThreadWatchers) {
                threads.Add(GetSavedThreadInfo(watcher));
            }
            return threads;
        }

        private static ThreadInfo GetSavedThreadInfo(ThreadWatcher watcher) {
            return new ThreadInfo {
                URL = watcher.PageURL,
                PageAuth = watcher.PageAuth,
                ImageAuth = watcher.ImageAuth,
                CheckIntervalSeconds = watcher.CheckIntervalSeconds,
                OneTimeDownload = watcher.OneTimeDownload,
                SaveDir = GetSaveDirLine(watcher),
                StopReason = GetSavedStopReason(watcher),
                Description = watcher.Description,
                ExtraData = (WatcherExtraData)watcher.Tag,
                Category = watcher.Category,
                AutoFollow = watcher.AutoFollow
            };
        }

        private static string GetSaveDirLine(ThreadWatcher watcher) {
            return watcher.ThreadDownloadDirectory != null ? General.GetRelativeDirectoryPath(watcher.ThreadDownloadDirectory, watcher.MainDownloadDirectory) : String.Empty;
        }

        private static StopReason? GetSavedStopReason(ThreadWatcher watcher) {
            return (watcher.IsStopping && watcher.StopReason != StopReason.Exiting) ? watcher.StopReason : (StopReason?)null;
        }

        // Called on a worker thread
        internal void LoadThreadList() {
            bool loadedFully = false;
            try {
                loadedFully = LoadThreadListFile();
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
            _isLoadingThreadsFromFile = false;
            // If anything failed, the original file is copied aside before saving is allowed.
            _threadListStore.EndLoad(ThreadListPath, loadedFully);
        }

        // Returns false if any part of the file could not be loaded.
        private bool LoadThreadListFile() {
            ThreadListData data = _threadListStore.Read(ThreadListPath);
            if (data == null) return true;
            bool allThreadsAdded = data.Threads.Count == 0 || AddLoadedThreads(data.Threads);
            return allThreadsAdded && data.TrailingLineCount == 0;
        }

        private bool AddLoadedThreads(List<ThreadInfo> threads) {
            _isLoadingThreadsFromFile = true;
            _runOnOwnerThread(() => {
                ThreadListLoadStarting?.Invoke();
            });
            int failedCount = 0;
            foreach (ThreadInfo thread in threads) {
                if (!TryAddLoadedThread(thread)) failedCount++;
            }
            LinkLoadedThreadsToParents();
            MigrateChildThreadsToNewFormat();
            _isLoadingThreadsFromFile = false;
            return failedCount == 0;
        }

        private bool TryAddLoadedThread(ThreadInfo thread) {
            try {
                thread.SaveDir = thread.SaveDir.Length != 0 ? General.GetAbsoluteDirectoryPath(thread.SaveDir, Settings.AbsoluteDownloadDirectory) : null;
                _runOnOwnerThread(() => {
                    // A second entry for the same thread fails the load, so the first one is kept and the file is copied aside
                    if (IsThreadWatched(thread.URL)) throw new InvalidOperationException("Duplicate entry in the thread list");
                    AddThread(thread, true);
                });
                return true;
            }
            catch (Exception ex) {
                Logger.Log("Unable to load thread " + thread.URL + Environment.NewLine + ex);
                return false;
            }
        }

        internal bool IsThreadWatched(string url) {
            SiteHelper siteHelper = SiteHelpers.GetInstance((new Uri(url)).Host);
            siteHelper.SetURL(url);
            ThreadWatcher watcher;
            return TryGetThreadWatcher(siteHelper.GetPageID(), out watcher);
        }

        private void LinkLoadedThreadsToParents() {
            foreach (ThreadWatcher threadWatcher in ThreadWatchers) {
                LinkToParentThread(threadWatcher);
                ThreadWatcher watcher = threadWatcher;
                _runOnOwnerThread(() => {
                    AddedFromChanged?.Invoke(watcher);
                });
                if (Settings.ChildThreadsAreNewFormat == true && IsRestartableAfterLoad(threadWatcher)) {
                    threadWatcher.Start();
                }
            }
        }

        internal void LinkToParentThread(ThreadWatcher threadWatcher) {
            ThreadWatcher parentThread = FindLoadedParentThread(threadWatcher);
            threadWatcher.ParentThread = parentThread;
            if (parentThread != null) {
                parentThread.AddChildThread(threadWatcher);
            }
        }

        // Returns null if the thread has no (known) parent, or if linking it to its parent
        // would make a thread its own ancestor.
        private ThreadWatcher FindLoadedParentThread(ThreadWatcher threadWatcher) {
            string addedFrom = ((WatcherExtraData)threadWatcher.Tag).AddedFrom;
            ThreadWatcher parentThread;
            if (String.IsNullOrEmpty(addedFrom) || !TryGetThreadWatcher(addedFrom, out parentThread)) return null;
            return IsSelfOrAncestor(threadWatcher, parentThread) ? null : parentThread;
        }

        private bool IsSelfOrAncestor(ThreadWatcher threadWatcher, ThreadWatcher descendantThread) {
            // The step limit stops the walk even if the existing parent links already form a cycle.
            int stepsLeft;
            lock (_watchers) {
                stepsLeft = _watchers.Count + 1;
            }
            for (ThreadWatcher thread = descendantThread; thread != null && stepsLeft-- > 0; thread = thread.ParentThread) {
                if (thread == threadWatcher) return true;
            }
            return false;
        }

        private static bool IsRestartableAfterLoad(ThreadWatcher threadWatcher) {
            return threadWatcher.StopReason != StopReason.PageNotFound && threadWatcher.StopReason != StopReason.UserRequest;
        }

        private void MigrateChildThreadsToNewFormat() {
            if (Settings.ChildThreadsAreNewFormat == true) return;
            foreach (ThreadWatcher threadWatcher in ThreadWatchers) {
                if (threadWatcher.ChildThreads.Count == 0 || threadWatcher.ParentThread != null) continue;
                foreach (ThreadWatcher descendantThread in threadWatcher.DescendantThreads.Values) {
                    MoveDescendantThreadDirectory(threadWatcher, descendantThread);
                }
            }
            Settings.ChildThreadsAreNewFormat = true;
            Settings.Save();

            foreach (ThreadWatcher threadWatcher in ThreadWatchers) {
                if (IsRestartableAfterLoad(threadWatcher)) threadWatcher.Start();
            }
        }

        internal static void MoveDescendantThreadDirectory(ThreadWatcher threadWatcher, ThreadWatcher descendantThread) {
            descendantThread.DoNotRename = true;
            try {
                TryMoveDescendantThreadDirectory(threadWatcher, descendantThread);
            }
            finally {
                descendantThread.DoNotRename = false;
            }
        }

        private static void TryMoveDescendantThreadDirectory(ThreadWatcher threadWatcher, ThreadWatcher descendantThread) {
            string sourceDir = descendantThread.ThreadDownloadDirectory;
            if (!CanMoveDescendantThreadDirectory(threadWatcher, sourceDir)) return;
            string destDir = GetDescendantThreadDestDir(threadWatcher, descendantThread, sourceDir);
            if (String.Equals(destDir, sourceDir, StringComparison.Ordinal)) return;
            try {
                MoveThreadDirectory(sourceDir, destDir);
                descendantThread.ThreadDownloadDirectory = destDir;
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
        }

        private static bool CanMoveDescendantThreadDirectory(ThreadWatcher threadWatcher, string sourceDir) {
            return !String.IsNullOrEmpty(sourceDir) && !String.IsNullOrEmpty(threadWatcher.ThreadDownloadDirectory) && Directory.Exists(sourceDir);
        }

        internal static string GetDescendantThreadDestDir(ThreadWatcher threadWatcher, ThreadWatcher descendantThread, string sourceDir) {
            // A folder directly inside the download folder is already in the new layout.
            if (General.RemoveLastDirectory(sourceDir) == descendantThread.MainDownloadDirectory) {
                return sourceDir;
            }
            return Path.Combine(General.RemoveLastDirectory(threadWatcher.ThreadDownloadDirectory),
                General.GetRelativeDirectoryPath(descendantThread.ThreadDownloadDirectory, threadWatcher.ThreadDownloadDirectory));
        }

        private static void MoveThreadDirectory(string sourceDir, string destDir) {
            if (String.Equals(destDir, sourceDir, StringComparison.OrdinalIgnoreCase)) {
                Directory.Move(sourceDir, destDir + " Temp");
                sourceDir = destDir + " Temp";
            }
            if (!Directory.Exists(General.RemoveLastDirectory(destDir))) Directory.CreateDirectory(General.RemoveLastDirectory(destDir));
            Directory.Move(sourceDir, destDir);
        }

        private string BlacklistPath {
            get { return Path.Combine(SettingsDirectory, Settings.BlacklistFileName); }
        }

        // Called on a worker thread
        internal void LoadBlacklist() {
            try {
                string path = BlacklistPath;
                if (!File.Exists(path)) return;
                string[] lines = File.ReadAllLines(path);
                if (lines.Length < 1) return;
                AddBlacklistRules(lines);
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
        }

        private void AddBlacklistRules(string[] lines) {
            for (int i = 0; i < lines.Length; i++) {
                string rule = lines[i];
                if (rule.Split('/').Length == 3) {
                    _blacklist.Add(rule);
                }
            }
        }

        // Writes the blacklist file with the threads added; they are only blacklisted if it was written
        internal void AddToBlacklist(IEnumerable<ThreadWatcher> watchers) {
            List<string> lines = new List<string>();
            foreach (string rule in _blacklist) {
                lines.Add(rule);
            }
            HashSet<string> blacklist = new HashSet<string>();
            foreach (ThreadWatcher watcher in watchers) {
                if (!_blacklist.Contains(watcher.PageID) && blacklist.Add(watcher.PageID)) {
                    lines.Add(watcher.PageID);
                }
            }
            try {
                string path = BlacklistPath;
                File.WriteAllLines(path, lines.ToArray());
                foreach (string pageID in blacklist) {
                    _blacklist.Add(pageID);
                }
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
        }

        internal bool IsBlacklisted(string pageID) {
            if (_blacklist.Contains(pageID)) return true;
            if (Settings.BlacklistWildcards != true) return false;
            string[] pageIDSplit = pageID.Split('/');
            if (pageIDSplit.Length != 3) return false;
            foreach (string rule in _blacklist) {
                if (MatchesWildcardRule(rule, pageIDSplit)) return true;
            }
            return false;
        }

        private static bool MatchesWildcardRule(string rule, string[] pageIDSplit) {
            string[] ruleSplit = rule.Split('/');
            if (ruleSplit.Length != 3) return false;
            for (int i = 0; i < 3; i++) {
                if (!MatchesWildcardRulePart(ruleSplit[i], pageIDSplit[i])) return false;
            }
            return true;
        }

        private static bool MatchesWildcardRulePart(string rulePart, string pageIDPart) {
            return rulePart == "*" || rulePart == pageIDPart;
        }

        internal MonitoringInfo GetMonitoringInfo() {
            int running = 0;
            int dead = 0;
            int stopped = 0;
            List<ThreadWatcher> watchers = ThreadWatchers;
            foreach (ThreadWatcher watcher in watchers) {
                if (watcher.IsRunning || watcher.IsWaiting) {
                    running++;
                }
                else if (watcher.StopReason == StopReason.PageNotFound) {
                    dead++;
                }
                else {
                    stopped++;
                }
            }
            return new MonitoringInfo {
                TotalThreads = watchers.Count,
                RunningThreads = running,
                DeadThreads = dead,
                StoppedThreads = stopped
            };
        }
    }
}
