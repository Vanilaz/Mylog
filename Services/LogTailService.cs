using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using MyLog.Hubs;
using System.Security.Cryptography;
using System.Text;

namespace MyLog.Services;

public sealed class LogTailService : ILogTailService, IDisposable
{
    private readonly IFileBrowserService _files;
    private readonly IHubContext<LogHub> _hub;
    private readonly ILogger<LogTailService> _logger;
    private readonly ConcurrentDictionary<string, string> _connectionToPath = new();
    private readonly ConcurrentDictionary<string, SharedTailSubscription> _sharedTails = new();

    public LogTailService(IFileBrowserService files, IHubContext<LogHub> hub, ILogger<LogTailService> logger)
    {
        _files = files;
        _hub = hub;
        _logger = logger;
    }
    
    private static string GetGroupName(string path) 
    {
        // Safe group name for SignalR
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(path))).Replace("=", "").Replace("+", "").Replace("/", "");
    }

    public async Task SubscribeAsync(string connectionId, string path, CancellationToken cancellationToken)
    {
        var safePath = _files.GetSafeFilePath(path);
        await UnsubscribeAsync(connectionId);
        
        _connectionToPath[connectionId] = safePath;
        var groupName = GetGroupName(safePath);
        
        await _hub.Groups.AddToGroupAsync(connectionId, groupName, cancellationToken);

        lock (_sharedTails)
        {
            if (!_sharedTails.TryGetValue(safePath, out var sub))
            {
                sub = new SharedTailSubscription(safePath, groupName, _hub, _logger);
                _sharedTails[safePath] = sub;
                _ = sub.StartAsync(CancellationToken.None);
            }
            sub.SubscriberCount++;
            
            // Send the initial tail block to just this new connection
            _ = sub.SendInitialTailAsync(connectionId);
        }
    }

    public async Task UnsubscribeAsync(string connectionId)
    {
        if (_connectionToPath.TryRemove(connectionId, out var safePath))
        {
            var groupName = GetGroupName(safePath);
            await _hub.Groups.RemoveFromGroupAsync(connectionId, groupName);
            
            lock (_sharedTails)
            {
                if (_sharedTails.TryGetValue(safePath, out var sub))
                {
                    sub.SubscriberCount--;
                    if (sub.SubscriberCount <= 0)
                    {
                        _sharedTails.TryRemove(safePath, out _);
                        sub.Dispose();
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        foreach (var sub in _sharedTails.Values)
        {
            sub.Dispose();
        }
        _sharedTails.Clear();
    }

    private sealed class SharedTailSubscription : IDisposable
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
        private const long InitialTailBytes = 64 * 1024;
        private const long MaxCatchupBytes = 512 * 1024; // 512 KB
        private const int MaxChunkBytes = 64 * 1024; // 64 KB per chunk
        private const int MaxChunksPerPump = 2; // 128 KB max per batch flush
        private const int DebounceMs = 150;
        private const int MaxThrottleMs = 300;

        private readonly string _path;
        private readonly string _groupName;
        private readonly IHubContext<LogHub> _hub;
        private readonly ILogger _logger;
        private readonly FileSystemWatcher? _watcher;
        private readonly Timer _debounceTimer;
        private readonly Timer _poller;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _disposed = new();

        private long _position;
        private long _lastPumpTicks;
        public int SubscriberCount { get; set; }

        public SharedTailSubscription(string path, string groupName, IHubContext<LogHub> hub, ILogger logger)
        {
            _path = path;
            _groupName = groupName;
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
                _logger.LogWarning(exception, "File watcher unavailable for {Path}; falling back to polling", path);
                _watcher = null;
            }

            _debounceTimer = new Timer(_ => _ = PumpAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _poller = new Timer(_ => _ = PumpAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_watcher is not null)
            {
                try { _watcher.EnableRaisingEvents = true; }
                catch (Exception exception) { _logger.LogWarning(exception, "Could not enable watcher for {Path}", _path); }
            }
            _poller.Change(PollInterval, PollInterval);
            return Task.CompletedTask;
        }
        
        public async Task SendInitialTailAsync(string connectionId) 
        {
            try 
            {
                await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
                var pos = Math.Max(0, stream.Length - InitialTailBytes);
                if (pos >= stream.Length) return;
                
                stream.Position = pos;
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: false);
                if (pos > 0) await reader.ReadLineAsync(_disposed.Token);
                var text = await reader.ReadToEndAsync(_disposed.Token);
                
                if (text.Length > 0 && !_disposed.IsCancellationRequested)
                {
                    await _hub.Clients.Client(connectionId).SendAsync("LogAppended", text, _disposed.Token);
                }
            } 
            catch { /* Ignore */ }
        }

        private void OnFileEvent(object? sender, FileSystemEventArgs args) => SchedulePump();

        private void SchedulePump()
        {
            if (_disposed.IsCancellationRequested) return;

            var now = Environment.TickCount64;
            var elapsed = now - Interlocked.Read(ref _lastPumpTicks);

            if (elapsed >= MaxThrottleMs)
            {
                _ = PumpAsync();
            }
            else
            {
                try { _debounceTimer.Change(DebounceMs, Timeout.Infinite); }
                catch (ObjectDisposedException) { }
            }
        }

        private void OnWatcherError(object? sender, ErrorEventArgs args)
        {
            _logger.LogWarning(args.GetException(), "File watcher failed for {Path}; relying on polling", _path);
            try
            {
                if (_watcher is not null) _watcher.EnableRaisingEvents = false;
            }
            catch { /* watcher already dead */ }
        }

        private async Task PumpAsync()
        {
            if (_disposed.IsCancellationRequested) return;
            if (!await _gate.WaitAsync(0)) return;

            Interlocked.Exchange(ref _lastPumpTicks, Environment.TickCount64);

            try
            {
                var chunksSent = 0;
                while (!_disposed.IsCancellationRequested && chunksSent < MaxChunksPerPump)
                {
                    if (!await ReadOnceAsync()) break;
                    chunksSent++;
                }

                if (chunksSent >= MaxChunksPerPump && !_disposed.IsCancellationRequested)
                {
                    try { _debounceTimer.Change(50, Timeout.Infinite); }
                    catch (ObjectDisposedException) { }
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

        private async Task<bool> ReadOnceAsync()
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);

            if (stream.Length < _position)
            {
                _position = Math.Max(0, stream.Length - InitialTailBytes);
            }

            if (stream.Length == _position) return false;

            if (stream.Length - _position > MaxCatchupBytes)
            {
                _position = Math.Max(0, stream.Length - InitialTailBytes);
                if (!_disposed.IsCancellationRequested)
                {
                    await _hub.Clients.Group(_groupName).SendAsync(
                        "LogAppended",
                        "\n[... Output skipped to protect memory due to rapid log burst ...]\n",
                        _disposed.Token);
                }
            }

            return await ReadChunkAndSendAsync(stream);
        }

        private async Task<bool> ReadChunkAndSendAsync(FileStream stream)
        {
            var available = stream.Length - _position;
            if (available <= 0) return false;

            var bytesToRead = (int)Math.Min(available, MaxChunkBytes);
            var buffer = ArrayPool<byte>.Shared.Rent(bytesToRead);

            try
            {
                stream.Position = _position;
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), _disposed.Token);
                if (bytesRead <= 0) return false;

                var bytesToSend = bytesRead;
                var hasMore = stream.Length > _position + bytesRead;

                if (hasMore)
                {
                    var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', bytesRead - 1, bytesRead);
                    if (lastNewline >= 0)
                    {
                        bytesToSend = lastNewline + 1;
                    }
                }

                var text = Encoding.UTF8.GetString(buffer, 0, bytesToSend);
                _position += bytesToSend;

                if (text.Length > 0 && !_disposed.IsCancellationRequested)
                {
                    await _hub.Clients.Group(_groupName).SendAsync("LogAppended", text, _disposed.Token);
                }

                return stream.Length > _position;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
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

            _debounceTimer.Dispose();
            _poller.Dispose();
            _gate.Dispose();
            _disposed.Dispose();
        }
    }
}
