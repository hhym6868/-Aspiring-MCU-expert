using System.IO;
using System.Text;
using DreamMcuToolbox.Models;
using Microsoft.Win32;

namespace DreamMcuToolbox.Services;

public sealed class InstalledAppScanner
{
    public Dictionary<string, string> Scan(IReadOnlyList<ToolDefinition> tools)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var registryEntries = ReadUninstallEntries().ToList();
        var appPaths = ReadAppPaths();

        foreach (var tool in tools)
        {
            var path = FindExistingPath(tool, registryEntries, appPaths);
            if (!string.IsNullOrWhiteSpace(path))
                results[tool.Id] = path;
        }

        return results;
    }

    private static string? FindExistingPath(
        ToolDefinition tool,
        IReadOnlyList<InstalledEntry> registryEntries,
        IReadOnlyDictionary<string, string> appPaths)
    {
        // 用户手动添加的工具优先。
        if (!string.IsNullOrWhiteSpace(tool.LocalPath) && File.Exists(tool.LocalPath))
            return tool.LocalPath;

        if (!string.IsNullOrWhiteSpace(tool.SystemPath))
        {
            var expandedSystemPath = Environment.ExpandEnvironmentVariables(tool.SystemPath);
            if (File.Exists(expandedSystemPath))
                return expandedSystemPath;
        }

        var local = FindInToolFolder(tool);
        if (local is not null)
            return local;

        foreach (var executableName in tool.ExecutableNames)
        {
            if (appPaths.TryGetValue(executableName, out var appPath) && File.Exists(appPath))
                return appPath;
        }

        foreach (var entry in registryEntries)
        {
            if (!MatchesDetectName(tool, entry.DisplayName))
                continue;

            var iconPath = ExtractExecutablePath(entry.DisplayIcon);
            if (iconPath is not null && File.Exists(iconPath))
                return iconPath;

            var fromInstallFolder = FindExecutableInFolder(entry.InstallLocation, tool.ExecutableNames);
            if (fromInstallFolder is not null)
                return fromInstallFolder;
        }

        return null;
    }

    private static bool MatchesDetectName(ToolDefinition tool, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return false;

        var names = tool.DetectNames.Count > 0 ? tool.DetectNames : new List<string> { tool.Name };
        return names.Any(name => !string.IsNullOrWhiteSpace(name) &&
                                 displayName.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindInToolFolder(ToolDefinition tool)
    {
        if (string.IsNullOrWhiteSpace(tool.Folder))
            return null;

        var folder = Path.Combine(AppPaths.ToolsDirectory, tool.Folder.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(folder))
            return null;

        foreach (var exeName in tool.ExecutableNames)
        {
            try
            {
                var matched = Directory.EnumerateFiles(folder, exeName, SearchOption.AllDirectories)
                    .OrderBy(p => p.Length)
                    .FirstOrDefault();
                if (matched is not null)
                    return matched;
            }
            catch
            {
                // 遇到无权限子目录时继续尝试其他名称。
            }
        }

        try
        {
            return Directory.EnumerateFiles(folder, "*.exe", SearchOption.AllDirectories)
                .Where(p => !Path.GetFileName(p).Contains("unins", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Length)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? FindExecutableInFolder(string? installLocation, IReadOnlyList<string> executableNames)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
            return null;

        var folder = Environment.ExpandEnvironmentVariables(installLocation.Trim().Trim('"'));
        if (!Directory.Exists(folder))
            return null;

        foreach (var name in executableNames)
        {
            var direct = Path.Combine(folder, name);
            if (File.Exists(direct))
                return direct;

            try
            {
                var nested = Directory.EnumerateFiles(folder, name, SearchOption.AllDirectories)
                    .OrderBy(p => p.Length)
                    .FirstOrDefault();
                if (nested is not null)
                    return nested;
            }
            catch
            {
                // 忽略不可访问目录。
            }
        }

        return null;
    }

    private static string? ExtractExecutablePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (value.StartsWith('"'))
        {
            var closing = value.IndexOf('"', 1);
            if (closing > 1)
                value = value[1..closing];
        }
        else
        {
            var comma = value.LastIndexOf(',');
            if (comma > 2 && int.TryParse(value[(comma + 1)..].Trim(), out _))
                value = value[..comma];
            value = value.Trim().Trim('"');
        }

        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(value)
            ? value
            : null;
    }

    private static IEnumerable<InstalledEntry> ReadUninstallEntries()
    {
        var roots = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.CurrentUser, RegistryView.Registry32)
        };

        foreach (var (hive, view) in roots)
        {
            RegistryKey? baseKey = null;
            RegistryKey? uninstallKey = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstallKey is null) continue;

                foreach (var subName in uninstallKey.GetSubKeyNames())
                {
                    RegistryKey? item = null;
                    try
                    {
                        item = uninstallKey.OpenSubKey(subName);
                        if (item is null) continue;

                        var displayName = item.GetValue("DisplayName") as string ?? string.Empty;
                        if (displayName.Length == 0) continue;

                        yield return new InstalledEntry(
                            displayName,
                            item.GetValue("DisplayIcon") as string,
                            item.GetValue("InstallLocation") as string);
                    }
                    finally
                    {
                        item?.Dispose();
                    }
                }
            }
            finally
            {
                uninstallKey?.Dispose();
                baseKey?.Dispose();
            }
        }
    }

    private static Dictionary<string, string> ReadAppPaths()
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.CurrentUser, RegistryView.Registry32)
        };

        foreach (var (hive, view) in roots)
        {
            RegistryKey? baseKey = null;
            RegistryKey? appPathsKey = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                appPathsKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (appPathsKey is null) continue;

                foreach (var subName in appPathsKey.GetSubKeyNames())
                {
                    RegistryKey? item = null;
                    try
                    {
                        item = appPathsKey.OpenSubKey(subName);
                        var value = item?.GetValue(null) as string;
                        if (string.IsNullOrWhiteSpace(value)) continue;

                        var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                        if (File.Exists(expanded))
                            results[subName] = expanded;
                    }
                    finally
                    {
                        item?.Dispose();
                    }
                }
            }
            finally
            {
                appPathsKey?.Dispose();
                baseKey?.Dispose();
            }
        }

        return results;
    }

    private sealed record InstalledEntry(string DisplayName, string? DisplayIcon, string? InstallLocation);
}

