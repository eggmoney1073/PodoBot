using System.Security.Cryptography;

namespace PodoBot;

public sealed class BotEngine
{
    private static readonly TimeSpan RouletteSpinDuration = TimeSpan.FromMilliseconds(3700);
    private static readonly TimeSpan RouletteRerollDelay = TimeSpan.FromMilliseconds(900);

    private readonly LocalDataStore _store;
    private readonly Func<string, Task> _send;
    private readonly Func<RouletteResult, Task> _rouletteOverlay;
    private readonly Dictionary<string, DateTime> _cooldowns = new();
    private readonly Dictionary<string, DateTime> _outgoing = new();
    private readonly SemaphoreSlim _rouletteGate = new(1, 1);
    private readonly object _sync = new();

    public event Action<string>? Log;

    public BotEngine(
        LocalDataStore store,
        Func<string, Task> send,
        Func<RouletteResult, Task> rouletteOverlay)
    {
        _store = store;
        _send = send;
        _rouletteOverlay = rouletteOverlay;
    }

    public void NotifyOutgoing(string text)
    {
        lock (_sync)
            _outgoing[text] = DateTime.UtcNow.AddSeconds(8);
    }

    public async Task ProcessAsync(ChatEvent chat)
    {
        if (string.IsNullOrWhiteSpace(chat.Content) || IsBotEcho(chat.Content))
            return;

        Log?.Invoke($"{chat.Nickname}: {chat.Content}");

        if (await TryBuiltInCommandListAsync(chat))
            return;

        if (await TryCommandAsync(chat))
            return;

        if (await TryRouletteAsync(chat))
            return;

        await TryCounterAsync(chat);
    }

    private async Task<bool> TryBuiltInCommandListAsync(ChatEvent chat)
    {
        var content = chat.Content.Trim();

        if (!content.Equals("!명령어", StringComparison.OrdinalIgnoreCase)
            && !content.Equals("!commands", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Acquire($"builtin:commands:user:{chat.SenderChannelId}", 3))
            return true;

        var triggers = new List<string>
        {
            "!명령어",
            "!commands"
        };

        triggers.AddRange(
            _store.Data.Commands
                .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Trigger))
                .Select(x => x.Trigger.Trim()));

        if (_store.Data.Roulette.Enabled
            && !string.IsNullOrWhiteSpace(_store.Data.Roulette.Trigger))
        {
            triggers.Add(_store.Data.Roulette.Trigger.Trim());
        }

        triggers.AddRange(
            _store.Data.Counters
                .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Trigger))
                .Select(x => x.Trigger.Trim()));

        var distinct = triggers
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        const string prefix = "사용 가능 명령어: ";
        var text = prefix;

        foreach (var trigger in distinct)
        {
            var next = text == prefix
                ? prefix + trigger
                : text + ", " + trigger;

            if (next.Length > 95)
            {
                text += ", ...";
                break;
            }

            text = next;
        }

        await _send(text);
        return true;
    }

    private async Task<bool> TryCommandAsync(ChatEvent chat)
    {
        foreach (var command in _store.Data.Commands.Where(x => x.Enabled))
        {
            if (!Match(chat.Content, command.Trigger, out var args))
                continue;

            if (!HasPermission(command.Permission, chat.UserRoleCode))
                return true;

            if (!Acquire($"cmd:{command.Id}:all", command.CooldownSeconds))
                return true;

            if (!Acquire(
                    $"cmd:{command.Id}:user:{chat.SenderChannelId}",
                    command.UserCooldownSeconds))
            {
                return true;
            }

            var text = command.Response
                .Replace("{user}", chat.Nickname)
                .Replace("{args}", args);

            await _send(text);
            return true;
        }

        return false;
    }

    private async Task<bool> TryRouletteAsync(ChatEvent chat)
    {
        var settings = _store.Data.Roulette;

        if (!settings.Enabled
            || !Match(chat.Content, settings.Trigger, out _))
        {
            return false;
        }

        if (!HasPermission(settings.Permission, chat.UserRoleCode))
            return true;

        if (!Acquire("roulette:all", settings.CooldownSeconds))
            return true;

        if (!Acquire(
                $"roulette:user:{chat.SenderChannelId}",
                settings.UserCooldownSeconds))
        {
            return true;
        }

        if (!await _rouletteGate.WaitAsync(0))
        {
            Log?.Invoke("룰렛이 이미 진행 중입니다.");
            return true;
        }

        try
        {
            var items = _store.Data.RouletteItems
                .Where(x => !string.IsNullOrWhiteSpace(x.Text)
                            && x.ChancePercent > 0)
                .ToList();

            var total = items.Sum(x => x.ChancePercent);

            if (items.Count == 0 || Math.Abs(total - 100) > 0.001)
            {
                Log?.Invoke("룰렛 확률 합계가 100%인지 확인하세요.");
                return true;
            }

            var finalItems = items
                .Where(x => !IsRerollResult(x.Text))
                .ToList();

            if (finalItems.Count == 0)
            {
                Log?.Invoke("룰렛에는 '한 번 더' 외의 최종 결과가 최소 1개 필요합니다.");
                return true;
            }

            RouletteItem? finalResult = null;
            var finalIndex = -1;

            for (var rerollCount = 0; rerollCount < 20; rerollCount++)
            {
                var selectedIndex = PickWeightedIndex(items);
                var selected = items[selectedIndex];

                await _rouletteOverlay(
                    new RouletteResult(
                        selected.Text,
                        selectedIndex,
                        chat.Nickname));

                Log?.Invoke(
                    $"룰렛 애니메이션: {chat.Nickname} -> {selected.Text}");

                await Task.Delay(RouletteSpinDuration);

                if (!IsRerollResult(selected.Text))
                {
                    finalResult = selected;
                    finalIndex = selectedIndex;
                    break;
                }

                Log?.Invoke("룰렛 '한 번 더' -> 자동 재추첨");
                await Task.Delay(RouletteRerollDelay);
            }

            if (finalResult is null)
            {
                var selectedIndex = PickWeightedIndex(finalItems);
                finalResult = finalItems[selectedIndex];
                finalIndex = items.IndexOf(finalResult);

                await _rouletteOverlay(
                    new RouletteResult(
                        finalResult.Text,
                        finalIndex,
                        chat.Nickname));

                await Task.Delay(RouletteSpinDuration);
            }

            var response = settings.Response
                .Replace("{user}", chat.Nickname)
                .Replace("{result}", finalResult.Text);

            await _send(response);

            Log?.Invoke(
                $"룰렛 최종 결과: {chat.Nickname} -> {finalResult.Text}");

            return true;
        }
        finally
        {
            _rouletteGate.Release();
        }
    }

    private static int PickWeightedIndex(IReadOnlyList<RouletteItem> items)
    {
        var total = items.Sum(x => x.ChancePercent);

        var roll = RandomNumberGenerator.GetInt32(0, 1_000_000)
                   / 1_000_000.0
                   * total;

        var sum = 0.0;

        for (var i = 0; i < items.Count; i++)
        {
            sum += items[i].ChancePercent;

            if (roll < sum)
                return i;
        }

        return items.Count - 1;
    }

    private static bool IsRerollResult(string text)
    {
        var normalized = new string(
            text.Where(c => !char.IsWhiteSpace(c))
                .ToArray());

        return normalized.Equals(
            "한번더",
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TryCounterAsync(ChatEvent chat)
    {
        foreach (var counter in _store.Data.Counters.Where(x => x.Enabled))
        {
            if (!Match(chat.Content, counter.Trigger, out _))
                continue;

            if (!HasPermission(counter.Permission, chat.UserRoleCode))
                return true;

            counter.Value += counter.Step;
            await _store.SaveAsync();
            await _send($"{counter.Name}: {counter.Value}");
            return true;
        }

        return false;
    }

    private bool IsBotEcho(string text)
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;

            foreach (var key in _outgoing
                         .Where(x => x.Value <= now)
                         .Select(x => x.Key)
                         .ToArray())
            {
                _outgoing.Remove(key);
            }

            if (_outgoing.ContainsKey(text))
            {
                _outgoing.Remove(text);
                return true;
            }

            return false;
        }
    }

    private bool Acquire(string key, int seconds)
    {
        if (seconds <= 0)
            return true;

        lock (_sync)
        {
            var now = DateTime.UtcNow;

            if (_cooldowns.TryGetValue(key, out var until)
                && until > now)
            {
                return false;
            }

            _cooldowns[key] = now.AddSeconds(seconds);
            return true;
        }
    }

    private static bool Match(
        string content,
        string trigger,
        out string args)
    {
        args = "";
        content = content.Trim();
        trigger = trigger.Trim();

        if (string.IsNullOrWhiteSpace(trigger))
            return false;

        if (content.Equals(
                trigger,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (content.StartsWith(
                trigger + " ",
                StringComparison.OrdinalIgnoreCase))
        {
            args = content[(trigger.Length + 1)..].Trim();
            return true;
        }

        return false;
    }

    private static bool HasPermission(
        string permission,
        string role)
    {
        var p = permission.Trim().ToLowerInvariant();
        var r = role.Trim().ToLowerInvariant();

        return p switch
        {
            "" or "전체" or "everyone" => true,

            "매니저" or "manager" =>
                r is "streamer"
                    or "streaming_channel_manager"
                    or "streaming_chat_manager",

            "스트리머" or "streamer" =>
                r == "streamer",

            _ => false
        };
    }
}
