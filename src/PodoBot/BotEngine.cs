using System.Security.Cryptography;

namespace PodoBot;

public sealed class BotEngine
{
    private static readonly TimeSpan RouletteSpinDuration = TimeSpan.FromMilliseconds(3800);
    private static readonly TimeSpan RouletteRerollDelay = TimeSpan.FromMilliseconds(700);

    private readonly LocalDataStore _store;
    private readonly Func<string, Task> _send;
    private readonly Func<RouletteResult, Task> _rouletteOverlay;
    private readonly Dictionary<string, DateTime> _cooldowns = new();
    private readonly Dictionary<string, DateTime> _outgoing = new();
    private readonly SemaphoreSlim _rouletteGate = new(1, 1);
    private readonly object _sync = new();

    public event Action<string>? Log;

    public BotEngine(LocalDataStore store, Func<string, Task> send, Func<RouletteResult, Task> rouletteOverlay)
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

        if (await TryBuiltInCommandListAsync(chat)) return;
        if (await TrySongBookAsync(chat)) return;
        if (await TryCommandAsync(chat)) return;
        if (await TryRouletteCommandAsync(chat)) return;
        await TryCounterAsync(chat);
    }

    public async Task ProcessDonationAsync(DonationEvent donation)
    {
        Log?.Invoke($"후원: {donation.DonatorNickname} / {donation.PayAmount:N0} / {donation.DonationText}");

        foreach (var rule in _store.Data.DonationRules.Where(x => x.Enabled))
        {
            if (donation.PayAmount < rule.MinAmount)
                continue;

            if (!string.IsNullOrWhiteSpace(rule.Keyword)
                && !donation.DonationText.Contains(rule.Keyword, StringComparison.OrdinalIgnoreCase))
                continue;

            var roulette = _store.Data.Roulettes.FirstOrDefault(x => x.Id == rule.RouletteId && x.Enabled);

            if (roulette is null)
            {
                Log?.Invoke("후원 룰렛 규칙의 대상 룰렛을 찾을 수 없습니다.");
                return;
            }

            Log?.Invoke($"후원 조건 일치 -> {roulette.Name} 실행");
            await RunRouletteAsync(roulette, donation.DonatorNickname, queueIfBusy: true);
            return;
        }
    }

    private async Task<bool> TryBuiltInCommandListAsync(ChatEvent chat)
    {
        var content = chat.Content.Trim();

        if (!content.Equals("!명령어", StringComparison.OrdinalIgnoreCase)
            && !content.Equals("!commands", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Acquire($"builtin:commands:user:{chat.SenderChannelId}", 3))
            return true;

        var triggers = new List<string> { "!명령어", "!commands", "!노래책", "!노래검색" };
        triggers.AddRange(_store.Data.Commands.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Trigger)).Select(x => x.Trigger.Trim()));
        triggers.AddRange(_store.Data.Roulettes.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Trigger)).Select(x => x.Trigger.Trim()));
        triggers.AddRange(_store.Data.Counters.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Trigger)).Select(x => x.Trigger.Trim()));

        var distinct = triggers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        const string prefix = "사용 가능 명령어: ";
        var text = prefix;

        foreach (var trigger in distinct)
        {
            var next = text == prefix ? prefix + trigger : text + ", " + trigger;
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

    private async Task<bool> TrySongBookAsync(ChatEvent chat)
    {
        var content = chat.Content.Trim();

        if (content.Equals("!노래책", StringComparison.OrdinalIgnoreCase))
        {
            await _send($"노래방 책 {_store.Data.Songs.Count}곡 등록. 검색: !노래검색 곡명/가수/번호");
            return true;
        }

        const string trigger = "!노래검색";
        if (!content.Equals(trigger, StringComparison.OrdinalIgnoreCase)
            && !content.StartsWith(trigger + " ", StringComparison.OrdinalIgnoreCase))
            return false;

        var query = content.Length > trigger.Length ? content[(trigger.Length + 1)..].Trim() : "";

        if (string.IsNullOrWhiteSpace(query))
        {
            await _send("사용법: !노래검색 곡명/가수/번호");
            return true;
        }

        var results = _store.Data.Songs
            .Where(x => x.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || x.Artist.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || x.Number.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || x.Provider.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToArray();

        if (results.Length == 0)
        {
            await _send($"노래방 책에서 '{query}' 검색 결과가 없습니다.");
            return true;
        }

        var text = string.Join(" | ", results.Select(x => $"[{x.Provider} {x.Number}] {x.Title} - {x.Artist}"));
        await _send(text);
        return true;
    }

    private async Task<bool> TryCommandAsync(ChatEvent chat)
    {
        foreach (var command in _store.Data.Commands.Where(x => x.Enabled))
        {
            if (!Match(chat.Content, command.Trigger, out var args)) continue;
            if (!HasPermission(command.Permission, chat.UserRoleCode)) return true;
            if (!Acquire($"cmd:{command.Id}:all", command.CooldownSeconds)) return true;
            if (!Acquire($"cmd:{command.Id}:user:{chat.SenderChannelId}", command.UserCooldownSeconds)) return true;

            var text = command.Response.Replace("{user}", chat.Nickname).Replace("{args}", args);
            await _send(text);
            return true;
        }

        return false;
    }

    private async Task<bool> TryRouletteCommandAsync(ChatEvent chat)
    {
        foreach (var roulette in _store.Data.Roulettes.Where(x => x.Enabled))
        {
            if (!Match(chat.Content, roulette.Trigger, out _)) continue;
            if (!HasPermission(roulette.Permission, chat.UserRoleCode)) return true;
            if (!Acquire($"roulette:{roulette.Id}:all", roulette.CooldownSeconds)) return true;
            if (!Acquire($"roulette:{roulette.Id}:user:{chat.SenderChannelId}", roulette.UserCooldownSeconds)) return true;

            await RunRouletteAsync(roulette, chat.Nickname, queueIfBusy: false);
            return true;
        }

        return false;
    }

    private async Task RunRouletteAsync(RouletteDefinition roulette, string user, bool queueIfBusy)
    {
        if (queueIfBusy)
            await _rouletteGate.WaitAsync();
        else if (!await _rouletteGate.WaitAsync(0))
        {
            Log?.Invoke("룰렛이 이미 진행 중입니다.");
            return;
        }

        try
        {
            var items = roulette.Items.Where(x => !string.IsNullOrWhiteSpace(x.Text) && x.ChancePercent > 0).ToList();
            var total = items.Sum(x => x.ChancePercent);

            if (items.Count == 0 || Math.Abs(total - 100) > 0.001)
            {
                Log?.Invoke($"{roulette.Name}: 확률 합계가 100%인지 확인하세요.");
                return;
            }

            var finalCandidates = items.Where(x => !x.IsReroll).ToList();
            if (finalCandidates.Count == 0)
            {
                Log?.Invoke($"{roulette.Name}: '한 번 더'가 아닌 최종 결과가 최소 1개 필요합니다.");
                return;
            }

            RouletteItem? finalResult = null;

            for (var rerollCount = 0; rerollCount < 20; rerollCount++)
            {
                var selected = items[PickWeightedIndex(items)];
                var index = items.IndexOf(selected);

                await _rouletteOverlay(new RouletteResult(
                    roulette.Id,
                    roulette.Name,
                    selected.Text,
                    index,
                    user,
                    selected.IsReroll));

                Log?.Invoke($"룰렛 회전: {roulette.Name} / {user} -> {selected.Text}");
                await Task.Delay(RouletteSpinDuration);

                if (!selected.IsReroll)
                {
                    finalResult = selected;
                    break;
                }

                Log?.Invoke($"{roulette.Name}: 한 번 더 -> 자동 재추첨");
                await Task.Delay(RouletteRerollDelay);
            }

            if (finalResult is null)
            {
                finalResult = finalCandidates[PickWeightedIndex(finalCandidates)];
                var index = items.IndexOf(finalResult);

                await _rouletteOverlay(new RouletteResult(
                    roulette.Id,
                    roulette.Name,
                    finalResult.Text,
                    index,
                    user,
                    false));

                await Task.Delay(RouletteSpinDuration);
            }

            var response = string.IsNullOrWhiteSpace(roulette.Response)
                ? "[룰렛] {user} -> {result}"
                : roulette.Response;

            var finalText = response
                .Replace("{user}", user)
                .Replace("{result}", finalResult.Text)
                .Replace("{roulette}", roulette.Name);

            // Send the result only after the final spin animation ends.
            await _send(finalText);
            Log?.Invoke($"룰렛 최종 결과: {roulette.Name} / {user} -> {finalResult.Text}");
        }
        finally
        {
            _rouletteGate.Release();
        }
    }

    private static int PickWeightedIndex(IReadOnlyList<RouletteItem> items)
    {
        var total = items.Sum(x => x.ChancePercent);
        var roll = RandomNumberGenerator.GetInt32(0, 1_000_000) / 1_000_000.0 * total;
        var sum = 0.0;

        for (var i = 0; i < items.Count; i++)
        {
            sum += items[i].ChancePercent;
            if (roll < sum) return i;
        }

        return items.Count - 1;
    }

    private async Task<bool> TryCounterAsync(ChatEvent chat)
    {
        foreach (var counter in _store.Data.Counters.Where(x => x.Enabled))
        {
            if (!Match(chat.Content, counter.Trigger, out _)) continue;
            if (!HasPermission(counter.Permission, chat.UserRoleCode)) return true;

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
            foreach (var key in _outgoing.Where(x => x.Value <= now).Select(x => x.Key).ToArray())
                _outgoing.Remove(key);

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
        if (seconds <= 0) return true;

        lock (_sync)
        {
            var now = DateTime.UtcNow;
            if (_cooldowns.TryGetValue(key, out var until) && until > now)
                return false;

            _cooldowns[key] = now.AddSeconds(seconds);
            return true;
        }
    }

    private static bool Match(string content, string trigger, out string args)
    {
        args = "";
        content = content.Trim();
        trigger = trigger.Trim();

        if (string.IsNullOrWhiteSpace(trigger)) return false;
        if (content.Equals(trigger, StringComparison.OrdinalIgnoreCase)) return true;

        if (content.StartsWith(trigger + " ", StringComparison.OrdinalIgnoreCase))
        {
            args = content[(trigger.Length + 1)..].Trim();
            return true;
        }

        return false;
    }

    private static bool HasPermission(string permission, string role)
    {
        var p = permission.Trim().ToLowerInvariant();
        var r = role.Trim().ToLowerInvariant();

        return p switch
        {
            "" or "전체" or "everyone" => true,
            "매니저" or "manager" => r is "streamer" or "streaming_channel_manager" or "streaming_chat_manager",
            "스트리머" or "streamer" => r == "streamer",
            _ => false
        };
    }
}
