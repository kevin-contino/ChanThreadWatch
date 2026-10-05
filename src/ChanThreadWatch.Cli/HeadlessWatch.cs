using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace JDP.Cli {
    // The main window's watching without the window: loads the thread list and the blacklist through WatchSession,
    // saves the thread list when it changed on the app's save timer (every minute), backs it up as the settings ask,
    // and on stop saves the settings, stops every watcher and saves the thread list before and after waiting for
    // them, as the window does when it closes. The window's UI thread is one owner thread here, which runs the
    // queued work in order: what the session posts (an auto-followed thread), the saves and the backups. Status
    // lines go to output.
    internal sealed class HeadlessWatch {
        // The app's save timer (tmrSaveThreadList); the tests make it shorter. Declared first: static fields are
        // set in order.
        internal static readonly TimeSpan DefaultSaveInterval = TimeSpan.FromMinutes(1);
        internal static TimeSpan SaveInterval { get; set; } = DefaultSaveInterval;
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

        // How long the stop waits in all for the watchers; then the thread list is saved without waiting further (a
        // watcher that hangs must not keep the final save from happening)
        internal static readonly TimeSpan StopWait = TimeSpan.FromSeconds(30);

        // Waits up to the time given for the watcher's check and reparse to finish; false if they did not. The tests
        // put another in its place.
        internal static Func<ThreadWatcher, TimeSpan, bool> WaitForWatcher { get; set; } = DefaultWaitForWatcher;

        // Test only: called on the calling thread once the thread list is loaded, once the watchers are told to stop,
        // and after each list of watchers that the stop waits for is taken
        internal static Action<HeadlessWatch> StartedForTesting { get; set; }
        internal static Action<HeadlessWatch> StoppingForTesting { get; set; }
        internal static Action<HeadlessWatch> WatchersListedForTesting { get; set; }

        internal static bool DefaultWaitForWatcher(ThreadWatcher watcher, TimeSpan wait) {
            int milliseconds = (int)wait.TotalMilliseconds;
            return watcher.WaitUntilStopped(milliseconds) && watcher.WaitReparse(milliseconds);
        }

        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _ownerThread;
        private readonly WatchSession _session;
        private readonly WatchStatusOutput _output;
        private volatile bool _isExiting;

        public HeadlessWatch(WatchStatusOutput output, string settingsFolder) {
            _output = output;
            _session = new WatchSession(Invoke, Post, settingsFolder);
            _session.ThreadWatcherCreated += Subscribe;
            _session.ThreadWatcherAdded += OnThreadAdded;
            _ownerThread = new Thread(RunOwnerThread) { Name = "ctw watch owner", IsBackground = true };
        }

        internal WatchSession Session {
            get { return _session; }
        }

        // Watches until stopToken is canceled. Returns false if the thread list could not be saved at the end.
        public bool Run(CancellationToken stopToken) {
            bool saved = false;
            _ownerThread.Start();
            try {
                Invoke(Load);
                StartedForTesting?.Invoke(this);
                WaitForStop(stopToken);
            }
            finally {
                saved = Exit();
            }
            return saved;
        }

        private void Load() {
            _session.LoadThreadList();
            _session.LoadBlacklist();
            _output.WriteStarted(_session.GetMonitoringInfo(), Settings.GetSettingsDirectory());
        }

        private void RunOwnerThread() {
            foreach (Action work in _queue.GetConsumingEnumerable()) {
                RunLogged(work);
            }
        }

        // A thread added while stopping (auto-follow) is stopped too. The window logs an exception of its UI thread
        // and goes on.
        private void RunLogged(Action work) {
            try {
                work();
                if (_isExiting) StopWatchers();
            }
            catch (Exception ex) {
                Logger.Log(ex.ToString());
            }
        }

        // Queues the work for the owner thread and returns without waiting, as the window's BeginInvoke. Nothing is
        // queued after the end (all watchers are stopped by then).
        internal void Post(Action work) {
            try {
                _queue.Add(work);
            }
            catch (InvalidOperationException) {
            }
        }

        // Runs the work on the owner thread and waits for it, as the window's Invoke; an exception is thrown here
        private void Invoke(Action work) {
            if (Thread.CurrentThread == _ownerThread) {
                work();
                return;
            }
            ExceptionDispatchInfo error = null;
            using (ManualResetEventSlim done = new ManualResetEventSlim(false)) {
                _queue.Add(() => {
                    try { work(); }
                    catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
                    finally { done.Set(); }
                });
                done.Wait();
            }
            error?.Throw();
        }

        private T Invoke<T>(Func<T> work) {
            T result = default(T);
            Invoke(() => { result = work(); });
            return result;
        }

        private void WaitForStop(CancellationToken stopToken) {
            Interval saves = new Interval(SaveInterval);
            Interval backups = new Interval(TimeSpan.FromMinutes(Math.Max(1, Settings.BackupEvery ?? 1)));
            while (!stopToken.WaitHandle.WaitOne(PollInterval)) {
                if (saves.IsDue()) Invoke(SaveIfChanged);
                if (backups.IsDue()) Invoke(BackUpThreadList);
            }
        }

        // A failed save stays pending, so the next interval (or the save on stop) tries again
        private void SaveIfChanged() {
            if (_session.SaveThreadListPending && _session.SaveThreadList()) _session.SaveThreadListPending = false;
        }

        // On the owner thread, so it never reads the thread list while a save replaces it
        private static void BackUpThreadList() {
            if (Settings.BackupThreadList == true) General.BackupThreadList(Settings.BackupCheckSize ?? false);
        }

        private bool Exit() {
            Invoke(() => {
                Settings.Save();
                _isExiting = true;
                StopWatchers();
                // Saved before waiting in addition to after, in case the wait is interrupted (a second Ctrl+C)
                _session.SaveThreadList();
            });
            StoppingForTesting?.Invoke(this);
            WaitUntilSettled();
            bool saved = Invoke(() => _session.SaveThreadList());
            _queue.CompleteAdding();
            _ownerThread.Join();
            _output.WriteStopped(saved);
            return saved;
        }

        private void StopWatchers() {
            foreach (ThreadWatcher watcher in _session.ThreadWatchers) {
                watcher.Stop(StopReason.Exiting);
            }
        }

        // Until every watcher has stopped, nothing is queued and no thread was added meanwhile (work queued by a
        // watcher before it stopped can add, and then stop, an auto-followed thread), or until StopWait is over
        private void WaitUntilSettled() {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (true) {
                List<ThreadWatcher> watchers = Invoke(() => {
                    StopWatchers();
                    return _session.ThreadWatchers;
                });
                WatchersListedForTesting?.Invoke(this);
                List<ThreadWatcher> notStopped = watchers.FindAll(watcher => !WaitForWatcher(watcher, GetRemainingWait(elapsed)));
                if (notStopped.Count != 0) {
                    ReportNotStopped(notStopped);
                    return;
                }
                if (Invoke(() => IsSettled(watchers.Count))) return;
            }
        }

        // On the owner thread: nothing is queued behind this, and no thread was added since the watchers were listed
        private bool IsSettled(int listedCount) {
            return _queue.Count == 0 && _session.ThreadWatchers.Count == listedCount;
        }

        private static TimeSpan GetRemainingWait(Stopwatch elapsed) {
            TimeSpan remaining = StopWait - elapsed.Elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        private void ReportNotStopped(List<ThreadWatcher> watchers) {
            foreach (ThreadWatcher watcher in watchers) {
                string text = ConsoleText.CleanUrl(watcher.PageURL) + ": did not stop within " + (int)StopWait.TotalSeconds + " seconds; the thread list is saved without waiting for it.";
                Logger.Log(text);
                _output.WriteLine(text);
            }
        }

        private void OnThreadAdded(ThreadWatcher watcher) {
            if (!_session.IsLoadingThreadsFromFile && !_isExiting) _output.WriteThreadAdded(watcher);
        }

        // The window's handlers, without the display: what marks the thread list as changed, and the status lines
        private void Subscribe(ThreadWatcher watcher) {
            watcher.DownloadStatus += ThreadWatcher_DownloadStatus;
            watcher.StopStatus += ThreadWatcher_StopStatus;
            watcher.WaitStatus += ThreadWatcher_WaitStatus;
            watcher.ThreadDownloadDirectoryRename += (sender, args) => MarkThreadListChanged();
        }

        private void ThreadWatcher_DownloadStatus(object sender, DownloadStatusEventArgs args) {
            WatcherExtraData extraData = (WatcherExtraData)((ThreadWatcher)sender).Tag;
            bool isInitialPageDownload = WatchSession.TrackPageDownload(extraData, args.DownloadType);
            bool isFirstImageUpdate = WatchSession.TrackImageDownload(extraData, args.DownloadType);
            if (isInitialPageDownload || isFirstImageUpdate) MarkThreadListChanged();
        }

        private void ThreadWatcher_StopStatus(object sender, StopStatusEventArgs args) {
            if (_isExiting || args.StopReason == StopReason.UserRequest || args.StopReason == StopReason.Exiting) return;
            if (!_session.IsLoadingThreadsFromFile) _output.WriteThreadStopped((ThreadWatcher)sender, args.StopReason);
            MarkThreadListChanged();
        }

        // A check has finished and the watcher waits for the next one
        private void ThreadWatcher_WaitStatus(object sender, EventArgs args) {
            if (!_isExiting) _output.WriteCheckProblems((ThreadWatcher)sender);
        }

        // Posted, as the window does, so a watcher's thread never waits for the owner thread
        private void MarkThreadListChanged() {
            if (!_isExiting) Post(() => _session.SaveThreadListPending = true);
        }

        // Due once each time the interval has passed
        private sealed class Interval {
            private readonly TimeSpan _length;
            private readonly Stopwatch _elapsed = Stopwatch.StartNew();

            public Interval(TimeSpan length) {
                _length = length;
            }

            public bool IsDue() {
                if (_elapsed.Elapsed < _length) return false;
                _elapsed.Restart();
                return true;
            }
        }
    }

    // The status lines of ctw watch, written from the watchers' threads one line at a time. Thread URLs are shown
    // without a login, and text from the web (errors) without control characters. Output that can't be written
    // (a closed pipe) is dropped, so it never fails a watcher's thread or skips the final save.
    internal sealed class WatchStatusOutput {
        private readonly TextWriter _output;

        public WatchStatusOutput(TextWriter output) {
            _output = TextWriter.Synchronized(output);
        }

        public void WriteLine(string line) {
            try {
                _output.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) {
            }
        }

        public void WriteStarted(MonitoringInfo info, string settingsFolder) {
            WriteLine("Watching " + info.TotalThreads + " thread" + (info.TotalThreads != 1 ? "s" : "") + " (" + info.RunningThreads + " running), settings folder: " +
                ConsoleText.Clean(settingsFolder) + ". Press Ctrl+C to stop.");
        }

        public void WriteThreadAdded(ThreadWatcher watcher) {
            WriteLine("Added " + ConsoleText.CleanUrl(watcher.PageURL));
        }

        public void WriteThreadStopped(ThreadWatcher watcher, StopReason reason) {
            WriteLine(ConsoleText.CleanUrl(watcher.PageURL) + ": " + ConsoleText.Clean(WatcherStatusText.FormatStopStatus(reason, watcher.StopError, watcher.FailedFileCount)));
        }

        // After a check with an error or with files that failed after all their tries, the app's status text, e.g.
        // "2 files failed, waiting 300 seconds" (each file is in the log)
        public void WriteCheckProblems(ThreadWatcher watcher) {
            string checkError = watcher.CheckError;
            int failedFileCount = watcher.FailedFileCount;
            if (checkError == null && failedFileCount == 0) return;
            int remainingSeconds = (watcher.MillisecondsUntilNextCheck + 999) / 1000;
            WriteLine(ConsoleText.CleanUrl(watcher.PageURL) + ": " + ConsoleText.Clean(WatcherStatusText.FormatWaitStatus(remainingSeconds, checkError, failedFileCount)));
        }

        public void WriteStopped(bool saved) {
            WriteLine(saved ? "Stopped. The thread list was saved." : "Stopped.");
        }
    }
}
