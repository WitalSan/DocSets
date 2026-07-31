using Newtonsoft.Json;
using System.Drawing;

namespace DocSets.Desktop;

internal sealed class DesktopSettings
{
    public int X { get; set; } = 80;
    public int Y { get; set; } = 80;
    public int Width { get; set; } = 1400;
    public int Height { get; set; } = 900;
    public FormWindowState WindowState { get; set; } = FormWindowState.Normal;
    public List<string> RecentDocSets { get; set; } = new();

    [JsonIgnore]
    public Rectangle Bounds => new(X, Y, Math.Max(700, Width), Math.Max(500, Height));
}

internal sealed class DesktopSettingsStore
{
    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DocSets", "Desktop");

    public string SettingsPath => Path.Combine(_directory, "settings.json");
    public string LayoutPath => Path.Combine(_directory, "layout.xml");

    public DesktopSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new DesktopSettings();
            return JsonConvert.DeserializeObject<DesktopSettings>(File.ReadAllText(SettingsPath))
                ?? new DesktopSettings();
        }
        catch (Exception exception)
        {
            DocSetsLog.Current.Error("Настройки", "Не удалось загрузить настройки Desktop.", exception);
            return new DesktopSettings();
        }
    }

    public void Save(DesktopSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(SettingsPath,
                JsonConvert.SerializeObject(settings, Formatting.Indented));
        }
        catch (Exception exception)
        {
            DocSetsLog.Current.Error("Настройки", "Не удалось сохранить настройки Desktop.", exception);
        }
    }

    public void DeleteLayout()
    {
        try
        {
            if (File.Exists(LayoutPath)) File.Delete(LayoutPath);
        }
        catch (Exception exception)
        {
            DocSetsLog.Current.Error("Layout", "Не удалось удалить layout.", exception);
        }
    }
}
