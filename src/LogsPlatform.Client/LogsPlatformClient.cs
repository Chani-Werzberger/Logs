using System.Net.Http.Json;

namespace LogsPlatform.Client;

public sealed class LogsPlatformClient : ILogsPlatformClient
{
    private const string IngestPath = "api/v1/ingest/events";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _apiKey;
    private readonly int _batchSize;
    private readonly int _queueLimit;
    private readonly Timer _timer;
    private readonly object _bufferLock = new();
    private readonly List<EventPayload> _buffer = new();
    private readonly List<Task> _pendingFlushes = new();
    private readonly object _pendingFlushesLock = new();
    private bool _disposed;

    public LogsPlatformClient(
        string baseUrl,
        string apiKey,
        HttpClient? httpClient = null,
        int batchSize = 100,
        TimeSpan? period = null,
        int queueLimit = 10_000)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseUrl);
        ArgumentException.ThrowIfNullOrEmpty(apiKey);

        _batchSize = batchSize;
        _queueLimit = queueLimit;
        _apiKey = apiKey;

        if (httpClient is null)
        {
            _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }

        var actualPeriod = period ?? TimeSpan.FromSeconds(2);
        _timer = new Timer(OnTimerTick, null, actualPeriod, actualPeriod);
    }

    public Task SendEventAsync(EventPayload evt)
    {
        List<EventPayload>? toFlush = null;
        lock (_bufferLock)
        {
            _buffer.Add(evt);
            while (_buffer.Count > _queueLimit)
            {
                _buffer.RemoveAt(0);
            }
            if (_buffer.Count >= _batchSize)
            {
                toFlush = new List<EventPayload>(_buffer);
                _buffer.Clear();
            }
        }

        if (toFlush is not null)
        {
            TrackPendingFlush(() => FlushBatchAsync(toFlush));
        }

        return Task.CompletedTask;
    }

    public Task FlushAsync()
    {
        List<EventPayload>? toFlush = null;
        lock (_bufferLock)
        {
            if (_buffer.Count > 0)
            {
                toFlush = new List<EventPayload>(_buffer);
                _buffer.Clear();
            }
        }

        return toFlush is null ? Task.CompletedTask : FlushBatchAsync(toFlush);
    }

    private void OnTimerTick(object? state)
    {
        TrackPendingFlush(() => FlushAsync());
    }

    private void TrackPendingFlush(Func<Task> startFlush)
    {
        lock (_pendingFlushesLock)
        {
            _pendingFlushes.RemoveAll(t => t.IsCompleted);
            _pendingFlushes.Add(startFlush());
        }
    }

    private async Task FlushBatchAsync(List<EventPayload> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, IngestPath)
            {
                Content = JsonContent.Create(batch)
            };
            request.Headers.Add("X-Api-Key", _apiKey);

            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[LogsPlatform.Client] Ingestion request failed with status {(int)response.StatusCode}.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LogsPlatform.Client] Ingestion request failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await _timer.DisposeAsync().ConfigureAwait(false);
        await FlushAsync().ConfigureAwait(false);

        Task[] pending;
        lock (_pendingFlushesLock)
        {
            pending = _pendingFlushes.Where(t => !t.IsCompleted).ToArray();
        }
        await Task.WhenAll(pending).ConfigureAwait(false);

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
