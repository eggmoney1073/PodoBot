namespace PodoBot;

public sealed class TimerService : IAsyncDisposable
{
    private readonly LocalDataStore _store;
    private readonly Func<string, Task> _send;
    private readonly Dictionary<Guid, DateTime> _next = new();

    private CancellationTokenSource? _cts;
    private Task? _task;

    public event Action<string>? Log;

    public TimerService(LocalDataStore store, Func<string, Task> send)
    {
        _store = store;
        _send = send;
    }

    public void Start()
    {
        if (_task is not null)
            return;

        _cts = new CancellationTokenSource();
        _task = RunAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is null || _task is null)
            return;

        _cts.Cancel();

        try
        {
            await _task;
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
        _cts = null;
        _task = null;
        _next.Clear();
    }

    public void Reset() => _next.Clear();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var now = DateTime.UtcNow;
            var active = _store.Data.Timers.Where(x => x.Enabled).ToArray();

            foreach (var item in active)
            {
                if (string.IsNullOrWhiteSpace(item.Message))
                    continue;

                var interval = TimeSpan.FromMinutes(
                    Math.Max(0.1, item.IntervalMinutes));

                if (!_next.TryGetValue(item.Id, out var runAt))
                {
                    _next[item.Id] = now.Add(interval);
                    continue;
                }

                if (now < runAt)
                    continue;

                _next[item.Id] = now.Add(interval);

                try
                {
                    await _send(item.Message);
                    Log?.Invoke($"타이머 전송: {item.Message}");
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"타이머 전송 실패: {ex.Message}");
                }
            }

            var ids = active.Select(x => x.Id).ToHashSet();
            foreach (var id in _next.Keys.Where(x => !ids.Contains(x)).ToArray())
                _next.Remove(id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
