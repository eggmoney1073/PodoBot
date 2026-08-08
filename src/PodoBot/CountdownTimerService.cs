namespace PodoBot;

public sealed class CountdownTimerService : IAsyncDisposable
{
    private readonly Func<string, Task> _send;
    private readonly OverlayServer _overlay;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public event Action<string>? Log;
    public event Action<string>? StatusChanged;

    public CountdownTimerService(Func<string, Task> send, OverlayServer overlay)
    {
        _send = send;
        _overlay = overlay;
    }

    public async Task StartAsync(CountdownTimerConfig config)
    {
        var duration = config.GetDuration();
        if (duration <= TimeSpan.Zero)
            throw new InvalidOperationException("타이머 시간을 1초 이상으로 설정하세요.");

        await StopAsync(notifyOverlay: false);

        _cts = new CancellationTokenSource();
        _task = RunAsync(config, duration, _cts.Token);
        Log?.Invoke($"타이머 시작: {config.Name} / {Format(duration)}");
    }

    public async Task StopAsync(bool notifyOverlay = true)
    {
        var cts = _cts;
        var task = _task;
        _cts = null;
        _task = null;

        if (cts is null) return;

        cts.Cancel();
        try
        {
            if (task is not null) await task;
        }
        catch (OperationCanceledException) { }
        finally { cts.Dispose(); }

        if (notifyOverlay)
            await _overlay.PublishTimerCancelAsync();

        StatusChanged?.Invoke("대기 중");
    }

    private async Task RunAsync(CountdownTimerConfig config, TimeSpan duration, CancellationToken cancellationToken)
    {
        var endsAtUtc = DateTime.UtcNow.Add(duration);
        await _overlay.PublishTimerStartAsync(config.Name, endsAtUtc, config.FinishMessage);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (!cancellationToken.IsCancellationRequested)
        {
            var remaining = endsAtUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            StatusChanged?.Invoke($"{config.Name}  {Format(remaining)}");
            await timer.WaitForNextTickAsync(cancellationToken);
        }

        StatusChanged?.Invoke($"{config.Name}  00:00:00");
        await _overlay.PublishTimerCompleteAsync(config.Name, config.FinishMessage);

        if (!string.IsNullOrWhiteSpace(config.FinishMessage))
        {
            try { await _send(config.FinishMessage); }
            catch (Exception ex) { Log?.Invoke($"타이머 종료 메시지 전송 실패: {ex.Message}"); }
        }

        Log?.Invoke($"타이머 종료: {config.Name}");
        await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        StatusChanged?.Invoke("대기 중");
    }

    private static string Format(TimeSpan time)
    {
        var totalSeconds = Math.Max(0L, (long)Math.Ceiling(time.TotalSeconds));
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        return $"{hours:00}:{minutes:00}:{seconds:00}";
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
