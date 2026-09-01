using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using MyLog.Hubs;

namespace MyLog.Services;

public sealed class LogTailService : ILogTailService, IDisposable
{
    private readonly IFileBrowserService _files;
    private readonly IHubContext<LogHub> _hub;
    private readonly ILogger<LogTailService> _logger;
    private readonly ConcurrentDictionary<string, TailSubscription> _subscriptions = new();

    public LogTailService(IFileBrowserService files, IHubContext<LogHub> hub, ILogger<LogTailService> logger)
    {
        _files = files;
        _hub = hub;
        _logger = logger;
    }

    public async Task SubscribeAsync(string connectionId, string path, CancellationToken cancellationToken)
    {
        var safePath = _files.GetSafeFilePath(path);
        await UnsubscribeAsync(connectionId);

        var subscription = new TailSubscription(connectionId, safePath, _hub, _logger);
        try
        {
            _subscriptions[connectionId] = subscription;
            await subscription.StartAsync(cancellationToken);
        }
        catch
        {
            // Never leave a half-started subscription (and its watcher) behind.
            _subscriptions.TryRemove(new KeyValuePair<string, TailSubscription>(connectionId, subscription));
            subscription.Dispose();
            throw;
        }
    }

    public Task UnsubscribeAsync(string connectionId)
    {
        if (_subscriptions.TryRemove(connectionId, out var subscription)) subscription.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var connectionId in _subscriptions.Keys)
        {
            if (_subscriptions.TryRemove(connectionId, out var subscription)) subscription.Dispose();
        }
    }

    private sealed class TailSubscription : IDisposable
    {
        // FileSystemWatcher is unreliable on network shares (and silently dies when a
        // share blips), so a slow timer always backstops it.
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
        private const long InitialTailBytes = 64 * 1024;

        private readonly string _connectionId;
        private readonly string _path;
        private readonly IHubContext<LogHub> _hub;
        private readonly ILogger _logger;
        private readonly FileSystemWatcher? _watcher;
        private readonly Timer _poller;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _disposed = new();

        private long _position;

        public TailSubscription(string connectionId, string path, IHubContext<LogHub> hub, ILogger logger)
        {
            _connectionId = connectionId;
            _path = path;
            _hub = hub;
            _logger = logger;

            var info = new FileInfo(path);
            _position = Math.Max(0, info.Length - InitialTailBytes);

            try
            {
                _watcher = new FileSystemWatcher(info.DirectoryName!, info.Name)
                {
                    NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024
                };
                _watcher.Changed += OnFileEvent;
                _watcher.Created += OnFileEvent;
                _watcher.Error += OnWatcherError;
            }
            catch (Exception exception)
            {
                // UNC paths and some mounts refuse to host a watcher at all; polling still works.
                _logger.LogWarning(exception, "File watcher unavailable for {Path}; falling back to polling", path);
                _watcher = null;
            }

            _poller = new Timer(_ => _ = PumpAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_watcher is not null)
            {
                try { _watcher.EnableRaisingEvents = true; }
                catch (Exception exception) { _logger.LogWarning(exception, "Could not enable watcher for {Path}", _path); }
            }
            _poller.Change(PollInterval, PollInterval);

            // Send the initial tail before returning so the client cannot race it.
            await PumpAsync(isInitial: true);
        }

        private void OnFileEvent(object? sender, FileSystemEventArgs args) => _ = PumpAsync();

        private void OnWatcherError(object? sender, ErrorEventArgs args)
        {
            _logger.LogWarning(args.GetException(), "File watcher failed for {Path}; relying on polling", _path);
            try
            {
                if (_watcher is not null) _watcher.EnableRaisingEvents = false;
            }
            catch { /* watcher already dead */ }
        }

        /// <summary>
        /// Reads everything appended since the last read and pushes it to the client.
        /// A change notification that arrives while a read is in flight is discarded
        /// (there is nowhere to queue it), so the loop re-checks the file length before
        /// releasing the gate rather than trusting that another notification will follow.
        /// Together with the poll timer this makes delivery depend on the file's actual
        /// length instead of on the watcher firing at the right moment.
        /// </summary>
        private async Task PumpAsync(bool isInitial = false)
        {
            if (_disposed.IsCancellationRequested) return;
            if (!await _gate.WaitAsync(0)) return; // a pump is already running; it will re-check

            try
            {
                while (!_disposed.IsCancellationRequested)
                {
                    if (!await ReadOnceAsync(isInitial)) break;
                    isInitial = false;
                }
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Tail read failed for {Path}", _path);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <returns><c>true</c> when more data may already be waiting.</returns>
        private async Task<bool> ReadOnceAsync(bool isInitial)
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);

            // Rolled or truncated file: restart from the beginning.
            if (stream.Length < _position) _position = 0;
            if (stream.Length == _position) return false;

            stream.Position = _position;
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: false);

            // The very first read starts mid-file, so drop the partial first line.
            if (isInitial && _position > 0) await reader.ReadLineAsync(_disposed.Token);

            var text = await reader.ReadToEndAsync(_disposed.Token);
            _position = stream.Length;

            if (text.Length > 0 && !_disposed.IsCancellationRequested)
            {
                await _hub.Clients.Client(_connectionId).SendAsync("LogAppended", text, _disposed.Token);
            }

            // Something may have been appended while we were reading.
            return new FileInfo(_path).Length > _position;
        }

        public void Dispose()
        {
            if (!_disposed.IsCancellationRequested) _disposed.Cancel();

            if (_watcher is not null)
            {
                _watcher.Changed -= OnFileEvent;
                _watcher.Created -= OnFileEvent;
                _watcher.Error -= OnWatcherError;
                _watcher.Dispose();
            }

            _poller.Dispose();
            // The CTS is deliberately not disposed: an in-flight pump may still touch
            // its token, and disposing it would turn that into an ObjectDisposedException.
        }
    }
}
