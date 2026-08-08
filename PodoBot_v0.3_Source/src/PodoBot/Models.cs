namespace PodoBot;

public sealed class AppData
{
    public List<BotCommand> Commands { get; set; } = new();
    public List<RouletteItem> RouletteItems { get; set; } = new();
    public RouletteSettings Roulette { get; set; } = new();
    public List<TimerConfig> Timers { get; set; } = new();
    public List<CounterConfig> Counters { get; set; } = new();

    public static AppData Default()
    {
        return new AppData
        {
            Commands =
            [
                new BotCommand
                {
                    Trigger = "!공지",
                    Response = "오늘 방송도 재밌게 봐주세요!",
                    Permission = "전체",
                    Enabled = false
                }
            ],
            RouletteItems =
            [
                new RouletteItem { Text = "꽝", ChancePercent = 50 },
                new RouletteItem { Text = "물 마시기", ChancePercent = 25 },
                new RouletteItem { Text = "한 번 더", ChancePercent = 25 }
            ],
            Roulette = new RouletteSettings(),
            Timers = [],
            Counters =
            [
                new CounterConfig
                {
                    Name = "사망",
                    Trigger = "!사망",
                    Permission = "매니저",
                    Enabled = false
                }
            ]
        };
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

public sealed class RouletteItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public double ChancePercent { get; set; }
}

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

public sealed class ChatEvent
{
    public string ChannelId { get; set; } = "";
    public string SenderChannelId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string UserRoleCode { get; set; } = "";
    public string Content { get; set; } = "";
    public long MessageTime { get; set; }
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
public sealed record RouletteResult(string Text, int Index, string User);
