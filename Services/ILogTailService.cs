namespace MyLog.Services;

public interface ILogTailService
{
    Task SubscribeAsync(string connectionId, string path, CancellationToken cancellationToken);
    Task UnsubscribeAsync(string connectionId);
}