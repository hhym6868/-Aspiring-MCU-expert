using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using DreamMcuToolbox.Models;
using DreamMcuToolbox.Services;
using Microsoft.Win32;

namespace DreamMcuToolbox;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly CatalogService _catalogService = new();
    private readonly SettingsService _settingsService = new();
    private readonly LaunchService _launchService = new();
    private readonly InstalledAppScanner _scanner = new();
    private readonly ObservableCollection<CategoryNav> _categories = new();
    private readonly ObservableCollection<ToolViewModel> _filteredTools = new();
    private readonly List<ToolViewModel> _allTools = new();
    private readonly List<ToolDefinition> _userTools = new();
    private AppSettings _settings = new();
    private CategoryNav? _activeCategory;
    private ToolViewModel? _selectedTool;
    private bool _initialized;
    private bool _scanning;
    private string _currentCategoryName = "全部工具";
    private int _resultCount;
    private string _scanStatusText = "正在准备工具目录…";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Closing += Window_Closing;
        SourceInitialized += Window_SourceInitialized;
    }

    public ObservableCollection<CategoryNav> Categories => _categories;
    public ObservableCollection<ToolViewModel> FilteredTools => _filteredTools;

    public ToolViewModel? SelectedTool
    {
        get => _selectedTool;
        set
        {
            if (ReferenceEquals(_selectedTool, value)) return;
            _selectedTool = value;
            DetailPlaceholderBorder.Visibility = value is null ? Visibility.Visible : Visibility.Collapsed;
            OnPropertyChanged();
        }
    }

    public string CurrentCategoryName
    {
        get => _currentCategoryName;
        private set
        {
            if (_currentCategoryName == value) return;
            _currentCategoryName = value;
            OnPropertyChanged();
        }
    }

    public int ResultCount
    {
        get => _resultCount;
        private set
        {
            if (_resultCount == value) return;
            _resultCount = value;
            OnPropertyChanged();
        }
    }

    public string ScanStatusText
    {
        get => _scanStatusText;
        private set
        {
            if (_scanStatusText == value) return;
            _scanStatusText = value;
            OnPropertyChanged();
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureCreated();
            _settings = _settingsService.Load();
            var catalog = _catalogService.LoadCatalog();
            _userTools.AddRange(_catalogService.LoadUserTools());

            var definitions = new List<ToolDefinition>();
            definitions.AddRange(catalog.Tools.Where(t => !string.IsNullOrWhiteSpace(t.Id)));
            definitions.AddRange(_userTools.Where(t => !string.IsNullOrWhiteSpace(t.Id)));

            // 允许用户为清单中的工具指定本机位置。
            foreach (var tool in definitions)
            {
                if (_settings.CustomPaths.TryGetValue(tool.Id, out var customPath) &&
                    (File.Exists(customPath) || Directory.Exists(customPath)))
                {
                    tool.LocalPath = customPath;
                }

                var vm = new ToolViewModel(tool)
                {
                    IsFavorite = _settings.Favorites.Contains(tool.Id, StringComparer.OrdinalIgnoreCase)
                };
                _allTools.Add(vm);
            }

            BuildCategories();
            OnlyLocalCheck.IsChecked = _settings.OnlyLocal;
            _initialized = true;

            var selectedCategory = _categories.FirstOrDefault(c => c.FilterKey == _settings.LastCategory)
                                   ?? _categories.First();
            CategoryList.SelectedItem = selectedCategory;
            ApplyFilter();
            SelectFirstVisibleTool();

            await ScanAsync();
        }
        catch (Exception ex)
        {
            _initialized = false;
            ScanStatusText = "工具清单读取失败";
            MessageBox.Show(
                $"工具箱初始化失败：\n{ex.Message}\n\n请确认 catalog.json 与主程序位于同一目录。",
                "梦想成为单片机大手",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void BuildCategories()
    {
        AddCategory("全部工具", "\uE8FD", "__all__", true);
        AddCategory("收藏", "\uE735", "__favorites__", true);
        AddCategory("最近使用", "\uE81C", "__recent__", true);
        AddCategory("本地可用", "\uE73E", "__local__", true);
        AddCategory("固件烧录", "\uE950", "固件烧录");
        AddCategory("串口终端", "\uE8C2", "串口终端");
        AddCategory("开发环境", "\uE943", "开发环境");
        AddCategory("调试分析", "\uE9D9", "调试分析");
        AddCategory("驱动与检测", "\uE8B7", "驱动与检测");
        AddCategory("实用工具", "\uE7C3", "实用工具");
        AddCategory("我的工具", "\uE710", "__mine__", true);
        UpdateCategoryCounts();
    }

    private void AddCategory(string name, string glyph, string filterKey, bool isSpecial = false)
    {
        _categories.Add(new CategoryNav
        {
            Name = name,
            Glyph = glyph,
            FilterKey = filterKey,
            IsSpecial = isSpecial
        });
    }

    private void UpdateCategoryCounts()
    {
        foreach (var category in _categories)
        {
            category.Count = category.FilterKey switch
            {
                "__all__" => _allTools.Count,
                "__favorites__" => _allTools.Count(t => t.IsFavorite),
                "__recent__" => _settings.Recent.Count(id => _allTools.Any(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase))),
                "__local__" => _allTools.Count(t => t.IsInstalled),
                "__mine__" => _allTools.Count(t => t.Definition.IsUserTool),
                _ => _allTools.Count(t => t.Category.Equals(category.FilterKey, StringComparison.OrdinalIgnoreCase))
            };
        }
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || CategoryList.SelectedItem is not CategoryNav category) return;
        _activeCategory = category;
        ApplyFilter();
        SelectFirstVisibleTool();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized) ApplyFilter();
    }

    private void FilterOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.OnlyLocal = OnlyLocalCheck.IsChecked == true;
        _settingsService.Save(_settings);
        ApplyFilter();
        SelectFirstVisibleTool();
    }

    private void ApplyFilter()
    {
        if (_activeCategory is null) return;

        IEnumerable<ToolViewModel> query = _allTools;
        var filterKey = _activeCategory.FilterKey;

        if (filterKey == "__favorites__")
            query = query.Where(t => t.IsFavorite);
        else if (filterKey == "__local__")
            query = query.Where(t => t.IsInstalled);
        else if (filterKey == "__mine__")
            query = query.Where(t => t.Definition.IsUserTool);
        else if (filterKey == "__recent__")
            query = query.Where(t => _settings.Recent.Contains(t.Id, StringComparer.OrdinalIgnoreCase));
        else if (filterKey != "__all__")
            query = query.Where(t => t.Category.Equals(filterKey, StringComparison.OrdinalIgnoreCase));

        if (OnlyLocalCheck.IsChecked == true)
            query = query.Where(t => t.IsInstalled);

        var terms = (SearchBox.Text ?? string.Empty)
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (terms.Length > 0)
        {
            query = query.Where(t => terms.All(term =>
            {
                var haystack = string.Join(" ",
                    t.Name, t.Category, t.Summary, t.Description, t.Vendor, t.License,
                    string.Join(" ", t.Definition.Tags));
                return haystack.Contains(term, StringComparison.OrdinalIgnoreCase);
            }));
        }

        var result = query.ToList();
        if (filterKey == "__recent__")
        {
            result = result
                .OrderBy(t =>
                {
                    var index = _settings.Recent.FindIndex(id => id.Equals(t.Id, StringComparison.OrdinalIgnoreCase));
                    return index < 0 ? int.MaxValue : index;
                })
                .ToList();
        }

        var previousId = SelectedTool?.Id;
        _filteredTools.Clear();
        foreach (var tool in result)
            _filteredTools.Add(tool);

        CurrentCategoryName = _activeCategory.Name;
        ResultCount = result.Count;
        UpdateCategoryCounts();

        var keep = result.FirstOrDefault(t => t.Id.Equals(previousId, StringComparison.OrdinalIgnoreCase));
        if (keep is not null)
        {
            ToolList.SelectedItem = keep;
            SelectedTool = keep;
        }
        else if (result.Count == 0)
        {
            ToolList.SelectedItem = null;
            SelectedTool = null;
        }

        EmptyStateBorder.Visibility = result.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectFirstVisibleTool();
    }

    private void SelectFirstVisibleTool()
    {
        if (_filteredTools.Count > 0 && ToolList.SelectedItem is null)
        {
            ToolList.SelectedIndex = 0;
            SelectedTool = _filteredTools[0];
        }
    }

    private void ToolList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedTool = ToolList.SelectedItem as ToolViewModel;
    }

    private void ToolList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<Button>(e.OriginalSource as DependencyObject) is not null) return;
        if (SelectedTool is not null)
        {
            ExecuteTool(SelectedTool);
            e.Handled = true;
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private async Task ScanAsync()
    {
        if (_scanning || _allTools.Count == 0) return;
        _scanning = true;
        ScanStatusText = "正在扫描本机软件…";

        try
        {
            var definitions = _allTools.Select(t => t.Definition).ToList();
            var result = await Task.Run(() => _scanner.Scan(definitions));

            foreach (var tool in _allTools)
            {
                result.TryGetValue(tool.Id, out var path);
                tool.SetLocalPath(path);
            }

            UpdateCategoryCounts();
            ApplyFilter();
            SelectFirstVisibleTool();
            var localCount = _allTools.Count(t => t.IsInstalled);
            ScanStatusText = localCount > 0
                ? $"已识别 {localCount} 个本地工具"
                : "尚未发现本地工具 · 可从官网获取";
        }
        catch
        {
            ScanStatusText = "扫描失败，可稍后重试";
        }
        finally
        {
            _scanning = false;
        }
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        await ScanAsync();
    }

    private void AddLocal_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要加入工具箱的本地程序",
            Filter = "Windows 程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true) return;

        var path = Path.GetFullPath(dialog.FileName);
        var existing = _allTools.FirstOrDefault(t =>
            t.LocalPath is not null && Path.GetFullPath(t.LocalPath).Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectTool(existing);
            ScanStatusText = "该程序已经在工具箱中";
            return;
        }

        FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
        var productName = (version.ProductName ?? string.Empty).Trim();
        var name = string.IsNullOrWhiteSpace(productName)
            ? Path.GetFileNameWithoutExtension(path)
            : productName;

        var definition = new ToolDefinition
        {
            Id = $"custom-{Guid.NewGuid():N}",
            Name = name,
            Category = "我的工具",
            Summary = "由用户添加的本地程序",
            Description = "从本机导入的程序，可通过统一入口启动。请自行确认其来源、版本与授权。",
            Vendor = string.IsNullOrWhiteSpace(version.CompanyName) ? "本地程序" : version.CompanyName.Trim(),
            License = "由用户导入，请遵循软件原始许可",
            Homepage = string.Empty,
            DownloadUrl = string.Empty,
            Folder = string.Empty,
            LocalPath = path,
            Glyph = "\uE7C3",
            Accent = "#F59E0B",
            IsUserTool = true,
            ExecutableNames = new List<string> { Path.GetFileName(path) },
            DetectNames = new List<string> { name },
            Tags = new List<string> { "本地导入", "自定义" }
        };

        _userTools.Add(definition);
        _catalogService.SaveUserTools(_userTools);

        var vm = new ToolViewModel(definition);
        vm.SetLocalPath(path);
        _allTools.Add(vm);
        UpdateCategoryCounts();
        _activeCategory = _categories.FirstOrDefault(c => c.FilterKey == "__mine__") ?? _activeCategory;
        if (_activeCategory is not null)
            CategoryList.SelectedItem = _activeCategory;
        ApplyFilter();
        SelectTool(vm);
        ScanStatusText = $"已添加：{name}";
    }

    private void SelectTool(ToolViewModel tool)
    {
        var category = _categories.FirstOrDefault(c =>
            c.FilterKey == tool.Category ||
            (tool.Definition.IsUserTool && c.FilterKey == "__mine__"));
        if (category is not null)
            CategoryList.SelectedItem = category;
        ApplyFilter();
        ToolList.SelectedItem = tool;
        ToolList.ScrollIntoView(tool);
        SelectedTool = tool;
    }

    private void FavoriteCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ToolViewModel tool }) return;
        tool.IsFavorite = !tool.IsFavorite;
        if (tool.IsFavorite) _settings.Favorites.Add(tool.Id);
        else _settings.Favorites.RemoveAll(id => id.Equals(tool.Id, StringComparison.OrdinalIgnoreCase));
        _settingsService.Save(_settings);
        UpdateCategoryCounts();
        if (_activeCategory?.FilterKey == "__favorites__")
            ApplyFilter();
        e.Handled = true;
    }

    private void LaunchCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ToolViewModel tool }) return;
        SelectTool(tool);
        ExecuteTool(tool);
        e.Handled = true;
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTool is not null) ExecuteTool(SelectedTool);
    }

    private void ExecuteTool(ToolViewModel tool)
    {
        var wasInstalled = tool.IsInstalled;
        if (_launchService.Launch(tool))
        {
            if (wasInstalled)
            {
                RecordRecent(tool.Id);
                ScanStatusText = $"已启动：{tool.Name}";
            }
            else
            {
                ScanStatusText = string.IsNullOrWhiteSpace(tool.DownloadUrl)
                    ? "未找到官方获取入口"
                    : $"已打开 {tool.Name} 官方获取页";
            }
        }
        else
        {
            ScanStatusText = "未启动：请检查文件，或重新进行管理员授权";
        }
    }

    private void RecordRecent(string id)
    {
        _settings.Recent.RemoveAll(value => value.Equals(id, StringComparison.OrdinalIgnoreCase));
        _settings.Recent.Insert(0, id);
        if (_settings.Recent.Count > 12)
            _settings.Recent.RemoveRange(12, _settings.Recent.Count - 12);
        _settingsService.Save(_settings);
        UpdateCategoryCounts();
        if (_activeCategory?.FilterKey == "__recent__")
            ApplyFilter();
    }

    private void OpenHomepage_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTool is null) return;
        var url = string.IsNullOrWhiteSpace(SelectedTool.Homepage)
            ? SelectedTool.DownloadUrl
            : SelectedTool.Homepage;
        if (string.IsNullOrWhiteSpace(url))
        {
            ScanStatusText = "该本地程序没有官网信息";
            return;
        }
        _launchService.OpenUrl(url);
        ScanStatusText = $"已打开 {SelectedTool.Name} 官方页面";
    }

    private void OpenToolFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTool is null) return;
        if (!string.IsNullOrWhiteSpace(SelectedTool.LocalPath))
        {
            _launchService.OpenFolder(SelectedTool.LocalPath);
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedTool.Definition.Folder)) return;
        var folder = Path.Combine(
            AppPaths.ToolsDirectory,
            SelectedTool.Definition.Folder.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            Directory.CreateDirectory(folder);
            _launchService.OpenFolder(folder);
            ScanStatusText = $"请将 {SelectedTool.Name} 便携版放入该目录";
        }
        catch
        {
            ScanStatusText = "无法创建工具目录";
        }
    }

    private void OpenToolsFolder_Click(object sender, RoutedEventArgs e)
    {
        AppPaths.EnsureCreated();
        _launchService.OpenFolder(AppPaths.ToolsDirectory);
    }

    private void OpenReadme_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(AppPaths.ReadmeFile))
            _launchService.OpenFile(AppPaths.ReadmeFile);
        else
            MessageBox.Show("未找到 README.txt。", "梦想成为单片机大手",
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_initialized) return;
        _settings.Favorites = _allTools
            .Where(t => t.IsFavorite)
            .Select(t => t.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settings.OnlyLocal = OnlyLocalCheck.IsChecked == true;
        _settings.LastCategory = _activeCategory?.FilterKey ?? "__all__";
        _settingsService.Save(_settings);
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            int enabled = 1;
            DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
            DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));

            int captionColor = 0x001F160D;
            int textColor = 0x00FAF7F2;
            int borderColor = 0x00322718;
            DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(int));
            DwmSetWindowAttribute(handle, 36, ref textColor, sizeof(int));
            DwmSetWindowAttribute(handle, 34, ref borderColor, sizeof(int));
        }
        catch
        {
            // 旧版 Windows 不支持深色标题栏时保持系统默认。
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

