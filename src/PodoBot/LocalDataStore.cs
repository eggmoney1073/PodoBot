using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace PodoBot;

public sealed class LocalDataStore
{
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string DirectoryPath { get; }
    public string DataPath { get; }
    public AppData Data { get; private set; }

    public LocalDataStore()
    {
        DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PodoBot");
        DataPath = Path.Combine(DirectoryPath, "settings.json");
        Directory.CreateDirectory(DirectoryPath);
        Data = Load();
    }

    private AppData Load()
    {
        try
        {
            if (!File.Exists(DataPath))
                return AppData.Default();

            var data = JsonSerializer.Deserialize<AppData>(
                File.ReadAllText(DataPath),
                _json);

            if (data is null)
                return AppData.Default();

            Migrate(data);
            return data;
        }
        catch
        {
            return AppData.Default();
        }
    }

    private static void Migrate(AppData data)
    {
        if (data.Roulettes.Count == 0)
        {
            var old = data.Roulette ?? new RouletteSettings();
            var cooldown = old.CooldownSeconds;
            var userCooldown = old.UserCooldownSeconds;

            if (cooldown == 10 && userCooldown == 30)
            {
                cooldown = 0;
                userCooldown = 0;
            }

            var items = new ObservableCollection<RouletteItem>();

            if (data.RouletteItems.Count > 0)
            {
                foreach (var item in data.RouletteItems)
                {
                    var normalized = new string(
                        item.Text.Where(c => !char.IsWhiteSpace(c)).ToArray());

                    if (normalized.Equals("한번더", StringComparison.OrdinalIgnoreCase))
                        item.IsReroll = true;

                    items.Add(item);
                }
            }
            else
            {
                items.Add(new RouletteItem { Text = "꽝", ChancePercent = 50 });
                items.Add(new RouletteItem { Text = "물 마시기", ChancePercent = 25 });
                items.Add(new RouletteItem { Text = "한 번 더", ChancePercent = 25, IsReroll = true });
            }

            data.Roulettes.Add(new RouletteDefinition
            {
                Name = "일반 룰렛",
                Enabled = old.Enabled,
                Trigger = old.Trigger,
                Response = old.Response,
                CooldownSeconds = cooldown,
                UserCooldownSeconds = userCooldown,
                Permission = old.Permission,
                Items = items
            });
        }

        if (data.RepeatingMessages.Count == 0 && data.Timers.Count > 0)
        {
            foreach (var item in data.Timers)
            {
                data.RepeatingMessages.Add(new RepeatingMessageConfig
                {
                    Id = item.Id,
                    Enabled = item.Enabled,
                    Message = item.Message,
                    IntervalMinutes = item.IntervalMinutes
                });
            }
        }

        if (data.CountdownTimers.Count == 0)
        {
            data.CountdownTimers.Add(new CountdownTimerConfig
            {
                Name = "10분 타이머",
                Minutes = 10,
                FinishMessage = "타이머가 종료되었습니다."
            });
        }

        if (data.DataVersion < 2)
        {
            var sample = data.Commands.FirstOrDefault(x =>
                x.Trigger == "!공지"
                && x.Response == "오늘 방송도 재밌게 봐주세요!"
                && x.Permission == "전체");

            if (sample is not null)
                sample.Enabled = true;
        }

        data.DataVersion = 5;
    }

    public async Task SaveAsync()
    {
        await _gate.WaitAsync();

        try
        {
            var temp = DataPath + ".tmp";
            await File.WriteAllTextAsync(
                temp,
                JsonSerializer.Serialize(Data, _json));
            File.Move(temp, DataPath, true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
