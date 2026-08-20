namespace LogsPlatform.Client;

public interface ILogsPlatformClient : IAsyncDisposable
{
    Task SendEventAsync(EventPayload evt);
    Task FlushAsync();
}
