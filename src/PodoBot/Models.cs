using System.Collections.ObjectModel;

namespace PodoBot;

public sealed class AppData
{
    public int DataVersion { get; set; } = 5;
    public ObservableCollection<BotCommand> Commands { get; set; } = new();
    public ObservableCollection<RouletteDefinition> Roulettes { get; set; } = new();
    public ObservableCollection<DonationRouletteRule> DonationRules { get; set; } = new();
    public ObservableCollection<RepeatingMessageConfig> RepeatingMessages { get; set; } = new();
    public ObservableCollection<CountdownTimerConfig> CountdownTimers { get; set; } = new();
    public ObservableCollection<CounterConfig> Counters { get; set; } = new();
    public ObservableCollection<SongBookEntry> Songs { get; set; } = new();

    // Legacy v0.3.x fields. Kept only for migration.
    public ObservableCollection<RouletteItem> RouletteItems { get; set; } = new();
    public RouletteSettings Roulette { get; set; } = new();
    public ObservableCollection<TimerConfig> Timers { get; set; } = new();

    public static AppData Default()
    {
        var roulette = new RouletteDefinition
        {
            Name = "일반 룰렛",
            Trigger = "!룰렛",
            Response = "[룰렛] {user} -> {result}",
            Permission = "전체"
        };

        roulette.Items.Add(new RouletteItem { Text = "꽝", ChancePercent = 50 });
        roulette.Items.Add(new RouletteItem { Text = "물 마시기", ChancePercent = 25 });
        roulette.Items.Add(new RouletteItem { Text = "한 번 더", ChancePercent = 25, IsReroll = true });

        var data = new AppData();
        data.Commands.Add(new BotCommand
        {
            Trigger = "!공지",
            Response = "오늘 방송도 재밌게 봐주세요!",
            Permission = "전체",
            Enabled = true
        });
        data.Roulettes.Add(roulette);
        data.CountdownTimers.Add(new CountdownTimerConfig
        {
            Name = "10분 타이머",
            Minutes = 10,
            FinishMessage = "타이머가 종료되었습니다."
        });
        data.Counters.Add(new CounterConfig
        {
            Name = "사망",
            Trigger = "!사망",
            Permission = "매니저",
            Enabled = false
        });
        return data;
    }
}

public sealed class BotCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string Trigger { get; set; } = "";
    public string Response { get; set; } = "";
    public int CooldownSeconds { get; set; } = 5;
    public int UserCooldownSeconds { get; set; } = 10;
    public string Permission { get; set; } = "전체";
}

public sealed class RouletteDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "새 룰렛";
    public string Trigger { get; set; } = "!룰렛";
    public string Response { get; set; } = "[룰렛] {user} -> {result}";
    public int CooldownSeconds { get; set; }
    public int UserCooldownSeconds { get; set; }
    public string Permission { get; set; } = "전체";
    public ObservableCollection<RouletteItem> Items { get; set; } = new();
}

public sealed class RouletteItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public double ChancePercent { get; set; }
    public bool IsReroll { get; set; }
}

public sealed class DonationRouletteRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public long MinAmount { get; set; } = 2000;
    public string Keyword { get; set; } = "";
    public Guid RouletteId { get; set; }
}

public sealed class RepeatingMessageConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string Message { get; set; } = "";
    public double IntervalMinutes { get; set; } = 30;
}

public sealed class CountdownTimerConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "새 타이머";
    public int Hours { get; set; }
    public int Minutes { get; set; } = 5;
    public int Seconds { get; set; }
    public string FinishMessage { get; set; } = "타이머가 종료되었습니다.";

    public TimeSpan GetDuration()
    {
        return TimeSpan.FromHours(Math.Max(0, Hours))
               + TimeSpan.FromMinutes(Math.Max(0, Minutes))
               + TimeSpan.FromSeconds(Math.Max(0, Seconds));
    }
}

public sealed class CounterConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public string Trigger { get; set; } = "";
    public long Value { get; set; }
    public int Step { get; set; } = 1;
    public string Permission { get; set; } = "매니저";
}

public sealed class SongBookEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Provider { get; set; } = "TJ";
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
}

public sealed class ChatEvent
{
    public string ChannelId { get; set; } = "";
    public string SenderChannelId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string UserRoleCode { get; set; } = "";
    public string Content { get; set; } = "";
    public long MessageTime { get; set; }
}

public sealed class DonationEvent
{
    public string ChannelId { get; set; } = "";
    public string DonatorChannelId { get; set; } = "";
    public string DonatorNickname { get; set; } = "";
    public long PayAmount { get; set; }
    public string DonationText { get; set; } = "";
    public string DonationType { get; set; } = "";
}

public sealed class AuthTokens
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; } = DateTime.MinValue;
    public string ChannelId { get; set; } = "";
    public string ChannelName { get; set; } = "";
}

public sealed record TokenResult(string AccessToken, string RefreshToken, int ExpiresInSeconds);
public sealed record MeResult(string ChannelId, string ChannelName);
public sealed record RouletteResult(Guid RouletteId, string RouletteName, string Text, int Index, string User, bool IsReroll);

// Legacy v0.3.x models.
public sealed class RouletteSettings
{
    public bool Enabled { get; set; } = true;
    public string Trigger { get; set; } = "!룰렛";
    public string Response { get; set; } = "[룰렛] {user} -> {result}";
    public int CooldownSeconds { get; set; } = 10;
    public int UserCooldownSeconds { get; set; } = 30;
    public string Permission { get; set; } = "전체";
}

public sealed class TimerConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string Message { get; set; } = "";
    public double IntervalMinutes { get; set; } = 30;
}
