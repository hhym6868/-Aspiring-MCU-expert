using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DreamMcuToolbox.Models;

namespace DreamMcuToolbox.Services;

public sealed class LaunchService
{
    public bool Launch(ToolViewModel tool)
    {
        try
        {
            if (!tool.IsInstalled || string.IsNullOrWhiteSpace(tool.LocalPath))
            {
                if (!string.IsNullOrWhiteSpace(tool.DownloadUrl))
                {
                    OpenUrl(tool.DownloadUrl);
                    return true;
                }
                return false;
            }

            var path = tool.LocalPath!;
            if (!File.Exists(path) && !Directory.Exists(path))
                return false;

            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                OpenUrl(path);
                return true;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? AppPaths.BaseDirectory,
                Arguments = tool.Definition.Arguments ?? string.Empty,
                UseShellExecute = true
            };

            if (tool.Definition.RequiresAdmin && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                startInfo.Verb = "runas";

            Process.Start(startInfo);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 打不开网址时保持界面可用。
        }
    }

    public void OpenFolder(string? path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                return;
            }

            var parent = path is not null ? Path.GetDirectoryName(path) : null;
            if (parent is not null && Directory.Exists(parent))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{parent}\"") { UseShellExecute = true });
        }
        catch
        {
            // 忽略资源管理器启动失败。
        }
    }

    public void OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // 忽略文档打开失败。
        }
    }
}

