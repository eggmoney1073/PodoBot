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

            return data ?? AppData.Default();
        }
        catch
        {
            return AppData.Default();
        }
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
