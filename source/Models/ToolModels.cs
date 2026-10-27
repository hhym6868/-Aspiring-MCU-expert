using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;

namespace DreamMcuToolbox.Models;

public sealed class ToolDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
    public string Homepage { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public string? SystemPath { get; set; }
    public string? LocalPath { get; set; }
    public string? Arguments { get; set; }
    public bool RequiresAdmin { get; set; }
    public string Glyph { get; set; } = "\uE7C3";
    public string Accent { get; set; } = "#2DD4BF";
    public bool IsUserTool { get; set; }
    public List<string> ExecutableNames { get; set; } = new();
    public List<string> DetectNames { get; set; } = new();
    public List<string> Tags { get; set; } = new();
}

public sealed class CatalogFile
{
    public string Version { get; set; } = "1.0";
    public string Title { get; set; } = "梦想成为单片机大手";
    public List<ToolDefinition> Tools { get; set; } = new();
}

public sealed class AppSettings
{
    public List<string> Favorites { get; set; } = new();
    public List<string> Recent { get; set; } = new();
    public Dictionary<string, string> CustomPaths { get; set; } = new();
    public string LastCategory { get; set; } = "全部工具";
    public bool OnlyLocal { get; set; }
}

public sealed class CategoryNav : INotifyPropertyChanged
{
    private int _count;

    public string Name { get; init; } = string.Empty;
    public string Glyph { get; init; } = "\uE8FD";
    public bool IsSpecial { get; init; }
    public string FilterKey { get; init; } = string.Empty;

    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ToolViewModel : INotifyPropertyChanged
{
    private string? _localPath;
    private bool _isFavorite;
    private ImageSource? _iconImage;

    public ToolViewModel(ToolDefinition definition)
    {
        Definition = definition;
        var accent = CreateBrush(definition.Accent);
        AccentBrush = accent;
        CategoryBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(
            28, accent.Color.R, accent.Color.G, accent.Color.B));
    }

    public ToolDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public string Category => Definition.Category;
    public string Summary => Definition.Summary;
    public string Description => Definition.Description;
    public string Vendor => Definition.Vendor;
    public string License => Definition.License;
    public string Homepage => Definition.Homepage;
    public string DownloadUrl => Definition.DownloadUrl;
    public string Glyph => Definition.Glyph;
    public string TagsText => string.Join("  ·  ", Definition.Tags);
    public Brush AccentBrush { get; }
    public Brush CategoryBrush { get; }
    public IReadOnlyList<string> Tags => Definition.Tags;

    public string? LocalPath
    {
        get => _localPath;
        private set
        {
            if (string.Equals(_localPath, value, StringComparison.OrdinalIgnoreCase)) return;
            _localPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(ActionLabel));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusDetail));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(SourceBrush));
        }
    }

    public ImageSource? IconImage
    {
        get => _iconImage;
        private set
        {
            if (ReferenceEquals(_iconImage, value)) return;
            _iconImage = value;
            OnPropertyChanged();
        }
    }

    public bool IsInstalled => !string.IsNullOrWhiteSpace(LocalPath);
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FavoriteGlyph));
            OnPropertyChanged(nameof(FavoriteBrush));
        }
    }

    public string FavoriteGlyph => IsFavorite ? "\uE735" : "\uE734";
    public Brush FavoriteBrush => IsFavorite ? new SolidColorBrush(Color.FromRgb(255, 183, 77)) : new SolidColorBrush(Color.FromRgb(96, 113, 132));
    public string ActionLabel => IsInstalled ? "启动" : "获取";
    public string StatusLabel => IsInstalled ? "本地可用" : (string.IsNullOrWhiteSpace(DownloadUrl) ? "未检测到" : "官方获取");
    public string StatusDetail => IsInstalled ? "已就绪，可直接启动" : "未放入本地文件，可通过官网获取";
    public Brush StatusBrush => IsInstalled ? new SolidColorBrush(Color.FromRgb(45, 212, 191)) : new SolidColorBrush(Color.FromRgb(117, 135, 154));
    public Brush SourceBrush => IsInstalled ? new SolidColorBrush(Color.FromRgb(45, 212, 191)) : new SolidColorBrush(Color.FromRgb(143, 162, 183));

    public void SetLocalPath(string? path, bool loadIcon = true)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? null : path;
        LocalPath = normalized;
        if (loadIcon && normalized is not null)
            TryLoadIcon(normalized);
    }

    public void TryLoadIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            var info = new ShellFileInfo();
            var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(),
                ShellIconLarge | ShellIconUseFileAttributes);
            if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero) return;

            try
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    info.IconHandle,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(64, 64));
                source.Freeze();
                IconImage = source;
            }
            finally
            {
                DestroyIcon(info.IconHandle);
            }
        }
        catch
        {
            // 无法读取图标时保留分类图标作为后备。
        }
    }

    private const uint ShellIconLarge = 0x000000100;
    private const uint ShellIconUseFileAttributes = 0x000000010;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    private static SolidColorBrush CreateBrush(string hex)
    {
        try
        {
            return (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        }
        catch
        {
            return new SolidColorBrush(Color.FromRgb(45, 212, 191));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}




