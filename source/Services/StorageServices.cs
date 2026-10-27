using System.IO;
using System.Text.Json;
using DreamMcuToolbox.Models;

namespace DreamMcuToolbox.Services;

public static class AppPaths
{
    public static string BaseDirectory { get; } = AppContext.BaseDirectory;
    public static string ToolsDirectory { get; } = Path.Combine(BaseDirectory, "tools");
    public static string DataDirectory { get; } = Path.Combine(BaseDirectory, "data");
    public static string CatalogFile { get; } = Path.Combine(BaseDirectory, "catalog.json");
    public static string UserToolsFile { get; } = Path.Combine(DataDirectory, "user_tools.json");
    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");
    public static string ReadmeFile { get; } = Path.Combine(BaseDirectory, "README.txt");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(ToolsDirectory);
        Directory.CreateDirectory(DataDirectory);

        foreach (var category in new[] { "firmware", "terminal", "development", "debug", "driver", "utility" })
            Directory.CreateDirectory(Path.Combine(ToolsDirectory, category));
    }
}

public sealed class CatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public CatalogFile LoadCatalog()
    {
        if (!File.Exists(AppPaths.CatalogFile))
            throw new FileNotFoundException("找不到 catalog.json。请确认它和主程序放在同一目录。", AppPaths.CatalogFile);

        var json = File.ReadAllText(AppPaths.CatalogFile);
        var catalog = JsonSerializer.Deserialize<CatalogFile>(json, JsonOptions)
                      ?? throw new InvalidDataException("catalog.json 内容为空或格式不正确。");

        catalog.Tools ??= new List<ToolDefinition>();
        foreach (var tool in catalog.Tools)
        {
            tool.ExecutableNames ??= new List<string>();
            tool.DetectNames ??= new List<string>();
            tool.Tags ??= new List<string>();
        }

        return catalog;
    }

    public List<ToolDefinition> LoadUserTools()
    {
        if (!File.Exists(AppPaths.UserToolsFile))
            return new List<ToolDefinition>();

        try
        {
            var json = File.ReadAllText(AppPaths.UserToolsFile);
            return JsonSerializer.Deserialize<List<ToolDefinition>>(json, JsonOptions) ?? new List<ToolDefinition>();
        }
        catch
        {
            return new List<ToolDefinition>();
        }
    }

    public void SaveUserTools(IEnumerable<ToolDefinition> tools)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var json = JsonSerializer.Serialize(tools.ToList(), JsonOptions);
        File.WriteAllText(AppPaths.UserToolsFile, json);
    }
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        AllowTrailingCommas = true
    };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
                return new AppSettings();

            var json = File.ReadAllText(AppPaths.SettingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.Favorites ??= new List<string>();
            settings.Recent ??= new List<string>();
            settings.CustomPaths ??= new Dictionary<string, string>();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(AppPaths.SettingsFile, json);
        }
        catch
        {
            // 设置写入失败不应影响工具启动。
        }
    }
}



