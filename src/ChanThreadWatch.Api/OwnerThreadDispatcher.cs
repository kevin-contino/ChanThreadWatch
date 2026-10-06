using System;
using System.Threading;
using System.Threading.Tasks;

namespace JDP.Api {
    // The owner thread (the app's UI thread, ctw watch's owner thread) is not available: the host is exiting, or the
    // owner thread did not take the work in time. The request gets 503.
    internal sealed class ApiUnavailableException : Exception {
        public ApiUnavailableException()
            : base("The owner thread is not available.") { }
    }

    // Runs a request's work on the owner thread through the host's post delegate (the window's BeginInvoke, ctw's
    // HeadlessWatch.Post), which queues it and returns without waiting. A request thread never waits synchronously
    // for the owner thread, which waits for the server when the host exits. Work that has not started when its
    // request times out or Close() is called never runs, so a request that got 503 has changed nothing.
    internal sealed class OwnerThreadDispatcher {
        private readonly Action<Action> _post;
        private readonly CancellationTokenSource _closed = new CancellationTokenSource();

        public OwnerThreadDispatcher(Action<Action> post) {
            _post = post ?? throw new ArgumentNullException(nameof(post));
        }

        public bool IsClosed {
            get { return _closed.IsCancellationRequested; }
        }

        // Pending and later calls fail with ApiUnavailableException. Called by the server's stop, before the host
        // stops its owner thread. The pending requests go on on the thread pool, never on the caller's thread (which
        // can be the app's UI thread): CancelAsync marks the source canceled at once and runs the callbacks elsewhere.
        public void Close() {
            try {
                _ = _closed.CancelAsync();
            }
            catch (ObjectDisposedException) {
            }
        }

        public async Task<T> RunAsync<T>(Func<T> work, TimeSpan timeout) {
            if (work == null) throw new ArgumentNullException(nameof(work));
            if (IsClosed) throw new ApiUnavailableException();
            PendingWork<T> pending = new PendingWork<T>(work, () => IsClosed);
            Post(pending);
            try {
                return await pending.Task.WaitAsync(timeout, _closed.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException || ex is OperationCanceledException) {
                return await pending.AbandonOrWaitAsync().ConfigureAwait(false);
            }
        }

        // A post delegate that fails (the window is gone) means the owner thread will never run the work
        private void Post<T>(PendingWork<T> pending) {
            try {
                _post(pending.Run);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException) {
                pending.Abandon();
            }
        }

        private sealed class PendingWork<T> {
            private const int NotStarted = 0;
            private const int Started = 1;
            private const int Abandoned = 2;

            private readonly Func<T> _work;
            private readonly Func<bool> _isClosed;
            private readonly TaskCompletionSource<T> _result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _state;

            public PendingWork(Func<T> work, Func<bool> isClosed) {
                _work = work;
                _isClosed = isClosed;
            }

            public Task<T> Task {
                get { return _result.Task; }
            }

            // On the owner thread
            public void Run() {
                if (_isClosed() || Interlocked.CompareExchange(ref _state, Started, NotStarted) != NotStarted) {
                    Abandon();
                    return;
                }
                try {
                    _result.TrySetResult(_work());
                }
                catch (Exception ex) {
                    _result.TrySetException(ex);
                }
            }

            public void Abandon() {
                Interlocked.CompareExchange(ref _state, Abandoned, NotStarted);
                _result.TrySetException(new ApiUnavailableException());
            }

            // Work that already started on the owner thread finishes there (it is short), so its result is kept
            public Task<T> AbandonOrWaitAsync() {
                if (Interlocked.CompareExchange(ref _state, Abandoned, NotStarted) == NotStarted) {
                    _result.TrySetException(new ApiUnavailableException());
                }
                return _result.Task;
            }
        }
    }
}
