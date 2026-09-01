using Microsoft.AspNetCore.SignalR;
using MyLog.Services;

namespace MyLog.Hubs;

public sealed class LogHub : Hub
{
    private readonly ILogTailService _tail;

    public LogHub(ILogTailService tail) => _tail = tail;

    public Task Subscribe(string filePath) => _tail.SubscribeAsync(Context.ConnectionId, filePath, Context.ConnectionAborted);

    public Task Unsubscribe() => _tail.UnsubscribeAsync(Context.ConnectionId);

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _tail.UnsubscribeAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}