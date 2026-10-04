using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;

namespace JDP {
    public class ReplaceInfo {
        public int Offset { get; set; }
        public int Length { get; set; }
        public string Value { get; set; }
        public ReplaceType Type { get; set; }
        public string Tag { get; set; }
    }

    public class PageInfo {
        public string URL { get; set; }
        public DateTime? CacheTime { get; set; }
        public bool IsFresh { get; set; }
        public string Path { get; set; }
        public List<ReplaceInfo> ReplaceList { get; set; }
    }

    public class ImageInfo {
        public string URL { get; set; }
        public string Referer { get; set; }
        public string OriginalFileName { get; set; }
        public HashType HashType { get; set; }
        public byte[] Hash { get; set; }
        public string Poster { get; set; }

        public string FileName {
            get { return General.CleanFileName(General.URLFileName(URL)); }
        }
    }

    public class DownloadInfo {
        public string Folder { get; set; }
        public string FileName { get; set; }
        public bool Skipped { get; set; }

        public string Path {
            get { return System.IO.Path.Combine(Folder ?? String.Empty, FileName); }
        }
    }

    public class ThumbnailInfo {
        public string URL { get; set; }
        public string Referer { get; set; }

        public string FileName {
            get { return General.CleanFileName(General.URLFileName(URL)); }
        }
    }

    public class WatcherExtraData {
        public DateTime AddedOn { get; set; }
        public DateTime? LastImageOn { get; set; }
        public bool HasDownloadedPage { get; set; }
        public bool PreviousDownloadWasPage { get; set; }
        public string AddedFrom { get; set; }
        // Saved logins that couldn't be decrypted, written back unchanged until a new login is set
        public string UndecryptablePageAuth { get; set; }
        public string UndecryptableImageAuth { get; set; }
    }

    public class ThreadInfo {
        public string URL { get; set; }
        public string PageAuth { get; set; }
        public string ImageAuth { get; set; }
        public int CheckIntervalSeconds { get; set; }
        public bool OneTimeDownload { get; set; }
        public string SaveDir { get; set; }
        public string Description { get; set; }
        public StopReason? StopReason { get; set; }
        public WatcherExtraData ExtraData { get; set; }
        public string Category { get; set; }
        public bool AutoFollow { get; set; }
    }

    public class MonitoringInfo {
        public int TotalThreads { get; set; }
        public int RunningThreads { get; set; }
        public int DeadThreads { get; set; }
        public int StoppedThreads { get; set; }
    }

    public class HTTP404Exception : Exception { }

    public class HTTP304Exception : Exception { }

    // A request was not sent because requests to Host are paused by a rate limit
    public class RateLimitException : Exception {
        public RateLimitException(string host)
            : this(host, "Requests to " + host + " are paused by a rate limit.", null) { }

        protected RateLimitException(string host, string message, Exception innerException)
            : base(message, innerException)
        {
            Host = host;
        }

        public string Host { get; private set; }
    }

    // HTTP 429, or 503 with Retry-After: Host asks the client to wait before sending more
    // requests. RetryAfter is the wait the server gave, or null if it gave none (or an invalid one).
    public class HTTPRateLimitedException : RateLimitException {
        public HTTPRateLimitedException(string host, TimeSpan? retryAfter, Exception innerException)
            : base(host, "Rate limited by " + host + ".", innerException)
        {
            RetryAfter = retryAfter;
        }

        public TimeSpan? RetryAfter { get; private set; }
    }

    public class PageTooLargeException : Exception {
        public PageTooLargeException(int maxBytes)
            : base("The page is larger than the maximum of " + maxBytes + " bytes.") { }
    }

    public static class TickCount {
        private static object _sync = new object();
        private static int _lastTickCount;
        private static long _correction;

        public static long Now {
            get {
                lock (_sync) {
                    int tickCount = Environment.TickCount;
                    if ((tickCount < 0) && (_lastTickCount >= 0)) {
                        _correction += 0x100000000L;
                    }
                    _lastTickCount = tickCount;
                    return tickCount + _correction;
                }
            }
        }
    }

    // One per host, shared by every watcher: limits the connections to the host, spaces out the
    // requests sent to it, and pauses all requests to it while it rate limits this client (a
    // limit applies to the client as a whole)
    public class ConnectionManager {
        // Downloads from one host that may be in progress at the same time, across all watchers
        internal const int DefaultMaxConnectionsPerHost = 1;
        // Shortest time between the starts of two requests to one host, retries included
        internal const int DefaultMinRequestStartIntervalMS = 1000;

        // Settable so tests can use other limits. Production code never changes them. A change of
        // MaxConnectionsPerHost only applies to hosts that no download has used yet.
        internal static int MaxConnectionsPerHost { get; set; } = DefaultMaxConnectionsPerHost;
        internal static int MinRequestStartIntervalMS { get; set; } = DefaultMinRequestStartIntervalMS;

        // How often a download waiting for a connection checks whether it should give up
        private const int SlotWaitPollMS = 200;

        // Starts the requests that have to wait for MinRequestStartIntervalMS, so no thread is
        // blocked while they wait
        private static readonly WorkScheduler _requestStartScheduler = new WorkScheduler();
        private const string _requestStartThreadGroup = "ConnectionManager request starts";

        // A rate limit pause lasts at least MinRateLimitPauseMS and at most MaxRateLimitPauseMS,
        // whatever the server asks. When the server gives no usable wait time, the first pause
        // lasts UnspecifiedRateLimitPauseMS and each later one twice as long as the one before,
        // until a download from the host succeeds.
        internal const int DefaultMinRateLimitPauseMS = 5 * 1000;
        internal const int DefaultMaxRateLimitPauseMS = 2 * 60 * 60 * 1000;
        internal const int DefaultUnspecifiedRateLimitPauseMS = 60 * 1000;
        private const int _maxRateLimitBackoffDoublings = 16;

        // Settable so tests can use short pauses. Production code never changes them.
        internal static int MinRateLimitPauseMS { get; set; } = DefaultMinRateLimitPauseMS;
        internal static int MaxRateLimitPauseMS { get; set; } = DefaultMaxRateLimitPauseMS;
        internal static int UnspecifiedRateLimitPauseMS { get; set; } = DefaultUnspecifiedRateLimitPauseMS;

        private static Dictionary<string, ConnectionManager> _connectionManagers = new Dictionary<string, ConnectionManager>(StringComparer.OrdinalIgnoreCase);

        private FIFOSemaphore _semaphore = new FIFOSemaphore(MaxConnectionsPerHost, MaxConnectionsPerHost);
        private Stack<string> _groupNames = new Stack<string>();
        private readonly string _host;
        private readonly object _pauseSync = new object();
        private long _pausedUntilTicks;
        private int _rateLimitBackoffLevel;
        private readonly object _requestStartSync = new object();
        private long _lastRequestStartTicks = Int64.MinValue / 2;

        private ConnectionManager(string host) {
            _host = host;
        }

        public static ConnectionManager GetInstance(string url) {
            return GetInstanceForHost((new Uri(url)).Host);
        }

        public static ConnectionManager GetInstanceForHost(string host) {
            ConnectionManager manager;
            lock (_connectionManagers) {
                if (!_connectionManagers.TryGetValue(host, out manager)) {
                    manager = new ConnectionManager(host);
                    _connectionManagers[host] = manager;
                }
            }
            return manager;
        }

        public string Host {
            get { return _host; }
        }

        // Whether requests to the host are paused because it rate limited this client
        public bool IsPaused {
            get { lock (_pauseSync) { return TickCount.Now < _pausedUntilTicks; } }
        }

        // The TickCount time at which the last pause ends (or ended)
        public long PausedUntilTicks {
            get { lock (_pauseSync) { return _pausedUntilTicks; } }
        }

        // Pauses all requests to the host after it answered with a rate limit, and returns how
        // many milliseconds the pause lasts from now. A pause is only ever extended, never cut
        // short, by a later answer.
        public int Pause(TimeSpan? retryAfter) {
            lock (_pauseSync) {
                long now = TickCount.Now;
                int pauseMS = ChoosePauseMS(retryAfter, now < _pausedUntilTicks);
                _pausedUntilTicks = Math.Max(_pausedUntilTicks, now + pauseMS);
                return (int)(_pausedUntilTicks - now);
            }
        }

        // Must be called while holding _pauseSync. An answer to a request that was sent before
        // the pause began does not raise the backoff again.
        private int ChoosePauseMS(TimeSpan? retryAfter, bool alreadyPaused) {
            if (retryAfter != null) return ClampPauseMS(retryAfter.Value.TotalMilliseconds);
            if (!alreadyPaused && _rateLimitBackoffLevel < _maxRateLimitBackoffDoublings) _rateLimitBackoffLevel++;
            return ClampPauseMS(UnspecifiedRateLimitPauseMS * Math.Pow(2, Math.Max(_rateLimitBackoffLevel - 1, 0)));
        }

        private static int ClampPauseMS(double pauseMS) {
            return (int)Math.Max(MinRateLimitPauseMS, Math.Min(MaxRateLimitPauseMS, pauseMS));
        }

        // Pauses, then logs one line with the host and the length of the pause (never a URL)
        public void PauseAndLog(TimeSpan? retryAfter) {
            int pauseMS = Pause(retryAfter);
            General.LogQuietly(String.Format("Rate limited by {0}, pausing requests to it for {1} seconds{2}.",
                _host, (pauseMS + 999) / 1000, retryAfter == null ? " (no Retry-After given)" : String.Empty));
        }

        // Throws RateLimitException, without sending anything, while requests to the host are paused
        public void ThrowIfPaused() {
            if (IsPaused) throw new RateLimitException(_host);
        }

        // The host answered a file request normally, so the next pause without a wait time starts over
        public void ResetRateLimitBackoff() {
            lock (_pauseSync) {
                _rateLimitBackoffLevel = 0;
            }
        }

        // Forgets every host, with its pause, backoff, reserved request starts and connection
        // slots, so that one test's rate limit, request interval or leaked slot does not affect the next
        internal static void ResetForTesting() {
            lock (_connectionManagers) {
                _connectionManagers.Clear();
            }
        }

        // Runs start (which sends a request to the host) right away, or later on a scheduler
        // thread if the previous request to the host started less than MinRequestStartIntervalMS
        // ago. Each call reserves its own start time, so requests that wait start in call order.
        public void StartRequest(Action start) {
            long startTicks = ReserveRequestStart();
            if (startTicks <= TickCount.Now) {
                start();
                return;
            }
            _requestStartScheduler.AddItem(startTicks, start, _requestStartThreadGroup);
        }

        private long ReserveRequestStart() {
            lock (_requestStartSync) {
                _lastRequestStartTicks = Math.Max(TickCount.Now, _lastRequestStartTicks + MinRequestStartIntervalMS);
                return _lastRequestStartTicks;
            }
        }

        // Waits for a free connection to the host. Returns null, without taking one, if
        // isCanceled returns true while waiting; it is called every SlotWaitPollMS.
        public string ObtainConnectionGroupName(Func<bool> isCanceled) {
            if (!_semaphore.WaitOne(SlotWaitPollMS, isCanceled)) return null;
            return GetConnectionGroupName();
        }

        public void ReleaseConnectionGroupName(string name) {
            lock (_groupNames) {
                _groupNames.Push(name);
            }
            _semaphore.Release();
        }

        public string SwapForFreshConnection(string name, string url) {
            ServicePoint servicePoint = ServicePointManager.FindServicePoint(new Uri(url));
            try {
                servicePoint.CloseConnectionGroup(name);
            }
            catch (NotImplementedException) {
                // Workaround for Mono
            }
            return GetConnectionGroupName();
        }

        private string GetConnectionGroupName() {
            lock (_groupNames) {
                return _groupNames.Count != 0 ? _groupNames.Pop() : Guid.NewGuid().ToString();
            }
        }
    }

    public class FIFOSemaphore {
        private int _currentCount;
        private int _maximumCount;
        private object _mainSync = new object();
        private Queue<QueueSync> _queueSyncs = new Queue<QueueSync>();

        public FIFOSemaphore(int initialCount, int maximumCount) {
            if (initialCount > maximumCount) {
                throw new ArgumentException();
            }
            if (initialCount < 0 || maximumCount < 1) {
                throw new ArgumentOutOfRangeException();
            }
            _currentCount = initialCount;
            _maximumCount = maximumCount;
        }

        public void WaitOne() {
            WaitOne(Timeout.Infinite);
        }

        // Waits until signaled, checking isCanceled every pollMS (outside of any lock). Returns
        // false, keeping no count and giving up its place in the queue, once isCanceled returns true.
        public bool WaitOne(int pollMS, Func<bool> isCanceled) {
            QueueSync queueSync = TakeOrEnqueue();
            if (queueSync == null) return true;
            while (!WaitSignaled(queueSync, pollMS)) {
                if (isCanceled()) return !Abandon(queueSync);
            }
            return true;
        }

        private static bool WaitSignaled(QueueSync queueSync, int timeout) {
            lock (queueSync) {
                return queueSync.IsSignaled || Monitor.Wait(queueSync, timeout) || queueSync.IsSignaled;
            }
        }

        // Returns false if the wait was signaled before it could be abandoned
        private static bool Abandon(QueueSync queueSync) {
            lock (queueSync) {
                if (queueSync.IsSignaled) return false;
                queueSync.IsAbandoned = true;
                return true;
            }
        }

        // Takes a count and returns null, or queues a new waiter and returns it
        private QueueSync TakeOrEnqueue() {
            lock (_mainSync) {
                if (_currentCount > 0) {
                    _currentCount--;
                    return null;
                }
                QueueSync queueSync = new QueueSync();
                _queueSyncs.Enqueue(queueSync);
                return queueSync;
            }
        }

        public bool WaitOne(int timeout) {
            QueueSync queueSync = TakeOrEnqueue();
            if (queueSync == null) return true;
            lock (queueSync) {
                if (queueSync.IsSignaled || Monitor.Wait(queueSync, timeout)) {
                    return true;
                }
                else {
                    queueSync.IsAbandoned = true;
                    return false;
                }
            }
        }

        public void Release() {
            lock (_mainSync) {
                if (_currentCount >= _maximumCount) { // Workaround for Mono
                    Type semaphoreException = Type.GetType("System.Threading.SemaphoreFullException");
                    object exception = Activator.CreateInstance(semaphoreException);
                    throw (SystemException)exception;
                }
                CheckQueue:
                if (_queueSyncs.Count == 0) {
                    _currentCount++;
                }
                else {
                    QueueSync queueSync = _queueSyncs.Dequeue();
                    lock (queueSync) {
                        if (queueSync.IsAbandoned) {
                            goto CheckQueue;
                        }
                        // Backup signal in case we acquired the lock before the waiter
                        queueSync.IsSignaled = true;
                        Monitor.Pulse(queueSync);
                    }
                }
            }
        }

        private class QueueSync {
            public bool IsSignaled { get; set; }
            public bool IsAbandoned { get; set; }
        }
    }

    public class WorkScheduler {
        private const int _maxThreadIdleTime = 15000;

        private object _sync = new object();
        private LinkedList<WorkItem> _workItems = new LinkedList<WorkItem>();
        private ManualResetEvent _scheduleChanged = new ManualResetEvent(false);
        private Thread _schedulerThread;

        public WorkItem AddItem(long runAtTicks, Action action) {
            return AddItem(runAtTicks, action, String.Empty);
        }

        public WorkItem AddItem(long runAtTicks, Action action, string group) {
            WorkItem item = new WorkItem(this, runAtTicks, action, group);
            AddItem(item);
            return item;
        }

        private void AddItem(WorkItem item) {
            lock (_sync) {
                LinkedListNode<WorkItem> nextNode = null;
                foreach (LinkedListNode<WorkItem> node in EnumerateNodes()) {
                    if (node.Value.RunAtTicks > item.RunAtTicks) {
                        nextNode = node;
                        break;
                    }
                }
                if (nextNode == null) {
                    _workItems.AddLast(item);
                }
                else {
                    _workItems.AddBefore(nextNode, item);
                }
                _scheduleChanged.Set();
                if (_schedulerThread == null) {
                    _schedulerThread = new Thread(SchedulerThread);
                    _schedulerThread.IsBackground = true;
                    _schedulerThread.Start();
                }
            }
        }

        public bool RemoveItem(WorkItem item) {
            lock (_sync) {
                if (_workItems.Remove(item)) {
                    _scheduleChanged.Set();
                    return true;
                }
                else {
                    return false;
                }
            }
        }

        private void ReAddItem(WorkItem item) {
            lock (_sync) {
                if (RemoveItem(item)) {
                    AddItem(item);
                }
            }
        }

        private void SchedulerThread() {
            while (true) {
                SchedulerWaitResult waitResult = WaitForFirstItem(GetFirstWaitTime());
                if (waitResult == SchedulerWaitResult.Exit) {
                    return;
                }
                if (waitResult == SchedulerWaitResult.Recheck) {
                    continue;
                }

                StartDueItems();
            }
        }

        // Resets the schedule changed signal and returns the time until the first
        // item is due, or null if there are no items.
        private int? GetFirstWaitTime() {
            int? firstWaitTime = null;

            lock (_sync) {
                _scheduleChanged.Reset();
                if (_workItems.Count != 0) {
                    firstWaitTime = (int)(_workItems.First.Value.RunAtTicks - TickCount.Now);
                }
            }

            return firstWaitTime;
        }

        // Waits until the first item is due or the schedule changes. With no items,
        // waits up to the idle time and then retires the thread if still idle.
        private SchedulerWaitResult WaitForFirstItem(int? firstWaitTime) {
            if (firstWaitTime <= 0) {
                return SchedulerWaitResult.RunDueItems;
            }
            if (_scheduleChanged.WaitOne(firstWaitTime ?? _maxThreadIdleTime, false)) {
                return SchedulerWaitResult.Recheck;
            }
            if (firstWaitTime != null) {
                return SchedulerWaitResult.RunDueItems;
            }
            return RetireSchedulerThreadIfIdle();
        }

        private SchedulerWaitResult RetireSchedulerThreadIfIdle() {
            lock (_sync) {
                if (_workItems.Count != 0) {
                    return SchedulerWaitResult.Recheck;
                }
                _schedulerThread = null;
                return SchedulerWaitResult.Exit;
            }
        }

        private void StartDueItems() {
            lock (_sync) {
                while (_workItems.Count != 0 && _workItems.First.Value.RunAtTicks <= TickCount.Now) {
                    _workItems.First.Value.StartRunning();
                    _workItems.RemoveFirst();
                }
            }
        }

        private IEnumerable<LinkedListNode<WorkItem>> EnumerateNodes() {
            LinkedListNode<WorkItem> node = _workItems.First;
            while (node != null) {
                yield return node;
                node = node.Next;
            }
        }

        private enum SchedulerWaitResult {
            RunDueItems,
            Recheck,
            Exit
        }

        public class WorkItem {
            private bool _hasStarted;
            private WorkScheduler _scheduler;
            private long _runAtTicks;
            private Action _action;
            private string _group;

            public WorkItem(WorkScheduler scheduler, long runAtTicks, Action action, string group) {
                _scheduler = scheduler;
                _runAtTicks = runAtTicks;
                _action = action;
                _group = group;
            }

            public long RunAtTicks {
                get { lock (_scheduler._sync) { return _runAtTicks; } }
                set {
                    lock (_scheduler._sync) {
                        if (_hasStarted) return;
                        _runAtTicks = value;
                        _scheduler.ReAddItem(this);
                    }
                }
            }

            public void StartRunning() {
                lock (_scheduler._sync) {
                    if (_hasStarted) {
                        throw new Exception("Work item has already started.");
                    }
                    _hasStarted = true;
                    ThreadPoolManager.QueueWorkItem(_group, _action);
                }
            }
        }
    }

    public class ThreadPoolManager {
        private const int _minThreadCount = 4;
        private const int _threadCreationDelay = 500;
        private const int _maxThreadIdleTime = 15000;

        private static Dictionary<string, ThreadPoolManager> _threadPoolManagers = new Dictionary<string, ThreadPoolManager>(StringComparer.OrdinalIgnoreCase);

        private object _sync = new object();
        private FIFOSemaphore _semaphore = new FIFOSemaphore(0, Int32.MaxValue);
        private Stack<ThreadPoolThread> _idleThreads = new Stack<ThreadPoolThread>();
        private ThreadPoolThread _schedulerThread = new ThreadPoolThread(null);

        public ThreadPoolManager() {
            lock (_sync) {
                for (int i = 0; i < _minThreadCount; i++) {
                    _idleThreads.Push(new ThreadPoolThread(this));
                    _semaphore.Release();
                }
            }
        }

        public static void QueueWorkItem(string group, Action action) {
            ThreadPoolManager manager;
            lock (_threadPoolManagers) {
                if (!_threadPoolManagers.TryGetValue(group, out manager)) {
                    manager = new ThreadPoolManager();
                    _threadPoolManagers[group] = manager;
                }
            }
            manager.QueueWorkItem(action);
        }

        public void QueueWorkItem(Action action) {
            _schedulerThread.QueueWorkItem(() => {
                ThreadPoolThread thread;
                if (_semaphore.WaitOne(_threadCreationDelay)) {
                    lock (_sync) {
                        thread = _idleThreads.Pop();
                    }
                }
                else {
                    thread = new ThreadPoolThread(this);
                }
                thread.QueueWorkItem(action);
                thread.QueueWorkItem(() => {
                    lock (_sync) {
                        _idleThreads.Push(thread);
                        _semaphore.Release();
                    }
                });
            });
        }

        private void OnThreadPoolThreadExit(ThreadPoolThread exitedThread) {
            lock (_sync) {
                if (_idleThreads.Count <= _minThreadCount) return;
                Stack<ThreadPoolThread> threads = RemoveIdleThread(exitedThread);
                while (threads.Count != 0) {
                    _idleThreads.Push(threads.Pop());
                }
            }
        }

        // Pops idle threads until the exited thread is found and removed, taking its
        // semaphore count. Returns the threads popped before it so the caller can
        // restore them. Must be called while holding _sync.
        private Stack<ThreadPoolThread> RemoveIdleThread(ThreadPoolThread exitedThread) {
            Stack<ThreadPoolThread> threads = new Stack<ThreadPoolThread>();
            while (_idleThreads.Count != 0) {
                ThreadPoolThread thread = _idleThreads.Pop();
                if (thread == exitedThread) {
                    if (!_semaphore.WaitOne(0)) {
                        throw new Exception("Semaphore count is invalid.");
                    }
                    break;
                }
                threads.Push(thread);
            }
            return threads;
        }

        private class ThreadPoolThread {
            private object _sync = new object();
            private ThreadPoolManager _manager;
            private Thread _thread;
            private ManualResetEvent _newWorkItem;
            private Queue<Action> _workItems = new Queue<Action>();

            internal ThreadPoolThread(ThreadPoolManager manager) {
                _manager = manager;
            }

            internal void QueueWorkItem(Action action) {
                lock (_sync) {
                    if (_thread == null) {
                        _newWorkItem = new ManualResetEvent(false);
                        _thread = new Thread(WorkThread);
                        _thread.IsBackground = true;
                        _thread.Start();
                    }
                    _workItems.Enqueue(action);
                    _newWorkItem.Set();
                }
            }

            private void WorkThread() {
                while (_newWorkItem.WaitOne(_maxThreadIdleTime, false) || !ReleaseThread()) {
                    Action workItem = DequeueWorkItem();
                    if (workItem != null) {
                        Thread.MemoryBarrier();
                        RunWorkItem(workItem);
                        Thread.MemoryBarrier();
                    }
                }
                if (_manager != null) {
                    _manager.OnThreadPoolThreadExit(this);
                }
            }

            // Returns the next work item, or null (and resets the new work item
            // signal) if the queue is empty.
            // An exception escaping a work item would end the process, so it is logged instead
            private static void RunWorkItem(Action workItem) {
                try {
                    workItem();
                }
                catch (Exception ex) {
                    General.LogQuietly("Unhandled exception in a background work item:" + Environment.NewLine + ex);
                }
            }

            private Action DequeueWorkItem() {
                Action workItem = null;
                lock (_sync) {
                    if (_workItems.Count != 0) {
                        workItem = _workItems.Dequeue();
                    }
                    else {
                        _newWorkItem.Reset();
                    }
                }
                return workItem;
            }

            private bool ReleaseThread() {
                lock (_sync) {
                    if (_workItems.Count == 0) {
                        _newWorkItem.Close();
                        _newWorkItem = null;
                        _thread = null;
                        return true;
                    }
                    else {
                        return false;
                    }
                }
            }
        }
    }

    public class HashSet<T> : IEnumerable<T> {
        private Dictionary<T, int> _dict;

        public HashSet() {
            _dict = new Dictionary<T, int>();
        }

        public HashSet(IEqualityComparer<T> comparer) {
            _dict = new Dictionary<T, int>(comparer);
        }

        public HashSet(IEnumerable<T> collection) :
            this()
        {
            AddRange(collection);
        }

        public HashSet(IEnumerable<T> collection, IEqualityComparer<T> comparer) :
            this(comparer)
        {
            AddRange(collection);
        }

        public int Count {
            get { return _dict.Count; }
        }

        public bool Add(T item) {
            if (!_dict.ContainsKey(item)) {
                _dict[item] = 0;
                return true;
            }
            return false;
        }

        private void AddRange(IEnumerable<T> collection) {
            foreach (T item in collection) {
                Add(item);
            }
        }

        public bool Remove(T item) {
            return _dict.Remove(item);
        }

        public void Clear() {
            _dict.Clear();
        }

        public bool Contains(T item) {
            return _dict.ContainsKey(item);
        }

        public IEnumerator<T> GetEnumerator() {
            foreach (KeyValuePair<T, int> item in _dict) {
                yield return item.Key;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() {
            return GetEnumerator();
        }
    }

    public class HashGeneratorStream : Stream {
        private HashAlgorithm _hashAlgo;
        private byte[] _dataHash;

        public HashGeneratorStream(HashType hashType) {
            switch (hashType) {
                case HashType.MD5:
                    _hashAlgo = new MD5CryptoServiceProvider();
                    break;
                default:
                    throw new Exception("Unsupported hash type.");
            }
        }

        public override bool CanRead {
            get { return false; }
        }

        public override bool CanSeek {
            get { return false; }
        }

        public override bool CanWrite {
            get { return true; }
        }

        public override void Write(byte[] buffer, int offset, int count) {
            if (_hashAlgo == null) {
                throw new Exception("Cannot write after hash has been finalized.");
            }
            _hashAlgo.TransformBlock(buffer, offset, count, null, 0);
        }

        public override void Flush() { }

        public byte[] GetDataHash() {
            if (_hashAlgo != null) {
                _hashAlgo.TransformFinalBlock(new byte[0], 0, 0);
                _dataHash = _hashAlgo.Hash;
                _hashAlgo = null;
            }
            return _dataHash;
        }

        public override long Length {
            get { throw new NotSupportedException(); }
        }

        public override long Position {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override int Read(byte[] buffer, int offset, int count) {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotSupportedException();
        }

        public override void SetLength(long value) {
            throw new NotSupportedException();
        }
    }
    
    public class ThrottledStream : Stream {
        public const long Infinite = 0;

        private static int _concurrentDownloads;
        private static readonly object _downloadsSync = new object();

        private Stream _baseStream;
        private long _maximumBytesPerSecond;
        private long _byteCount;
        private long _start;
        private bool _hasStarted;
        private bool _isReleased;
        private readonly object _throttleSync = new object();
        // Guards _isClosed; Dispose pulses it to end a throttle sleep early
        private readonly object _sleepSync = new object();
        private bool _isClosed;
        private long _sleptMilliseconds;

        protected long CurrentMilliseconds {
            get { return Environment.TickCount; }
        }

        // Total time spent sleeping to keep under the speed limit, which a reader's time limit
        // leaves out so that a slow but healthy throttled download is not cut off
        public long SleptMilliseconds {
            get { return Interlocked.Read(ref _sleptMilliseconds); }
        }

        public override long Position {
            get { return _baseStream.Position; }
            set { _baseStream.Position = value; }
        }

        public override long Length {
            get { return _baseStream.Length; }
        }

        public override bool CanWrite {
            get { return _baseStream.CanWrite; }
        }

        public override bool CanTimeout {
            get { return _baseStream.CanTimeout; }
        }

        public override bool CanSeek {
            get { return _baseStream.CanSeek; }
        }

        public override bool CanRead {
            get { return _baseStream.CanRead; }
        }

        public override int ReadTimeout {
            get { return _baseStream.ReadTimeout; }
            set { _baseStream.ReadTimeout = value; }
        }

        public override int WriteTimeout {
            get { return _baseStream.WriteTimeout; }
            set { _baseStream.WriteTimeout = value; }
        }

        public ThrottledStream(Stream baseStream, long maximumBytesPerSecond = Infinite) {
            if (baseStream == null) {
                throw new ArgumentNullException("baseStream");
            }

            if (maximumBytesPerSecond < 0) {
                throw new ArgumentOutOfRangeException("maximumBytesPerSecond", maximumBytesPerSecond, "The maximum number of bytes per second can't be negative.");
            }

            _baseStream = baseStream;
            _maximumBytesPerSecond = maximumBytesPerSecond;
            _start = CurrentMilliseconds;
            _byteCount = 0;
        }

        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state) {
            Throttle(count);
            return _baseStream.BeginRead(buffer, offset, count, callback, state);
        }

        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state) {
            Throttle(count);
            return _baseStream.BeginWrite(buffer, offset, count, callback, state);
        }

        public override int EndRead(IAsyncResult asyncResult) {
            return _baseStream.EndRead(asyncResult);
        }

        public override void EndWrite(IAsyncResult asyncResult) {
            _baseStream.EndWrite(asyncResult);
        }

        public override void Flush() {
            _baseStream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count) {
            Throttle(count);
            return _baseStream.Read(buffer, offset, count);
        }

        public override int ReadByte() {
            return _baseStream.ReadByte();
        }

        public override long Seek(long offset, SeekOrigin origin) {
            return _baseStream.Seek(offset, origin);
        }

        public override void SetLength(long value) {
            _baseStream.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count) {
            Throttle(count);
            _baseStream.Write(buffer, offset, count);
        }

        public override void WriteByte(byte value) {
            _baseStream.WriteByte(value);
        }

        protected void Throttle(int bufferSizeInBytes) {
            lock (_throttleSync) {
                if (bufferSizeInBytes <= 0) {
                    return;
                }

                UpdateMaximumBytesPerSecond();

                if (_maximumBytesPerSecond <= 0) {
                    return;
                }

                MarkStarted();

                _byteCount += bufferSizeInBytes;

                long weightedMaximumBytesPerSecond;
                lock (_downloadsSync) {
                    weightedMaximumBytesPerSecond = Math.Max(_maximumBytesPerSecond / Math.Max(_concurrentDownloads, 1), 1);
                }

                SleepIfOverLimit(weightedMaximumBytesPerSecond);
            }
        }

        // Picks up a changed speed limit from the settings. Called while holding _throttleSync.
        private void UpdateMaximumBytesPerSecond() {
            var maximumBytesPerSecond = Settings.MaximumBytesPerSecond ?? Infinite;
            if (_maximumBytesPerSecond != maximumBytesPerSecond) {
                _maximumBytesPerSecond = maximumBytesPerSecond;
                Reset();
            }
        }

        // Counts this stream as a concurrent download the first time it is throttled.
        // Called while holding _throttleSync.
        private void MarkStarted() {
            lock (_downloadsSync) {
                if (_hasStarted || _isReleased) {
                    return;
                }
                _concurrentDownloads += 1;
                _hasStarted = true;
            }
        }

        // Stops counting this stream as a concurrent download. Safe to call more than
        // once; the count is decremented at most once per stream.
        private void ReleaseDownloadSlot() {
            lock (_downloadsSync) {
                if (_hasStarted && !_isReleased) {
                    _concurrentDownloads -= 1;
                }
                _isReleased = true;
            }
        }

        // Sleeps long enough to bring the average speed down to the weighted limit.
        // Called while holding _throttleSync.
        private void SleepIfOverLimit(long weightedMaximumBytesPerSecond) {
            long elapsedMilliseconds = CurrentMilliseconds - _start;
            if (elapsedMilliseconds <= 0) {
                return;
            }
            long bps = _byteCount * 1000L / elapsedMilliseconds;
            if (bps <= weightedMaximumBytesPerSecond) {
                return;
            }
            long wakeElapsed = _byteCount * 1000L / weightedMaximumBytesPerSecond;

            int toSleep = (int)(wakeElapsed - elapsedMilliseconds);
            if (toSleep > 1) {
                SleepUnlessClosed(toSleep);
                Reset();
            }
        }

        // Sleeps like Thread.Sleep, but returns as soon as the stream is closed so a reader
        // blocked in Throttle does not hold up an abort
        private void SleepUnlessClosed(int milliseconds) {
            long sleepStart = TickCount.Now;
            try {
                lock (_sleepSync) {
                    if (!_isClosed) Monitor.Wait(_sleepSync, milliseconds);
                }
            }
            catch (ThreadAbortException) { }
            finally {
                Interlocked.Add(ref _sleptMilliseconds, TickCount.Now - sleepStart);
            }
        }

        private void WakeSleepers() {
            lock (_sleepSync) {
                _isClosed = true;
                Monitor.PulseAll(_sleepSync);
            }
        }

        protected void Reset() {
            long difference = CurrentMilliseconds - _start;
            if (difference > 1000) {
                _byteCount = 0;
                _start = CurrentMilliseconds;
            }
        }

        // Stream.Close and Stream.Dispose both end up here, so closing or disposing
        // more than once is safe.
        protected override void Dispose(bool disposing) {
            try {
                if (disposing) {
                    _baseStream.Dispose();
                }
            }
            finally {
                WakeSleepers();
                ReleaseDownloadSlot();
                base.Dispose(disposing);
            }
        }
    }

    public static class Enumerable {
        public static IEnumerable<TSource> Where<TSource>(IEnumerable<TSource> source, Func<TSource, bool> predicate) {
            foreach (TSource item in source) {
                if (predicate(item)) {
                    yield return item;
                }
            }
        }

        public static TSource FirstOrDefault<TSource>(IEnumerable<TSource> source) {
            foreach (TSource item in source) {
                return item;
            }
            return default(TSource);
        }

        public static bool Any<TSource>(IEnumerable<TSource> source, Func<TSource, bool> predicate) {
            foreach (TSource item in source) {
                if (predicate(item)) {
                    return true;
                }
            }
            return false;
        }

        public static bool All<TSource>(IEnumerable<TSource> source, Func<TSource, bool> predicate) {
            foreach (TSource item in source) {
                if (!predicate(item)) {
                    return false;
                }
            }
            return true;
        }

        public static IEnumerable<TResult> Select<TSource, TResult>(IEnumerable<TSource> source, Func<TSource, TResult> selector) {
            foreach (TSource item in source) {
                yield return selector(item);
            }
        }
    }

    public class DownloadStatusEventArgs : EventArgs {
        public DownloadType DownloadType { get; private set; }
        public int CompleteCount { get; private set; }
        public int TotalCount { get; private set; }

        public DownloadStatusEventArgs(DownloadType downloadType, int completeCount, int totalCount) {
            DownloadType = downloadType;
            CompleteCount = completeCount;
            TotalCount = totalCount;
        }
    }

    public class StopStatusEventArgs : EventArgs {
        public StopReason StopReason { get; private set; }

        public StopStatusEventArgs(StopReason stopReason) {
            StopReason = stopReason;
        }
    }

    public class ReparseStatusEventArgs : EventArgs {
        public ReparseType ReparseType { get; private set; }
        public int CompleteCount { get; private set; }
        public int TotalCount { get; private set; }

        public ReparseStatusEventArgs(ReparseType reparseType, int completeCount, int totalCount) {
            ReparseType = reparseType;
            CompleteCount = completeCount;
            TotalCount = totalCount;
        }
    }

    public class DownloadStartEventArgs : EventArgs {
        public long DownloadID { get; private set; }
        public string URL { get; private set; }
        public int TryNumber { get; private set; }
        public long? TotalSize { get; private set; }

        public DownloadStartEventArgs(long downloadID, string url, int tryNumber, long? totalSize) {
            DownloadID = downloadID;
            URL = url;
            TryNumber = tryNumber;
            TotalSize = totalSize;
        }
    }

    public class DownloadProgressEventArgs : EventArgs {
        public long DownloadID { get; private set; }
        public long DownloadedSize { get; private set; }

        public DownloadProgressEventArgs(long downloadID, long downloadedSize) {
            DownloadID = downloadID;
            DownloadedSize = downloadedSize;
        }
    }

    public class DownloadEndEventArgs : EventArgs {
        public long DownloadID { get; private set; }
        public long DownloadedSize { get; private set; }
        public bool IsSuccessful { get; private set; }

        public DownloadEndEventArgs(long downloadID, long downloadedSize, bool isSuccessful) {
            DownloadID = downloadID;
            DownloadedSize = downloadedSize;
            IsSuccessful = isSuccessful;
        }
    }

    public class AddThreadEventArgs : EventArgs {
        public string PageURL { get; private set; }

        public AddThreadEventArgs(string pageURL) {
            PageURL = pageURL;
        }
    }

    public delegate void EventHandler<TSender, TArgs>(TSender sender, TArgs e) where TArgs : EventArgs;

    public delegate void DownloadFileEndCallback(DownloadResult result);

    public delegate void DownloadPageEndCallback(DownloadResult result, string content, DateTime? lastModifiedTime);

    public delegate void Action();

    public delegate void Action<T1, T2>(T1 arg1, T2 arg2);

    public delegate void Action<T1, T2, T3>(T1 arg1, T2 arg2, T3 arg3);

    public delegate void Action<T1, T2, T3, T4>(T1 arg1, T2 arg2, T3 arg3, T4 arg4);

    public delegate TResult Func<TResult>();

    public delegate TResult Func<T, TResult>(T arg);

    public delegate TResult Func<T1, T2, TResult>(T1 arg1, T2 arg2);

    public delegate TResult Func<T1, T2, T3, TResult>(T1 arg1, T2 arg2, T3 arg3);

    public delegate TResult Func<T1, T2, T3, T4, TResult>(T1 arg1, T2 arg2, T3 arg3, T4 arg4);

    public enum ThreadDoubleClickAction {
        OpenFolder = 1,
        OpenURL = 2,
        Edit = 3
    }

    public enum HashType {
        None = 0,
        MD5 = 1
    }

    public enum ReplaceType {
        Other = 0,
        ImageLinkHref = 1,
        ImageSrc = 2,
        QuoteLinkHref = 3,
        DeadLink = 4,
        DeadPost = 5
    }

    public enum DownloadType {
        Page = 1,
        Image = 2,
        Thumbnail = 3
    }

    public enum DownloadResult {
        Completed = 1,
        Skipped = 2,
        RetryLater = 3,
        // Not sent, or answered with a rate limit: retried once the host's pause is over
        RateLimited = 4
    }

    public enum StopReason {
        Other = 0,
        UserRequest = 1,
        Exiting = 2,
        PageNotFound = 3,
        DownloadComplete = 4,
        IOError = 5
    }

    public enum ReparseType {
        Page = 1,
        Image = 2
    }

    public enum BOMType {
        None = 0,
        UTF8 = 1,
        UTF16LE = 2,
        UTF16BE = 3
    }

    public enum SlugType {
        First = 0,
        Last = 1,
        Only = 2
    }

    public enum WindowTitleMacro {
        ApplicationName = 0,
        TotalThreads = 1,
        RunningThreads = 2,
        DeadThreads = 3,
        StoppedThreads = 4
    }
}