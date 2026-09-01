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
        _subscriptions[connectionId] = subscription;
        await subscription.StartAsync(cancellationToken);
    }

    public Task UnsubscribeAsync(string connectionId)
    {
        if (_subscriptions.TryRemove(connectionId, out var subscription)) subscription.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions.Values) subscription.Dispose();
        _subscriptions.Clear();
    }

    private sealed class TailSubscription : IDisposable
    {
        private readonly string _connectionId;
        private readonly string _path;
        private readonly IHubContext<LogHub> _hub;
        private readonly ILogger _logger;
        private readonly FileSystemWatcher _watcher;
        private long _position;
        private int _reading;

        public TailSubscription(string connectionId, string path, IHubContext<LogHub> hub, ILogger logger)
        {
            _connectionId = connectionId;
            _path = path;
            _hub = hub;
            _logger = logger;
            var info = new FileInfo(path);
            long initialBytes = 64 * 1024; // 64 KB
            _position = Math.Max(0, info.Length - initialBytes);
            _watcher = new FileSystemWatcher(info.DirectoryName!, info.Name) { NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite };
            _watcher.Changed += OnChanged;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _watcher.EnableRaisingEvents = true;
            OnChanged(this, null);
            return Task.CompletedTask;
        }

        private async void OnChanged(object? sender, FileSystemEventArgs? args)
        {
            if (Interlocked.Exchange(ref _reading, 1) == 1) return;
            try
            {
                await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, true);
                if (stream.Length < _position) _position = 0;
                stream.Position = _position;
                using var reader = new StreamReader(stream);
                
                if (sender == this && _position > 0)
                {
                    await reader.ReadLineAsync(); // discard partial line
                }

                var text = await reader.ReadToEndAsync();
                _position = stream.Position;
                if (text.Length > 0) await _hub.Clients.Client(_connectionId).SendAsync("LogAppended", text);
            }
            catch (Exception exception) { _logger.LogDebug(exception, "Tail read failed for {Path}", _path); }
            finally { Volatile.Write(ref _reading, 0); }
        }

        public void Dispose() => _watcher.Dispose();
    }
}