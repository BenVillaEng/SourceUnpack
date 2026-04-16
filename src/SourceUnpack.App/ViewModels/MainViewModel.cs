using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Microsoft.Win32;
using SourceUnpack.Core.Conversion;
using SourceUnpack.Core.Formats.Bsp;
using SourceUnpack.Core.Formats.Gma;
using SourceUnpack.Core.Formats.Mdl;
using SourceUnpack.Core.Formats.Vpk;
using SourceUnpack.Core.Models;
using SourceUnpack.Core.Pipeline;

namespace SourceUnpack.App.ViewModels;

using SourceUnpack.Core.Services;


/// <summary>
/// Relay command implementation for MVVM.
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
}

/// <summary>
/// Tree node for the asset browser.
/// </summary>
public class AssetTreeNode : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isExpanded;
    private bool _isChecked;
    private AssetTreeNode? _parent;

    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = "📄";
    public AssetInfo? Asset { get; set; }
    public ObservableCollection<AssetTreeNode> Children { get; set; } = new();
    public int ItemCount { get; set; }
    public string DisplayName => ItemCount > 0 ? $"{Name} ({ItemCount})" : Name;
    public override string ToString() => DisplayName;

    public AssetTreeNode? Parent
    {
        get => _parent;
        set => _parent = value;
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                OnPropertyChanged();
                
                // Cascade down
                foreach (var child in Children)
                {
                    child.IsChecked = value;
                }

                // Cascade up (optional, checking if all siblings are checked)
                // For now, simpler approach: if user checks a child, verify parent. 
                // But strict "select folder -> select all" is the key requirement.
            }
        }
    }

    /// <summary>
    /// Returns the full asset path (e.g. "materials/models/cliffs/bridge.vtf") by traversing parents.
    /// </summary>
    public string FullPath
    {
        get
        {
            if (Asset != null) return Asset.FullPath;
            var parts = new List<string>();
            var node = this;
            while (node != null)
            {
                if (!string.IsNullOrEmpty(node.Name)) parts.Insert(0, node.Name);
                node = node.Parent;
            }
            return string.Join("/", parts);
        }
    }

    public ICommand CopyPathCommand => new RelayCommand(_ =>
    {
        try { System.Windows.Clipboard.SetText(FullPath); } catch { }
    });

    // Parent ViewModel reference for context menu commands
    public MainViewModel? ViewModel { get; set; }

    public ICommand ExtractNodeCommand => new RelayCommand(async _ =>
    {
        if (ViewModel != null && Asset != null)
        {
            await ViewModel.ExtractSingleNodeAsync(this);
        }
    });

    public ICommand ConvertNodeMdlCommand => new RelayCommand(async _ =>
    {
        if (ViewModel != null && Asset != null)
        {
            await ViewModel.ConvertSingleMdlNodeAsync(this);
        }
    });

    public ICommand ConvertNodeVtfCommand => new RelayCommand(async _ =>
    {
        if (ViewModel != null && Asset != null)
        {
            await ViewModel.ConvertSingleVtfNodeAsync(this);
        }
    });

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Main view model for the SourceUnpack application.
/// Handles BSP loading, asset discovery, extraction, and UI state.
/// </summary>
public class MainViewModel : INotifyPropertyChanged
{
    private string _bspFilePath = string.Empty;
    private string _mapName = string.Empty;
    private int _bspVersion;
    private int _mapRevision;
    private int _entityCount;
    private string _skyboxName = string.Empty;
    private string _outputDirectory = string.Empty;
    private string _gameDirectory = string.Empty;
    private double _progressValue;
    private string _progressText = string.Empty;
    private string _statusText = "Ready — Open a BSP file to begin";
    private bool _isBusy;
    private AssetTreeNode? _selectedNode;
    private string _logText = string.Empty;
    private BspReader? _currentBsp;
    private Dictionary<string, byte[]>? _cachedPakFiles; // Cache BSP pakfile to avoid re-extracting on every preview
    private List<AssetInfo> _allAssets = new();
    private string _infoLabel = "File Info";
    private string _addonName = string.Empty;
    private string _addonAuthor = string.Empty;

    // BSP Info
    public string BspFilePath { get => _bspFilePath; set { _bspFilePath = value; OnPropertyChanged(); } }
    public string MapName { get => _mapName; set { _mapName = value; OnPropertyChanged(); } }
    public int BspVersion { get => _bspVersion; set { _bspVersion = value; OnPropertyChanged(); } }
    public int MapRevision { get => _mapRevision; set { _mapRevision = value; OnPropertyChanged(); } }
    public int EntityCount { get => _entityCount; set { _entityCount = value; OnPropertyChanged(); } }
    public string SkyboxName { get => _skyboxName; set { _skyboxName = value; OnPropertyChanged(); } }
    public string InfoLabel { get => _infoLabel; set { _infoLabel = value; OnPropertyChanged(); } }
    
    private string _infoField1Label = "Name: ";
    public string InfoField1Label { get => _infoField1Label; set { _infoField1Label = value; OnPropertyChanged(); } }
    
    private string _infoField1Value = string.Empty;
    public string InfoField1Value { get => _infoField1Value; set { _infoField1Value = value; OnPropertyChanged(); } }
    
    private string _infoField2Label = "Ver: ";
    public string InfoField2Label { get => _infoField2Label; set { _infoField2Label = value; OnPropertyChanged(); } }
    
    private string _infoField2Value = string.Empty;
    public string InfoField2Value { get => _infoField2Value; set { _infoField2Value = value; OnPropertyChanged(); } }
    
    private string _infoField3Label = "Ents: ";
    public string InfoField3Label { get => _infoField3Label; set { _infoField3Label = value; OnPropertyChanged(); } }
    
    private string _infoField3Value = string.Empty;
    public string InfoField3Value { get => _infoField3Value; set { _infoField3Value = value; OnPropertyChanged(); } }

    // Settings
    public string OutputDirectory { get => _outputDirectory; set { _outputDirectory = value; OnPropertyChanged(); SaveSettings(); } }
    public string GameDirectory { get => _gameDirectory; set { _gameDirectory = value; OnPropertyChanged(); SaveSettings(); } }
    
    private bool _extractGameDependencies = true;
    public bool ExtractGameDependencies { get => _extractGameDependencies; set { _extractGameDependencies = value; OnPropertyChanged(); SaveSettings(); } }
    
    private bool _generateMissingPlaceholders = false;
    public bool GenerateMissingPlaceholders { get => _generateMissingPlaceholders; set { _generateMissingPlaceholders = value; OnPropertyChanged(); SaveSettings(); } }
    
    private bool _skipExistingFiles = false;
    public bool SkipExistingFiles { get => _skipExistingFiles; set { _skipExistingFiles = value; OnPropertyChanged(); SaveSettings(); } }
    
    private bool _keepMdlFile = false;
    public bool KeepMdlFile { get => _keepMdlFile; set { _keepMdlFile = value; OnPropertyChanged(); SaveSettings(); } }
    
    private bool _preserveDirectoryStructure = true;
    public bool PreserveDirectoryStructure { get => _preserveDirectoryStructure; set { _preserveDirectoryStructure = value; OnPropertyChanged(); SaveSettings(); } }
    
    private int _modelConversionFormatIndex;
    public int ModelConversionFormatIndex { get => _modelConversionFormatIndex; set { _modelConversionFormatIndex = value; OnPropertyChanged(); SaveSettings(); } }
    
    private int _textureFormatIndex; // 0 = PNG, 1 = JPG
    public int TextureFormatIndex { get => _textureFormatIndex; set { _textureFormatIndex = value; OnPropertyChanged(); SaveSettings(); } }
    
    private string _customAssetsDirectory = string.Empty;
    public string CustomAssetsDirectory 
    { 
        get => _customAssetsDirectory; 
        set 
        { 
            _customAssetsDirectory = value; 
            OnPropertyChanged(); 
            SaveSettings(); 
            UpdateDependencyGmas();
        } 
    }

    private Dictionary<string, GmaReader> _dependencyGmas = new(StringComparer.OrdinalIgnoreCase);

    // Progress
    public double ProgressValue { get => _progressValue; set { _progressValue = value; OnPropertyChanged(); } }
    public string ProgressText { get => _progressText; set { _progressText = value; OnPropertyChanged(); } }
    public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }
    public bool IsBusy { get => _isBusy; set { _isBusy = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); } }

    // Data
    public ObservableCollection<AssetTreeNode> AssetTree { get; set; } = new();
    public string LogText { get => _logText; set { _logText = value; OnPropertyChanged(); } }
    
    private string _systemLogText = string.Empty;
    public string SystemLogText { get => _systemLogText; set { _systemLogText = value; OnPropertyChanged(); } }

    // Search filter
    private string _searchFilter = string.Empty;
    private string _assetCountText = "0 assets";
    private CancellationTokenSource? _searchDebounceToken;
    public string SearchFilter 
    { 
        get => _searchFilter; 
        set 
        { 
            _searchFilter = value; 
            OnPropertyChanged();
            // Debounce: cancel previous pending filter and schedule a new one
            _searchDebounceToken?.Cancel();
            _searchDebounceToken = new CancellationTokenSource();
            var token = _searchDebounceToken.Token;
            Task.Delay(300, token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        OnPropertyChanged(nameof(FilteredAssetTree)));
                }
            }, TaskScheduler.Default);
        } 
    }
    public string AssetCountText { get => _assetCountText; set { _assetCountText = value; OnPropertyChanged(); } }

    public ObservableCollection<AssetTreeNode> FilteredAssetTree
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_searchFilter)) return AssetTree;
            var filtered = new ObservableCollection<AssetTreeNode>();
            foreach (var node in AssetTree)
            {
                var match = FilterNode(node, _searchFilter);
                if (match != null) filtered.Add(match);
            }
            return filtered;
        }
    }

    private AssetTreeNode? FilterNode(AssetTreeNode node, string filter)
    {
        bool nameMatches = node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);

        // Leaf node (has an Asset): return the ORIGINAL node so check state is shared
        if (node.Asset != null)
        {
            return nameMatches ? node : null;
        }

        // Container/directory node: build a wrapper with filtered children
        var matchedChildren = new ObservableCollection<AssetTreeNode>();
        foreach (var child in node.Children)
        {
            var match = FilterNode(child, filter);
            if (match != null) matchedChildren.Add(match);
        }
        if (nameMatches || matchedChildren.Count > 0)
        {
            var wrapper = new AssetTreeNode
            {
                Name = node.Name, Icon = node.Icon, Asset = node.Asset,
                ItemCount = node.ItemCount, IsExpanded = true,
                ViewModel = node.ViewModel
            };
            wrapper.IsChecked = node.IsChecked;
            wrapper.Children = matchedChildren;
            return wrapper;
        }
        return null;
    }

    // Asset Preview
    private ImageSource? _previewImage;
    private string _previewType = "none"; // "none", "image", "audio", "model", "text", "material"
    private string _previewText = "Select an asset to preview";
    private bool _isAudioPlaying;
    private System.Media.SoundPlayer? _soundPlayer;
    private Model3DGroup? _previewModel3D;
    private string _modelRenderMode = "solid"; // "solid" or "textured"
    private MdlModelData? _lastModelData;
    private AssetInfo? _lastModelAsset;
    private byte[]? _lastMdlData;

    public ImageSource? PreviewImage { get => _previewImage; set { _previewImage = value; OnPropertyChanged(); } }
    public string PreviewType { get => _previewType; set { _previewType = value; OnPropertyChanged(); } }
    public string PreviewText { get => _previewText; set { _previewText = value; OnPropertyChanged(); } }
    public bool IsAudioPlaying { get => _isAudioPlaying; set { _isAudioPlaying = value; OnPropertyChanged(); } }
    public Model3DGroup? PreviewModel3D { get => _previewModel3D; set { _previewModel3D = value; OnPropertyChanged(); } }
    public string ModelRenderMode { get => _modelRenderMode; set { _modelRenderMode = value; OnPropertyChanged(); } }

    public ICommand PlayAudioCommand { get; private set; }
    public ICommand StopAudioCommand { get; private set; }
    public ICommand ToggleRenderModeCommand { get; private set; }

    public AssetTreeNode? SelectedNode
    {
        get => _selectedNode;
        set 
        { 
            _selectedNode = value; 
            OnPropertyChanged();
            _ = LoadPreviewAsync(value);
        }
    }

    // Commands
    public ICommand OpenBspCommand { get; }
    public ICommand OpenVpkCommand { get; }
    public ICommand ExtractAllCommand { get; }
    public ICommand ExtractSelectedCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand BrowseGameDirCommand { get; }
    public ICommand BrowseCustomAssetsDirCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand OpenGmaCommand { get; }
    public ICommand ConvertMdlCommand { get; }
    public ICommand ConvertVtfCommand { get; }
    public ICommand ConvertBspToVmfCommand { get; }
    public ICommand UncheckAllCommand { get; }
    public ICommand CheckAllCommand { get; }
    public ICommand ShowAboutCommand { get; }

    // Export Presets
    public ICommand SavePresetCommand { get; }
    public ICommand LoadPresetCommand { get; }

    // Batch Queue
    private ObservableCollection<string> _batchQueue = new();
    private string _batchQueueText = string.Empty;
    public ObservableCollection<string> BatchQueue { get => _batchQueue; set { _batchQueue = value; OnPropertyChanged(); } }
    public string BatchQueueText { get => _batchQueueText; set { _batchQueueText = value; OnPropertyChanged(); } }
    public ICommand AddToBatchCommand { get; }
    public ICommand ClearBatchCommand { get; }
    public ICommand RunBatchCommand { get; }

    public MainViewModel()
    {
        OutputDirectory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SourceUnpack_Output");

        OpenBspCommand = new RelayCommand(_ => OpenBsp(), _ => !IsBusy);
        OpenVpkCommand = new RelayCommand(_ => OpenVpk(), _ => !IsBusy);
        ExtractAllCommand = new RelayCommand(async _ => await ExtractAll(), _ => !IsBusy && _allAssets.Count > 0);
        ExtractSelectedCommand = new RelayCommand(async _ => await ExtractSelected(), _ => !IsBusy && _allAssets.Count > 0);
        BrowseOutputCommand = new RelayCommand(_ => BrowseOutput());
        BrowseGameDirCommand = new RelayCommand(_ => BrowseGameDir());
        BrowseCustomAssetsDirCommand = new RelayCommand(_ => BrowseCustomAssetsDir());
        ClearLogCommand = new RelayCommand(_ => 
        { 
            LogText = string.Empty; 
            SystemLogText = string.Empty;
            // Easter egg: rare messages after clearing
            var rng = new Random();
            int roll = rng.Next(100);
            if (roll < 5)
                AppendLog("The cake is a lie.");
            else if (roll < 7)
                AppendSystemLog("I see you, Dr. Freeman.");
        });
        OpenGmaCommand = new RelayCommand(_ => OpenGma(), _ => !IsBusy);
        ConvertMdlCommand = new RelayCommand(async _ => await ConvertMdlDirect(), _ => !IsBusy);
        ConvertVtfCommand = new RelayCommand(async _ => await ConvertVtfDirect(), _ => !IsBusy);
        ConvertBspToVmfCommand = new RelayCommand(async _ => await ConvertBspToVmf(), _ => !IsBusy);
        PlayAudioCommand = new RelayCommand(_ => PlayAudio());
        StopAudioCommand = new RelayCommand(_ => StopAudio());
        UncheckAllCommand = new RelayCommand(_ => UncheckAll());
        CheckAllCommand = new RelayCommand(_ => CheckAll());
        ShowAboutCommand = new RelayCommand(_ => ShowAbout());
        ToggleRenderModeCommand = new RelayCommand(p => ToggleRenderMode(p as string));

        // v1.7+ commands
        SavePresetCommand = new RelayCommand(_ => SaveExportPreset());
        LoadPresetCommand = new RelayCommand(_ => LoadExportPreset());
        AddToBatchCommand = new RelayCommand(_ => AddToBatch(), _ => !IsBusy);
        ClearBatchCommand = new RelayCommand(_ => { BatchQueue.Clear(); BatchQueueText = "Queue cleared"; });
        RunBatchCommand = new RelayCommand(async _ => await RunBatchQueue(), _ => !IsBusy && BatchQueue.Count > 0);
        StartSteamCmdCommand = new RelayCommand(_ => StartSteamCmd());
        StopSteamCmdCommand = new RelayCommand(_ => StopSteamCmd());
        SendSteamCmdCommand = new RelayCommand(_ => SendSteamCmd());
        OpenSteamCmdContentCommand = new RelayCommand(_ => OpenSteamCmdContent());
        BrowseSteamCmdPathCommand = new RelayCommand(_ => BrowseSteamCmdPath());
        
        // Initialize Games
        SteamGames = new ObservableCollection<SteamGameOption>
        {
            new("Half-Life 2", 220),
            new("Counter-Strike: Source", 240),
            new("Team Fortress 2", 440),
            new("Left 4 Dead 2", 550),
            new("Portal 2", 620),
            new("Garry's Mod", 4000),
            new("Black Mesa", 362890)
        };
        
        LoadSettings();
    }

    private void OpenBsp()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open Source Engine BSP Map",
            Filter = "BSP Files (*.bsp)|*.bsp|All Files (*.*)|*.*",
            DefaultExt = ".bsp"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            IsBusy = true;
            StatusText = "Loading BSP...";
            AppendLog($"Opening: {dialog.FileName}");

            _currentBsp?.Dispose();
            _currentBsp = new BspReader(dialog.FileName);

            if (!_currentBsp.IsValid)
            {
                AppendLog("ERROR: Not a valid Source Engine BSP file.");
                StatusText = "Invalid BSP file";
                IsBusy = false;
                return;
            }

            _cachedPakFiles = _currentBsp.ExtractPakfile();

            BspFilePath = dialog.FileName;
            InfoLabel = "Map Info";
            
            MapName = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
            BspVersion = _currentBsp.Header.Version;
            MapRevision = _currentBsp.Header.MapRevision;
            var entities = _currentBsp.ParseEntities();
            EntityCount = entities.Count;
            SkyboxName = _currentBsp.GetSkyboxName() ?? "(none)";

            InfoField1Label = "Name: ";
            InfoField1Value = MapName;
            InfoField2Label = "Ver: ";
            InfoField2Value = BspVersion.ToString();
            InfoField3Label = "Ents: ";
            InfoField3Value = EntityCount.ToString();

            AppendLog($"BSP v{BspVersion} | Revision: {MapRevision} | Entities: {EntityCount}");
            AppendLog($"Skybox: {SkyboxName}");

            // Discover assets
            StatusText = "Discovering assets...";
            _allAssets = AssetDiscovery.DiscoverFromBsp(_currentBsp);
            var stats = AssetDiscovery.GetStats(_allAssets);
            AppendLog($"Found: {stats.textures} textures, {stats.models} models, {stats.sounds} sounds, {stats.skyboxes} skybox faces, {stats.other} other");

            // Build tree
            BuildAssetTree();

            StatusText = $"Loaded {MapName} — {_allAssets.Count} assets discovered";
            AssetCountText = $"{_allAssets.Count} assets";
            _ = MountGameVpksAsync(dialog.FileName);
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            StatusText = "Error loading BSP";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BuildAssetTree()
    {
        AssetTree.Clear();
        var grouped = AssetDiscovery.GroupByType(_allAssets);

        foreach (var (type, assets) in grouped.OrderBy(g => g.Key))
        {
            string icon = type switch
            {
                AssetType.Texture => "🖼",
                AssetType.Material => "🎨",
                AssetType.Model => "🗃",
                AssetType.Sound => "🔊",
                AssetType.Skybox => "🌅",
                _ => "📄"
            };

            var typeNode = new AssetTreeNode
            {
                Name = type.ToString(),
                Icon = icon,
                ItemCount = assets.Count,
                IsExpanded = true,
                ViewModel = this
            };

            // Group by subdirectory
            var byDir = assets.GroupBy(a => a.Directory).OrderBy(g => g.Key);
            foreach (var dirGroup in byDir)
            {
                string dirName = string.IsNullOrEmpty(dirGroup.Key) ? "(root)" : dirGroup.Key;
                var dirNode = new AssetTreeNode
                {
                    Name = dirName,
                    Icon = "📁",
                    ItemCount = dirGroup.Count(),
                    ViewModel = this
                };

                foreach (var asset in dirGroup)
                {
                    dirNode.Children.Add(new AssetTreeNode
                    {
                        Name = asset.FileName,
                        Icon = icon,
                        Asset = asset,
                        ViewModel = this
                    });
                }

                typeNode.Children.Add(dirNode);
            }

            AssetTree.Add(typeNode);
        }
    }

    private async Task ExtractAll()
    {
        await RunExtraction(_allAssets);
    }

    private async Task ExtractSelected()
    {
        var selected = GetSelectedAssets();
        if (selected.Count == 0)
        {
            AppendLog("No assets selected.");
            return;
        }
        await RunExtraction(selected);
    }

    private List<AssetInfo> GetSelectedAssets()
    {
        var result = new List<AssetInfo>();
        // Walk the original tree (handles unfiltered case and nodes checked before filtering)
        CollectSelected(AssetTree, result);
        // Also walk the filtered tree in case filter created wrapper nodes
        // that contain original leaf nodes not yet visited
        var filtered = FilteredAssetTree;
        if (!ReferenceEquals(filtered, AssetTree))
        {
            CollectSelected(filtered, result);
        }
        // Deduplicate by FullPath since the same leaf node may appear in both trees
        return result.DistinctBy(a => a.FullPath).ToList();
    }

    private void CollectSelected(IEnumerable<AssetTreeNode> nodes, List<AssetInfo> result)
    {
        foreach (var node in nodes)
        {
            if (node.IsChecked && node.Asset != null)
                result.Add(node.Asset);
            CollectSelected(node.Children, result);
        }
    }

    public async Task ExtractSingleNodeAsync(AssetTreeNode node)
    {
        if (node.Asset == null) return;
        var selected = new List<AssetInfo> { node.Asset };
        await RunExtraction(selected);
    }

    public async Task ConvertSingleMdlNodeAsync(AssetTreeNode node)
    {
        if (node.Asset == null || !node.Asset.FileName.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) return;
        
        // This invokes the existing logic for models but we need it to use the pipeline or direct convert.
        // The easiest way is to just call RunExtraction with Model mode enabled.
        var selected = new List<AssetInfo> { node.Asset };
        await RunExtraction(selected);
    }
    
    public async Task ConvertSingleVtfNodeAsync(AssetTreeNode node)
    {
        if (node.Asset == null || !node.Asset.FileName.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase)) return;
        
        var selected = new List<AssetInfo> { node.Asset };
        
        // Temporarily override ModelFormat? No, RunExtraction handles VTF to PNG automatically if requested.
        // Actually, ExtractModel handles models, but for VTF to PNG we need a specific flag or we just let pipeline extract it.
        // The standard RunExtraction pipeline currently extracts VTF raw. 
        // Let's implement dynamic single-file conversion utilizing the existing ConvertMdlDirect and ConvertVtfDirect logic, 
        // BUT they prompt for a file. We'll add overloaded methods that take byte data directly.
        await ConvertNodeDirectAsync(node);
    }

    private async Task ConvertNodeDirectAsync(AssetTreeNode node)
    {
        if (node.Asset == null) return;
        
        try
        {
            IsBusy = true;
            StatusText = $"Quick Converting {node.Asset.FileName}...";
            AppendLog($"Quick Converting: {node.Asset.FullPath}");
            
            var options = new ConversionOptions
            {
                OutputDirectory = OutputDirectory,
                PreserveDirectoryStructure = PreserveDirectoryStructure,
                ExportSeparateMaterialMaps = true,
                ModelExportFormat = (ModelFormat)ModelConversionFormatIndex,
                KeepMdlFile = KeepMdlFile,
                TextureOutputFormat = (TextureFormat)TextureFormatIndex
            };
            
            var pipeline = new ExtractionPipeline(options);
            pipeline.LogMessage += (_, msg) => System.Windows.Application.Current?.Dispatcher.Invoke(() => AppendLog(msg));
            
            // Load source
            if (!string.IsNullOrEmpty(BspFilePath)) pipeline.LoadBsp(BspFilePath);
            else if (!string.IsNullOrEmpty(_currentGmaPath)) pipeline.LoadGma(_currentGmaPath);
            else if (!string.IsNullOrEmpty(_currentVpkPath)) pipeline.LoadVpk(_currentVpkPath);
            
            // For custom assets
            if (!string.IsNullOrWhiteSpace(CustomAssetsDirectory))
            {
                var paths = CustomAssetsDirectory.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in paths)
                {
                    string t = p.Trim();
                    if (System.IO.Directory.Exists(t)) pipeline.LoadDirectory(t);
                    else if (System.IO.File.Exists(t) && t.EndsWith(".gma", StringComparison.OrdinalIgnoreCase)) pipeline.LoadGma(t);
                }
            }
            
            // Run on a single item. We just need to filter the assets list to one item and process it.
            var singleList = new List<AssetInfo> { node.Asset };
            
            if (node.Asset.Type == AssetType.Texture)
            {
                // Quick Convert automatically uses the selected TextureOutputFormat
                // via the standard ExtractionPipeline since it supports VTF conversion.
            }
            
            await pipeline.ExtractAsync(singleList);
            StatusText = $"Quick Converted {node.Asset.FileName}";
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            StatusText = "Quick Conversion failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunExtraction(List<AssetInfo> assets)
    {
        try
        {
            IsBusy = true;
            StatusText = "Extracting assets...";
            ProgressValue = 0;

            var options = new ConversionOptions
            {
                OutputDirectory = OutputDirectory,
                PreserveDirectoryStructure = PreserveDirectoryStructure,
                ExportSeparateMaterialMaps = true,
                AssembleSkyboxCubemap = true,
                ExtractGameDependencies = ExtractGameDependencies,
                GenerateMissingPlaceholders = GenerateMissingPlaceholders,
                SkipExistingFiles = SkipExistingFiles,
                KeepMdlFile = KeepMdlFile,
                ModelExportFormat = (ModelFormat)ModelConversionFormatIndex,
                TextureOutputFormat = (TextureFormat)TextureFormatIndex
            };

            var pipeline = new ExtractionPipeline(options);
            pipeline.ProgressChanged += (_, e) =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    ProgressValue = e.Percentage;
                    ProgressText = $"{e.Current}/{e.Total} — {e.Message}";
                });
            };
            pipeline.LogMessage += (_, msg) =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() => AppendLog(msg));
            };
            pipeline.SystemMessage += (_, msg) =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() => AppendSystemLog(msg));
            };

            // Load BSP into pipeline
            if (!string.IsNullOrEmpty(BspFilePath))
            {
                pipeline.LoadBsp(BspFilePath);
                
                // Auto-detect implicitly discovered addon root:
                // E.g., if we opened C:\Addons\MyMap\maps\my_map.bsp, the loose files might be in C:\Addons\MyMap
                try {
                    string mapDir = Path.GetDirectoryName(BspFilePath)!;
                    string dirName = new DirectoryInfo(mapDir).Name;
                    if (dirName.Equals("maps", StringComparison.OrdinalIgnoreCase))
                    {
                        var parentDir = Directory.GetParent(mapDir);
                        if (parentDir != null)
                        {
                            pipeline.LoadDirectory(parentDir.FullName);
                        }
                    }
                } catch { }
            }
            else if (!string.IsNullOrEmpty(_currentGmaPath))
            {
                // GMA Mode
                pipeline.LoadGma(_currentGmaPath);
            }
            else if (!string.IsNullOrEmpty(_currentVpkPath))
            {
                // VPK Mode
                pipeline.LoadVpk(_currentVpkPath);
            }

            // Load explicit Custom Assets Directory (supports multiple)
            if (!string.IsNullOrWhiteSpace(CustomAssetsDirectory))
            {
                var paths = CustomAssetsDirectory.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in paths)
                {
                    string t = p.Trim();
                    if (System.IO.Directory.Exists(t)) pipeline.LoadDirectory(t);
                    else if (System.IO.File.Exists(t) && t.EndsWith(".gma", StringComparison.OrdinalIgnoreCase)) pipeline.LoadGma(t);
                }
            }

            // Load Game VPKs if configured (Background Task)
            // But skip if we're explicitly extracting a direct standalone VPK (we don't need dependencies for it)
            if (options.ExtractGameDependencies && string.IsNullOrEmpty(_currentVpkPath))
            {
                await Task.Run(() =>
                {
                    StatusText = "Mounting Game VPKs...";
                    try
                    {
                        var vpkList = FindGameVpks(!string.IsNullOrEmpty(BspFilePath) ? BspFilePath : _currentVpkPath);

                        if (vpkList.Count > 100) 
                        {
                            AppendSystemLog($"WARNING: Found {vpkList.Count} VPKs! Mounting first 100.");
                            vpkList = vpkList.Take(100).ToList();
                        }

                        foreach (var vpk in vpkList)
                        {
                            if (string.Equals(vpk, _currentVpkPath, StringComparison.OrdinalIgnoreCase)) continue;
                            pipeline.LoadVpk(vpk);
                        }
                        AppendSystemLog($"Mounted {vpkList.Count} external VPKs.");
                    }
                    catch (Exception ex)
                    {
                         AppendSystemLog($"ERROR scanning VPKs: {ex.Message}");
                    }
                });
            }
            
            await pipeline.ExtractAsync(assets, CancellationToken.None);

            StatusText = $"Extraction complete — {assets.Count} assets processed";
            ProgressValue = 100;
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            StatusText = "Extraction failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string _currentVpkPath = string.Empty;
    private VpkReader? _currentVpk;
    private List<VpkReader> _mountedGameVpks = new();

    private async Task MountGameVpksAsync(string currentFilePath)
    {
        lock (_mountedGameVpks)
        {
            foreach (var vpk in _mountedGameVpks) vpk.Dispose();
            _mountedGameVpks.Clear();
        }

        var newVpks = await Task.Run(() =>
        {
            var result = new List<VpkReader>();
            var vpkList = FindGameVpks(currentFilePath);

            if (vpkList.Count > 100) vpkList = vpkList.Take(100).ToList();

            foreach (var vpk in vpkList)
            {
                if (string.Equals(vpk, currentFilePath, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    result.Add(new VpkReader(vpk));
                }
                catch { }
            }
            return result;
        });

        lock (_mountedGameVpks)
        {
            _mountedGameVpks.AddRange(newVpks);
        }
    }
    private void OpenVpk()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open Source Engine VPK Archive",
            Filter = "VPK Files (*.vpk)|*.vpk|All Files (*.*)|*.*",
            DefaultExt = ".vpk"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            IsBusy = true;
            StatusText = "Loading VPK...";
            string vpkPath = dialog.FileName;

            // Fix: Handle multi-part VPKs (if user selects _000.vpk, switch to _dir.vpk)
            if (System.Text.RegularExpressions.Regex.IsMatch(vpkPath, @"_\d{3}\.vpk$"))
            {
                string dirVpk = System.Text.RegularExpressions.Regex.Replace(vpkPath, @"_\d{3}\.vpk$", "_dir.vpk");
                if (System.IO.File.Exists(dirVpk))
                {
                    AppendLog($"Note: Redirecting from '{System.IO.Path.GetFileName(vpkPath)}' to '{System.IO.Path.GetFileName(dirVpk)}'");
                    vpkPath = dirVpk;
                }
                else
                {
                    StatusText = "Invalid VPK Selection";
                    AppendLog("ERROR: You selected a VPK content chunk. Please open the '_dir.vpk' file instead.");
                    IsBusy = false;
                    return;
                }
            }

            AppendLog($"Opening VPK: {vpkPath}");

            // Cleanup previous
            _currentBsp?.Dispose();
            _currentBsp = null;
            BspFilePath = string.Empty; 
            
            // Auto-detect Game Directory from VPK path if not set
            if (string.IsNullOrEmpty(GameDirectory))
            {
                // If VPK is in "steamapps/common/Half-Life 2/ep2", suggest "ep2" or "Half-Life 2"?
                // Let's just set it to the parent folder of the VPK for now.
                string? vpkDir = System.IO.Path.GetDirectoryName(vpkPath);
                if (vpkDir != null)
                {
                    GameDirectory = vpkDir;
                    AppendLog($"Auto-set Game Directory to: {GameDirectory}");
                }
            }

            _currentVpk?.Dispose();
            _currentVpk = new VpkReader(vpkPath);
            _currentVpkPath = vpkPath;

            MapName = System.IO.Path.GetFileNameWithoutExtension(vpkPath);
            BspVersion = _currentVpk.Version;
            MapRevision = 0;
            EntityCount = 0;
            InfoLabel = "VPK Info";
            
            InfoField1Label = "Files: ";
            InfoField1Value = _currentVpk.Entries.Count.ToString();
            InfoField2Label = "Ver: ";
            InfoField2Value = _currentVpk.Version.ToString();
            InfoField3Label = "Archives: ";
            InfoField3Value = "0"; // VpkReader implementation doesn't expose Archives directly here, loose files don't matter as much.

            AppendLog($"VPK v{_currentVpk.Version} | {_currentVpk.Entries.Count} entries");

            // Discover assets
            StatusText = "Discovering assets...";
            _allAssets = AssetDiscovery.DiscoverFromVpk(_currentVpk);
            AssetCountText = $"{_allAssets.Count} assets";
            var stats = AssetDiscovery.GetStats(_allAssets);
            AppendLog($"Found: {stats.textures} textures, {stats.models} models, {stats.sounds} sounds");

            // Build tree
            BuildAssetTree();

            StatusText = $"Loaded {MapName} — {_allAssets.Count} assets discovered";
            _ = MountGameVpksAsync(vpkPath);
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            StatusText = "Error loading VPK";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string _currentGmaPath = string.Empty;
    private GmaReader? _currentGma;

    private void OpenGma()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open Workshop Addon (GMA)",
            Filter = "GMA Files (*.gma)|*.gma|All Files (*.*)|*.*",
            DefaultExt = ".gma"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            IsBusy = true;
            StatusText = "Loading GMA...";
            string gmaPath = dialog.FileName;

            AppendLog($"Opening GMA: {gmaPath}");

            // Cleanup previous
            _currentBsp?.Dispose();
            _currentBsp = null;
            BspFilePath = string.Empty;
            _currentVpk?.Dispose();
            _currentVpk = null;
            _currentVpkPath = string.Empty;

            _currentGma?.Dispose();
            _currentGma = new GmaReader(gmaPath);
            _currentGmaPath = gmaPath;

            if (!_currentGma.IsValid)
            {
                AppendLog("ERROR: Not a valid GMA file.");
                StatusText = "Invalid GMA file";
                IsBusy = false;
                return;
            }

            InfoLabel = "Addon Info";
            
            MapName = _currentGma.AddonName;
            BspVersion = _currentGma.FormatVersion;
            MapRevision = _currentGma.AddonVersion;
            EntityCount = _currentGma.Entries.Count;
            SkyboxName = "(n/a)";

            InfoField1Label = "Name: ";
            InfoField1Value = _currentGma.AddonName;
            InfoField2Label = "Author: ";
            InfoField2Value = _currentGma.AddonAuthor;
            InfoField3Label = "Desc: ";
            InfoField3Value = _currentGma.AddonDescription;

            AppendLog($"Addon: {_currentGma.AddonName} | Author: {_currentGma.AddonAuthor} | Files: {_currentGma.Entries.Count}");

            // Discover assets
            StatusText = "Discovering assets...";
            _allAssets = AssetDiscovery.DiscoverFromGma(_currentGma);
            AssetCountText = $"{_allAssets.Count} assets";
            var stats = AssetDiscovery.GetStats(_allAssets);
            AppendLog($"Found: {stats.textures} textures, {stats.models} models, {stats.sounds} sounds, {stats.other} other");

            // Build tree
            BuildAssetTree();

            StatusText = $"Loaded addon '{_currentGma.AddonName}' — {_allAssets.Count} assets discovered";
            _ = MountGameVpksAsync(gmaPath);
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            StatusText = "Error loading GMA";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ConvertMdlDirect()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose MDL Model to Convert",
            Filter = "MDL Files (*.mdl)|*.mdl|All Files (*.*)|*.*",
            DefaultExt = ".mdl"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            IsBusy = true;
            string mdlPath = dialog.FileName;
            string baseName = System.IO.Path.GetFileNameWithoutExtension(mdlPath);
            string? dir = System.IO.Path.GetDirectoryName(mdlPath);
            if (dir == null) return;

            StatusText = $"Converting {baseName}.mdl → OBJ...";
            AppendLog($"Converting MDL: {mdlPath}");

            // Find sibling files
            string vvdPath = System.IO.Path.ChangeExtension(mdlPath, ".vvd");
            string vtxPath = System.IO.Path.Combine(dir, baseName + ".dx90.vtx");

            if (!System.IO.File.Exists(vtxPath))
                vtxPath = System.IO.Path.Combine(dir, baseName + ".dx80.vtx");
            if (!System.IO.File.Exists(vtxPath))
                vtxPath = System.IO.Path.Combine(dir, baseName + ".sw.vtx");
            if (!System.IO.File.Exists(vtxPath))
                vtxPath = System.IO.Path.Combine(dir, baseName + ".vtx");

            byte[]? mdlData = System.IO.File.Exists(mdlPath) ? System.IO.File.ReadAllBytes(mdlPath) : null;
            byte[]? vvdData = System.IO.File.Exists(vvdPath) ? System.IO.File.ReadAllBytes(vvdPath) : null;
            byte[]? vtxData = System.IO.File.Exists(vtxPath) ? System.IO.File.ReadAllBytes(vtxPath) : null;

            if (mdlData == null || vvdData == null || vtxData == null)
            {
                string missing = "";
                if (vvdData == null) missing += ".vvd ";
                if (vtxData == null) missing += ".vtx ";
                AppendLog($"ERROR: Missing required sibling files ({missing.Trim()}) in {dir}");
                StatusText = "Missing VVD/VTX files";
                return;
            }

            await Task.Run(() =>
            {
                var reader = new MdlReader();
                var model = reader.Load(mdlData, vvdData, vtxData);

                if (!model.IsValid)
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        AppendLog($"FAIL: MDL parse error for {baseName}");
                        StatusText = "MDL parse error";
                    });
                    return;
                }

                string outDir = OutputDirectory;
                if (ModelConverter.ExportObj(model, outDir, baseName))
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        AppendLog($"OK: {baseName}.mdl → {baseName}.obj + {baseName}.mtl");
                        StatusText = $"Converted {baseName}.mdl → OBJ";
                    });
                }
                else
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        AppendLog($"FAIL: OBJ export error for {baseName}");
                        StatusText = "OBJ export error";
                    });
                }
            });
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            StatusText = "MDL conversion failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ConvertBspToVmf()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose BSP to Decompile to VMF",
            Filter = "BSP Files (*.bsp)|*.bsp|All Files (*.*)|*.*",
            DefaultExt = ".bsp"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            IsBusy = true;
            string bspPath = dialog.FileName;
            string baseName = System.IO.Path.GetFileNameWithoutExtension(bspPath);

            StatusText = $"Decompiling {baseName}.bsp → VMF...";
            AppendLog($"BSP → VMF: {bspPath}");

            string outPath = System.IO.Path.Combine(OutputDirectory, baseName + ".vmf");

            bool success = await Task.Run(() => BspToVmfConverter.Convert(bspPath, outPath));

            if (success)
            {
                AppendLog($"OK: {baseName}.bsp → {baseName}.vmf");
                AppendLog($"Output: {outPath}");
                StatusText = $"Decompiled {baseName}.bsp → VMF";
            }
            else
            {
                AppendLog($"FAIL: Could not decompile {baseName}.bsp (invalid BSP or read error)");
                StatusText = "BSP → VMF failed";
            }
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            StatusText = "BSP → VMF failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ConvertVtfDirect()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose VTF Texture to Convert",
            Filter = "VTF Files (*.vtf)|*.vtf|All Files (*.*)|*.*",
            DefaultExt = ".vtf"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            IsBusy = true;
            string vtfPath = dialog.FileName;
            string baseName = System.IO.Path.GetFileNameWithoutExtension(vtfPath);

            var texFormat = (TextureFormat)TextureFormatIndex;
            string texExt = TextureConverter.GetExtension(texFormat);
            StatusText = $"Converting {baseName}.vtf → {texExt.TrimStart('.').ToUpper()}...";
            AppendLog($"Converting VTF: {vtfPath}");

            await Task.Run(() =>
            {
                byte[] vtfData = System.IO.File.ReadAllBytes(vtfPath);
                string outPath = System.IO.Path.Combine(OutputDirectory, baseName + texExt);

                if (TextureConverter.VtfToFile(vtfData, outPath, texFormat))
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        AppendLog($"OK: {baseName}.vtf → {baseName}{texExt}");
                        StatusText = $"Converted {baseName}.vtf → {texExt.TrimStart('.').ToUpper()}";
                    });
                }
                else
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        AppendLog($"FAIL: VTF decode error for {baseName}");
                        StatusText = "VTF decode error";
                    });
                }
            });
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            StatusText = "VTF conversion failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BrowseOutput()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select Output Directory",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            OutputDirectory = dialog.SelectedPath;
    }

    private void BrowseGameDir()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Source Engine Game Directory (contains _dir.vpk files)",
            InitialDirectory = !string.IsNullOrEmpty(GameDirectory) ? GameDirectory : string.Empty,
            Multiselect = false
        };
        var res = dialog.ShowDialog();
        if (res == true)
        {
            GameDirectory = dialog.FolderName;
        }
    }

    private void BrowseCustomAssetsDir()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Custom Assets Directory or GMA",
            Filter = "Directories or GMAs|*.*",
            Multiselect = true,
            CheckFileExists = false,
            FileName = "Select Folder"
        };
        var res = dialog.ShowDialog();
        if (res == true)
        {
            var paths = new List<string>(dialog.FileNames);
            
            // If the user selected a folder, the path might end with "Select Folder".
            // Clean it up.
            for (int i = 0; i < paths.Count; i++)
            {
                if (paths[i].EndsWith("Select Folder", StringComparison.OrdinalIgnoreCase))
                {
                    paths[i] = System.IO.Path.GetDirectoryName(paths[i]) ?? paths[i];
                }
            }

            string combined = string.Join(";", paths);
            if (!string.IsNullOrWhiteSpace(CustomAssetsDirectory))
                CustomAssetsDirectory += ";" + combined;
            else
                CustomAssetsDirectory = combined;
        }
    }

    private void UpdateDependencyGmas()
    {
        if (string.IsNullOrWhiteSpace(CustomAssetsDirectory))
        {
            _dependencyGmas.Clear();
            return;
        }

        var paths = CustomAssetsDirectory.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim());
        var currentKeys = _dependencyGmas.Keys.ToList();
        
        // Remove GMAs no longer in the list
        foreach (var key in currentKeys)
        {
            if (!paths.Contains(key, StringComparer.OrdinalIgnoreCase))
                _dependencyGmas.Remove(key);
        }

        // Add new GMAs
        foreach (var p in paths)
        {
            if (System.IO.File.Exists(p) && p.EndsWith(".gma", StringComparison.OrdinalIgnoreCase))
            {
                if (!_dependencyGmas.ContainsKey(p))
                {
                    try
                    {
                        _dependencyGmas[p] = new GmaReader(p);
                    }
                    catch { }
                }
            }
        }
    }

    private void AppendLog(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string newLine = $"[{timestamp}] {message}\n";
        // Cap log length to prevent O(n²) string concat for large extractions
        if (LogText.Length > 200_000)
            LogText = LogText.Substring(LogText.Length - 150_000) + newLine;
        else
            LogText += newLine;
    }

    private void AppendSystemLog(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string newLine = $"[{timestamp}] {message}\n";
        if (SystemLogText.Length > 200_000)
            SystemLogText = SystemLogText.Substring(SystemLogText.Length - 150_000) + newLine;
        else
            SystemLogText += newLine;
    }

    // SteamCMD Integration
    private SteamCmdService? _steamCmd;
    private string _steamCmdLog = string.Empty;
    private string _steamCmdInput = string.Empty;
    private ObservableCollection<SteamGameOption> _steamGames = new();
    private SteamGameOption? _selectedSteamGame;
    private string _steamCmdPath = string.Empty;

    public string SteamCmdLog { get => _steamCmdLog; set { _steamCmdLog = value; OnPropertyChanged(); } }
    public string SteamCmdInput { get => _steamCmdInput; set { _steamCmdInput = value; OnPropertyChanged(); } }
    public ObservableCollection<SteamGameOption> SteamGames { get => _steamGames; set { _steamGames = value; OnPropertyChanged(); } }
    public string SteamCmdPath { get => _steamCmdPath; set { _steamCmdPath = value; OnPropertyChanged(); SaveSettings(); } }
    
    public SteamGameOption? SelectedSteamGame 
    { 
        get => _selectedSteamGame; 
        set 
        { 
            _selectedSteamGame = value; 
            OnPropertyChanged();
            if (value != null)
            {
                SteamCmdInput = $"workshop_download_item {value.AppId} ";
            }
        } 
    }

    public ICommand StartSteamCmdCommand { get; }
    public ICommand StopSteamCmdCommand { get; }
    public ICommand SendSteamCmdCommand { get; }
    public ICommand OpenSteamCmdContentCommand { get; }
    public ICommand BrowseSteamCmdPathCommand { get; }

    private void InitializeSteamCmd()
    {
        _steamCmd = new SteamCmdService();
        
        // Use configured path if available
        if (!string.IsNullOrEmpty(SteamCmdPath))
        {
            _steamCmd.SetCustomPath(SteamCmdPath);
        }

        _steamCmd.OutputReceived += (s, msg) =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                SteamCmdLog += msg + "\n";
            });
        };
    }

    private void StartSteamCmd()
    {
        if (_steamCmd == null) InitializeSteamCmd();
        
        // Re-apply path just in case
        if (!string.IsNullOrEmpty(SteamCmdPath) && _steamCmd != null)
             _steamCmd.SetCustomPath(SteamCmdPath);

        if (_steamCmd!.IsRunning) return;
        
        SteamCmdLog = "";
        AppendLog("Starting SteamCMD...");
        try 
        {
            _steamCmd.Start();
        }
        catch (Exception ex)
        {
             SteamCmdLog += $"[Error] {ex.Message}\n";
        }
    }

    private void StopSteamCmd()
    {
        if (_steamCmd != null && _steamCmd.IsRunning)
        {
            _steamCmd.Stop();
            AppendLog("SteamCMD stopped.");
        }
    }

    private void SendSteamCmd()
    {
        if (string.IsNullOrWhiteSpace(SteamCmdInput)) return;
        
        string cmd = SteamCmdInput.Trim();
        SteamCmdInput = string.Empty;

        // If the command looks like a workshop download, use batch mode for reliability
        if (cmd.StartsWith("workshop_download_item", StringComparison.OrdinalIgnoreCase) ||
            cmd.StartsWith("+workshop_download_item", StringComparison.OrdinalIgnoreCase))
        {
            string cleanCmd = cmd.StartsWith("+") ? cmd : "+" + cmd;
            if (_steamCmd != null && _steamCmd.IsRunning)
                _steamCmd.Stop();
            _steamCmd?.RunBatch(cleanCmd);
        }
        else if (_steamCmd != null && _steamCmd.IsRunning)
        {
            // For other commands, send to running interactive process
            _steamCmd.SendCommand(cmd);
        }
        else
        {
            AppendLog("[Info] Starting SteamCMD and sending command...");
            StartSteamCmd();
            // Small delay then send
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                _steamCmd?.SendCommand(cmd);
            });
        }
    }

    private void OpenSteamCmdContent()
    {
        // 1. Determine actual SteamCMD executable path
        string exePath = SteamCmdPath;
        if (string.IsNullOrEmpty(exePath) || !System.IO.File.Exists(exePath))
        {
             string appRoot = AppDomain.CurrentDomain.BaseDirectory;
             string subPath = System.IO.Path.Combine(appRoot, "steamcmd", "steamcmd.exe");
             if (System.IO.File.Exists(subPath)) exePath = subPath;
             else exePath = System.IO.Path.Combine(appRoot, "steamcmd.exe");
        }

        // 2. Resolve content directory relative to that executable
        string rootDir = System.IO.Path.GetDirectoryName(exePath) ?? AppDomain.CurrentDomain.BaseDirectory;
        string contentPath = System.IO.Path.Combine(rootDir, "steamapps", "workshop", "content");

        // 3. Append AppID if selected
        if (_selectedSteamGame != null)
        {
            contentPath = System.IO.Path.Combine(contentPath, _selectedSteamGame.AppId.ToString());
        }

        // 4. Open the most specific path that exists
        if (System.IO.Directory.Exists(contentPath))
        {
            Process.Start(new ProcessStartInfo { FileName = contentPath, UseShellExecute = true });
        }
        else if (System.IO.Directory.Exists(rootDir))
        {
            // Fallback to root if specific content folder not found
            Process.Start(new ProcessStartInfo { FileName = rootDir, UseShellExecute = true });
            
            // Helpful message
            if (_selectedSteamGame != null)
                AppendLog($"[Info] Content folder for {_selectedSteamGame.Name} not found yet. Opened root.");
        }
        else
        {
            AppendLog($"[Error] Could not find SteamCMD directory at: {rootDir}");
        }
    }

    private void BrowseSteamCmdPath()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select steamcmd.exe",
            Filter = "Executables (*.exe)|*.exe|All Files (*.*)|*.*",
            FileName = "steamcmd.exe"
        };
        
        if (dialog.ShowDialog() == true)
        {
            SteamCmdPath = dialog.FileName;
        }
    }

    private void SaveSettings()
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string settingsDir = System.IO.Path.Combine(appData, "SourceUnpack");
            string settingsFile = System.IO.Path.Combine(settingsDir, "settings.txt");
            System.IO.Directory.CreateDirectory(settingsDir);
            // Simple format: GameDir|OutputDir|SteamCmdPath|ExtractGameDependencies|GenerateMissingPlaceholders|SkipExistingFiles|ModelConversionFormatIndex|KeepMdl|PreserveDir|TextureFormatIndex
            string data = $"{GameDirectory}|{OutputDirectory}|{SteamCmdPath}|{ExtractGameDependencies}|{GenerateMissingPlaceholders}|{SkipExistingFiles}|{ModelConversionFormatIndex}|{KeepMdlFile}|{PreserveDirectoryStructure}|{TextureFormatIndex}";
            System.IO.File.WriteAllText(settingsFile, data);
        }
        catch { }
    }

    private void LoadSettings()
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string settingsFile = System.IO.Path.Combine(appData, "SourceUnpack", "settings.txt");
            if (System.IO.File.Exists(settingsFile))
            {
                string data = System.IO.File.ReadAllText(settingsFile);
                string[] parts = data.Split('|');
                if (parts.Length >= 1) GameDirectory = parts[0];
                if (parts.Length >= 2) OutputDirectory = parts[1];
                if (parts.Length >= 3) SteamCmdPath = parts[2];
                if (parts.Length >= 4 && bool.TryParse(parts[3], out bool egd)) ExtractGameDependencies = egd;
                if (parts.Length >= 5 && bool.TryParse(parts[4], out bool gmp)) GenerateMissingPlaceholders = gmp;
                if (parts.Length >= 6 && bool.TryParse(parts[5], out bool sef)) SkipExistingFiles = sef;
                if (parts.Length >= 7 && int.TryParse(parts[6], out int mcf)) ModelConversionFormatIndex = mcf;
                if (parts.Length >= 8 && bool.TryParse(parts[7], out bool keep)) KeepMdlFile = keep;
                if (parts.Length >= 9 && bool.TryParse(parts[8], out bool pds)) PreserveDirectoryStructure = pds;
                if (parts.Length >= 10 && int.TryParse(parts[9], out int tfi)) TextureFormatIndex = tfi;
            }
        }
        catch { }
    }

    // ── Utility ────────────────────────────────────────────────────
    
    private List<string> FindGameVpks(string? currentFilePath)
    {
        var vpkList = new List<string>();
        if (!string.IsNullOrEmpty(GameDirectory) && System.IO.Directory.Exists(GameDirectory))
            vpkList.AddRange(System.IO.Directory.GetFiles(GameDirectory, "*_dir.vpk", System.IO.SearchOption.AllDirectories));

        string? currentDir = !string.IsNullOrEmpty(currentFilePath) ? System.IO.Path.GetDirectoryName(currentFilePath) : null;
        if (currentDir != null)
        {
            // Auto-detect from Steam Library path if inside workshop
            string tempDir = currentDir;
            string? steamAppsDir = null;
            while (!string.IsNullOrEmpty(tempDir))
            {
                if (System.IO.Path.GetFileName(tempDir).Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                {
                    steamAppsDir = tempDir;
                    break;
                }
                var parentInfo = System.IO.Directory.GetParent(tempDir);
                tempDir = parentInfo?.FullName ?? "";
            }

            if (steamAppsDir != null)
            {
                string commonDir = System.IO.Path.Combine(steamAppsDir, "common");
                if (System.IO.Directory.Exists(commonDir))
                {
                    try {
                        var hl2 = System.IO.Path.Combine(commonDir, "Half-Life 2");
                        if (System.IO.Directory.Exists(hl2)) vpkList.AddRange(System.IO.Directory.GetFiles(hl2, "*_dir.vpk", System.IO.SearchOption.TopDirectoryOnly));
                        
                        var ep2 = System.IO.Path.Combine(commonDir, "Half-Life 2", "ep2");
                        if (System.IO.Directory.Exists(ep2)) vpkList.AddRange(System.IO.Directory.GetFiles(ep2, "*_dir.vpk", System.IO.SearchOption.TopDirectoryOnly));

                        var gmod = System.IO.Path.Combine(commonDir, "GarrysMod", "sourceengine");
                        if (System.IO.Directory.Exists(gmod)) vpkList.AddRange(System.IO.Directory.GetFiles(gmod, "*_dir.vpk", System.IO.SearchOption.TopDirectoryOnly));
                    } catch {}
                }
            }
            else
            {
                string? parentDir = System.IO.Directory.GetParent(currentDir)?.FullName;
                if (parentDir != null)
                {
                    try 
                    {
                        var allVpks = System.IO.Directory.GetFiles(parentDir, "*_dir.vpk", System.IO.SearchOption.AllDirectories);
                        vpkList.AddRange(allVpks);
                    }
                    catch { }
                }
            }
        }

        vpkList = vpkList.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        string curDirName = currentDir ?? "";
        vpkList.Sort((a, b) => 
        {
            bool aInCur = a.StartsWith(curDirName, StringComparison.OrdinalIgnoreCase);
            bool bInCur = b.StartsWith(curDirName, StringComparison.OrdinalIgnoreCase);
            if (aInCur && !bInCur) return -1;
            if (!aInCur && bInCur) return 1;

            bool aIsHl2 = a.IndexOf("hl2", StringComparison.OrdinalIgnoreCase) >= 0;
            bool bIsHl2 = b.IndexOf("hl2", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!aIsHl2 && bIsHl2) return -1;
            if (aIsHl2 && !bIsHl2) return 1;

            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });

        return vpkList;
    }

    // ── Asset Preview ──────────────────────────────────────────────

    private async Task LoadPreviewAsync(AssetTreeNode? node)
    {
        StopAudio();
        PreviewImage = null;
        PreviewModel3D = null;

        if (node?.Asset == null)
        {
            PreviewType = "none";
            PreviewText = "Select an asset to preview";
            return;
        }

        var asset = node.Asset;
        PreviewText = asset.FileName;

        try
        {
            byte[]? data = await Task.Run(() => ResolveAssetData(asset.FullPath));
            if (data == null)
            {
                PreviewType = "text";
                PreviewText = $"Cannot resolve: {asset.FileName}";
                return;
            }

            string ext = asset.Extension;

            // Texture preview
            if (ext == ".vtf")
            {
                var pngBytes = TextureConverter.VtfToPng(data);
                if (pngBytes != null)
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = new MemoryStream(pngBytes);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    PreviewImage = bmp;
                    PreviewType = "image";
                    PreviewText = $"{asset.FileName} — {((BitmapImage)PreviewImage).PixelWidth}x{((BitmapImage)PreviewImage).PixelHeight}";
                }
                else
                {
                    PreviewType = "text";
                    PreviewText = $"Cannot decode: {asset.FileName}";
                }
                return;
            }

            // Audio preview
            if (ext == ".wav")
            {
                // Save to temp file for SoundPlayer
                string tempPath = Path.Combine(Path.GetTempPath(), "su_preview.wav");
                await File.WriteAllBytesAsync(tempPath, data);
                _soundPlayer = new System.Media.SoundPlayer(tempPath);
                PreviewType = "audio";
                PreviewText = $"🔊 {asset.FileName} ({data.Length / 1024} KB)";
                return;
            }

            // Model preview (wireframe)
            if (ext == ".mdl")
            {
                await Task.Run(() => LoadModelPreview(asset, data));
                return;
            }

            // VMT material preview
            if (ext == ".vmt")
            {
                try
                {
                    string vmtText = System.Text.Encoding.UTF8.GetString(data);
                    PreviewType = "vmt";
                    PreviewText = $"📋 {asset.FileName}\n─────────────────\n{vmtText}";
                }
                catch
                {
                    PreviewType = "text";
                    PreviewText = $"Cannot decode VMT: {asset.FileName}";
                }
                return;
            }

            // Text-like files
            if (ext == ".txt" || ext == ".cfg" || ext == ".res" || ext == ".vcd")
            {
                try
                {
                    string text = System.Text.Encoding.UTF8.GetString(data);
                    PreviewType = "vmt"; // reuse scrollable text viewer
                    PreviewText = $"📄 {asset.FileName}\n─────────────────\n{text}";
                }
                catch
                {
                    PreviewType = "text";
                    PreviewText = $"{asset.FileName} — {data.Length / 1024} KB";
                }
                return;
            }

            // Generic fallback: show raw code or hex dump
            try
            {
                // Check if data looks like text (low ratio of non-printable characters)
                int nonPrintable = 0;
                int sampleSize = Math.Min(data.Length, 1024);
                for (int i = 0; i < sampleSize; i++)
                {
                    byte b = data[i];
                    if (b < 0x20 && b != 0x0A && b != 0x0D && b != 0x09) // not newline, carriage return, or tab
                        nonPrintable++;
                }

                bool isBinary = sampleSize > 0 && (double)nonPrintable / sampleSize > 0.1;

                if (!isBinary)
                {
                    // Display as raw text
                    string rawText = System.Text.Encoding.UTF8.GetString(data);
                    // Truncate very large text for preview
                    if (rawText.Length > 8000) rawText = rawText.Substring(0, 8000) + "\n\n... (truncated)";
                    PreviewType = "vmt"; // reuse scrollable text viewer
                    PreviewText = $"📄 {asset.FileName} ({data.Length / 1024} KB)\n─────────────────\n{rawText}";
                }
                else
                {
                    // Display hex dump of first 512 bytes
                    int hexLen = Math.Min(data.Length, 512);
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < hexLen; i += 16)
                    {
                        sb.Append($"{i:X8}  ");
                        int lineEnd = Math.Min(i + 16, hexLen);
                        for (int j = i; j < lineEnd; j++)
                            sb.Append($"{data[j]:X2} ");
                        // Pad if line is short
                        for (int j = lineEnd; j < i + 16; j++)
                            sb.Append("   ");
                        sb.Append(" ");
                        for (int j = i; j < lineEnd; j++)
                        {
                            char c = (char)data[j];
                            sb.Append(c >= 0x20 && c < 0x7F ? c : '.');
                        }
                        sb.AppendLine();
                    }
                    if (data.Length > 512)
                        sb.AppendLine($"\n... ({data.Length - 512} more bytes)");

                    PreviewType = "vmt"; // reuse scrollable text viewer
                    PreviewText = $"🔧 {asset.FileName} ({data.Length / 1024} KB) [Binary]\n─────────────────\n{sb}";
                }
            }
            catch
            {
                PreviewType = "text";
                PreviewText = $"{asset.FileName} — {data.Length / 1024} KB";
            }
        }
        catch (Exception ex)
        {
            PreviewType = "text";
            PreviewText = $"Preview error: {ex.Message}";
        }
    }

    private void LoadModelPreview(AssetInfo asset, byte[] mdlData)
    {
        string basePath = asset.FullPath;
        string vvdPath = Path.ChangeExtension(basePath, ".vvd");
        string vtxPath = Path.ChangeExtension(basePath, ".dx90.vtx");

        byte[]? vvdData = ResolveAssetData(vvdPath);
        byte[]? vtxData = ResolveAssetData(vtxPath);

        if (vtxData == null)
        {
            vtxData = ResolveAssetData(Path.ChangeExtension(basePath, ".dx80.vtx"));
            if (vtxData == null)
                vtxData = ResolveAssetData(Path.ChangeExtension(basePath, ".sw.vtx"));
            if (vtxData == null)
                vtxData = ResolveAssetData(Path.ChangeExtension(basePath, ".vtx"));
        }

        if (vvdData == null || vtxData == null)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                PreviewType = "text";
                PreviewText = $"Missing VVD/VTX for {asset.FileName}";
            });
            return;
        }

        try
        {
            var reader = new MdlReader();
            var model = reader.Load(mdlData, vvdData, vtxData);

            if (!model.IsValid || model.BodyParts.Count == 0)
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    PreviewType = "text";
                    PreviewText = $"Invalid model: {asset.FileName}";
                });
                return;
            }

            // Collect all vertices and indices from all meshes
            var allPositions = new Point3DCollection();
            var allIndices = new Int32Collection();
            int totalVerts = 0;
            int totalTris = 0;
            int vertexOffset = 0;

            foreach (var bodyPart in model.BodyParts)
            {
                foreach (var mdlMesh in bodyPart.Meshes)
                {
                    foreach (var v in mdlMesh.Vertices)
                        allPositions.Add(new Point3D(v.PosX, v.PosY, v.PosZ));

                    for (int i = 0; i + 2 < mdlMesh.Indices.Count; i += 3)
                    {
                        allIndices.Add(mdlMesh.Indices[i] + vertexOffset);
                        allIndices.Add(mdlMesh.Indices[i + 1] + vertexOffset);
                        allIndices.Add(mdlMesh.Indices[i + 2] + vertexOffset);
                    }

                    totalVerts += mdlMesh.Vertices.Count;
                    totalTris += mdlMesh.Indices.Count / 3;
                    vertexOffset += mdlMesh.Vertices.Count;
                }
            }

            if (allPositions.Count < 3) 
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    PreviewType = "text";
                    PreviewText = $"Empty model: {asset.FileName}";
                });
                return;
            }

            // Compute model bounds for auto-centering
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var p in allPositions)
            {
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
            }
            var center = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
            double radius = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ)) / 2;
            if (radius < 1) radius = 50;

            // Cache model data for render mode switching
            _lastModelData = model;
            _lastModelAsset = asset;
            _lastMdlData = mdlData;

            // Build model with current render mode
            var meshGroup = BuildModelGroup(model, asset, allPositions, allIndices, totalVerts, totalTris);

            // Material info string
            string matInfo = "";
            if (model.MaterialNames.Count > 0)
                matInfo = $" | Materials: {string.Join(", ", model.MaterialNames.Take(4))}";

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                PreviewModel3D = meshGroup;
                PreviewType = "model";
                PreviewText = $"🗃 {asset.FileName} — {totalVerts} verts, {totalTris} tris{matInfo}";

                if (System.Windows.Application.Current?.MainWindow is MainWindow mw)
                    mw.SetCameraTarget(center, radius);
            });
        }
        catch (Exception ex)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                PreviewType = "text";
                PreviewText = $"Model error: {ex.Message}";
            });
        }
    }

    private Model3DGroup BuildModelGroup(MdlModelData model, AssetInfo asset, 
        Point3DCollection positions, Int32Collection indices, int totalVerts, int totalTris)
    {
        var meshGroup = new Model3DGroup();
        bool textured = ModelRenderMode == "textured";
        
        if (textured)
        {
            // Per-mesh rendering with textures
            int vertOff = 0;
            int meshIdx = 0;
            foreach (var bodyPart in model.BodyParts)
            {
                foreach (var mdlMesh in bodyPart.Meshes)
                {
                    var meshPositions = new Point3DCollection();
                    var meshIndices = new Int32Collection();
                    var meshUVs = new System.Windows.Media.PointCollection();

                    foreach (var v in mdlMesh.Vertices)
                    {
                        meshPositions.Add(new Point3D(v.PosX, v.PosY, v.PosZ));
                        meshUVs.Add(new System.Windows.Point(v.TexU, v.TexV));
                    }

                    for (int i = 0; i + 2 < mdlMesh.Indices.Count; i += 3)
                    {
                        meshIndices.Add(mdlMesh.Indices[i]);
                        meshIndices.Add(mdlMesh.Indices[i + 1]);
                        meshIndices.Add(mdlMesh.Indices[i + 2]);
                    }

                    var geo = new MeshGeometry3D 
                    { 
                        Positions = meshPositions, 
                        TriangleIndices = meshIndices,
                        TextureCoordinates = meshUVs
                    };
                    geo.Freeze();

                    // Try to load texture for this mesh
                    Material mat = TryLoadMeshTexture(mdlMesh, model, asset);
                    if (mat == null)
                    {
                        // Use unique color per mesh so they're visually distinguishable
                        byte r = (byte)(80 + (meshIdx * 37) % 150);
                        byte g = (byte)(100 + (meshIdx * 53) % 120);
                        byte b = (byte)(60 + (meshIdx * 71) % 160);
                        mat = new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)));
                    }
                    if (mat.CanFreeze) mat.Freeze();

                    var geoModel = new GeometryModel3D(geo, mat) { BackMaterial = mat };
                    geoModel.Freeze();
                    meshGroup.Children.Add(geoModel);
                    meshIdx++;
                }
            }
        }
        else
        {
            // Solid color — single merged mesh
            var mesh = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
            mesh.Freeze();

            var mat = new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xBB, 0xE2, 0x16)));
            mat.Freeze();

            var geoModel = new GeometryModel3D(mesh, mat) { BackMaterial = mat };
            geoModel.Freeze();
            meshGroup.Children.Add(geoModel);
        }

        meshGroup.Children.Add(new AmbientLight(Colors.White));
        meshGroup.Freeze();
        return meshGroup;
    }

    private Material? TryLoadMeshTexture(MdlMesh mdlMesh, MdlModelData model, AssetInfo asset)
    {
        try
        {
            string matName = mdlMesh.MaterialName;
            if (string.IsNullOrEmpty(matName)) return null;

            // Try to find VMT first
            foreach (var matPath in model.MaterialPaths)
            {
                string vmtPath = $"materials/{matPath}{matName}.vmt".Replace("//", "/");
                byte[]? vmtData = ResolveAssetData(vmtPath);
                
                // Fallback: If missing and path lacks a trailing slash, try adding it
                if (vmtData == null && !string.IsNullOrEmpty(matPath) && !matPath.EndsWith("/") && !matPath.EndsWith("\\"))
                {
                    string fixedVmtPath = $"materials/{matPath}/{matName}.vmt".Replace("//", "/");
                    vmtData = ResolveAssetData(fixedVmtPath);
                }

                string? vtfPathToLoad = null;

                if (vmtData != null)
                {
                    try
                    {
                        var matInfo = SourceUnpack.Core.Formats.Vmt.VmtReader.Parse(vmtData);
                        if (!string.IsNullOrEmpty(matInfo.BaseTexture))
                        {
                            vtfPathToLoad = $"materials/{matInfo.BaseTexture}.vtf".Replace("//", "/");
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(vtfPathToLoad))
                {
                    vtfPathToLoad = $"materials/{matPath}{matName}.vtf".Replace("//", "/");
                    
                    // Same fallback for direct VTF lookup
                    if (!string.IsNullOrEmpty(matPath) && !matPath.EndsWith("/") && !matPath.EndsWith("\\"))
                    {
                        byte[]? fallbackCheck = ResolveAssetData(vtfPathToLoad);
                        if (fallbackCheck == null)
                        {
                            vtfPathToLoad = $"materials/{matPath}/{matName}.vtf".Replace("//", "/");
                        }
                    }
                }

                byte[]? vtfData = ResolveAssetData(vtfPathToLoad);
                if (vtfData != null)
                {
                    var mat = CreateMaterialFromVtf(vtfData);
                    if (mat != null) return mat;
                }
            }

            // Try without material path prefix
            string directVmt = $"materials/{matName}.vmt";
            byte[]? directVmtData = ResolveAssetData(directVmt);
            string? directVtfPath = null;
            if (directVmtData != null)
            {
                try
                {
                    var matInfo = SourceUnpack.Core.Formats.Vmt.VmtReader.Parse(directVmtData);
                    if (!string.IsNullOrEmpty(matInfo.BaseTexture))
                    {
                        directVtfPath = $"materials/{matInfo.BaseTexture}.vtf".Replace("//", "/");
                    }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(directVtfPath))
            {
                directVtfPath = $"materials/{matName}.vtf";
            }

            byte[]? directData = ResolveAssetData(directVtfPath);
            if (directData != null)
            {
                var mat = CreateMaterialFromVtf(directData);
                if (mat != null) return mat;
            }
        }
        catch { }

        return null;
    }

    private Material? CreateMaterialFromVtf(byte[] vtfData)
    {
        byte[]? pngBytes = TextureConverter.VtfToPng(vtfData);
        if (pngBytes != null)
        {
            var bmp = new BitmapImage();
            using (var ms = new System.IO.MemoryStream(pngBytes))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
            }
            var brush = new ImageBrush(bmp) { ViewportUnits = BrushMappingMode.Absolute };
            brush.Freeze();
            return new DiffuseMaterial(brush);
        }
        return null;
    }

    private void ToggleRenderMode(string? mode)
    {
        if (mode == null || mode == ModelRenderMode) return;
        ModelRenderMode = mode;

        // Rebuild current model with new render mode if we have cached data
        if (_lastModelData != null && _lastModelAsset != null && PreviewType == "model")
        {
            _ = Task.Run(() =>
            {
                var allPositions = new Point3DCollection();
                var allIndices = new Int32Collection();
                int totalVerts = 0, totalTris = 0, vertexOffset = 0;

                foreach (var bodyPart in _lastModelData.BodyParts)
                {
                    foreach (var mdlMesh in bodyPart.Meshes)
                    {
                        foreach (var v in mdlMesh.Vertices)
                            allPositions.Add(new Point3D(v.PosX, v.PosY, v.PosZ));

                        for (int i = 0; i + 2 < mdlMesh.Indices.Count; i += 3)
                        {
                            allIndices.Add(mdlMesh.Indices[i] + vertexOffset);
                            allIndices.Add(mdlMesh.Indices[i + 1] + vertexOffset);
                            allIndices.Add(mdlMesh.Indices[i + 2] + vertexOffset);
                        }

                        totalVerts += mdlMesh.Vertices.Count;
                        totalTris += mdlMesh.Indices.Count / 3;
                        vertexOffset += mdlMesh.Vertices.Count;
                    }
                }

                var meshGroup = BuildModelGroup(_lastModelData, _lastModelAsset, allPositions, allIndices, totalVerts, totalTris);

                string matInfo = "";
                if (_lastModelData.MaterialNames.Count > 0)
                    matInfo = $" | Materials: {string.Join(", ", _lastModelData.MaterialNames.Take(4))}";

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    PreviewModel3D = meshGroup;
                    PreviewText = $"🗃 {_lastModelAsset.FileName} — {totalVerts} verts, {totalTris} tris [{ModelRenderMode}]{matInfo}";
                });
            });
        }
    }

    private void UncheckAll()
    {
        foreach (var node in AssetTree)
            UncheckNode(node);
    }

    private void UncheckNode(AssetTreeNode node)
    {
        node.IsChecked = false;
        foreach (var child in node.Children)
            UncheckNode(child);
    }

    private void CheckAll()
    {
        foreach (var node in AssetTree)
            CheckNode(node);
    }

    private void CheckNode(AssetTreeNode node)
    {
        node.IsChecked = true;
        foreach (var child in node.Children)
            CheckNode(child);
    }

    private void ShowAbout()
    {
        System.Windows.MessageBox.Show(
            "SourceUnpack v1.8\n\n" +
            "A Source Engine asset extractor and converter.\n" +
            "Supports BSP, VPK, and GMA files.\n\n" +
            "v1.8 Features:\n" +
            "• JPG texture export support\n" +
            "• Asset browser filtering state fixes\n" +
            "• Drag & Drop file loading\n" +
            "• glTF/GLB model export format\n" +
            "• Export profile presets (Save/Load)\n" +
            "• Batch processing queue\n" +
            "• Improved MDL companion file resolution\n\n" +
            "Core Features:\n" +
            "• Extract textures, models, sounds from Source maps\n" +
            "• Preview assets (VTF, WAV, MDL) with 3D viewport\n" +
            "• Convert MDL → OBJ/FBX/glTF/GLB, VTF → PNG, BSP → VMF\n" +
            "• SteamCMD workshop downloader\n" +
            "• Asset browser with search/filter\n\n" +
            "GitHub: github.com/Azocke/SourceUnpack\n\n" +
            "by Highwayz\n\n" +
            "Shortcuts:\n" +
            "Ctrl+O = Open BSP | Ctrl+Shift+O = Open VPK\n" +
            "Ctrl+G = Open GMA | Ctrl+E = Extract All\n" +
            "Ctrl+Shift+E = Extract Selected",
            "About SourceUnpack",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    // ══════════════════════════════════════
    // ██  DRAG & DROP
    // ══════════════════════════════════════

    /// <summary>
    /// Handle a file dropped onto the main window.
    /// Automatically opens BSP/VPK/GMA files or quick-converts MDL/VTF.
    /// </summary>
    public void HandleFileDrop(string filePath)
    {
        if (IsBusy) return;
        string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
        switch (ext)
        {
            case ".bsp":
                OpenBspDirect(filePath);
                break;
            case ".vpk":
                OpenVpkDirect(filePath);
                break;
            case ".gma":
                OpenGmaDirect(filePath);
                break;
            case ".mdl":
                _ = ConvertMdlDirectFromPath(filePath);
                break;
            case ".vtf":
                _ = ConvertVtfDirectFromPath(filePath);
                break;
            default:
                AppendLog($"Unsupported file type: {ext}");
                break;
        }
    }

    private void OpenBspDirect(string path)
    {
        try
        {
            IsBusy = true;
            StatusText = "Loading BSP...";
            AppendLog($"Opening (Drag & Drop): {path}");

            // Clear previous state
            _currentBsp?.Dispose();
            _currentVpk?.Dispose(); _currentVpk = null; _currentVpkPath = string.Empty;
            _currentGma?.Dispose(); _currentGma = null; _currentGmaPath = string.Empty;
            _currentBsp = new BspReader(path);

            if (!_currentBsp.IsValid)
            {
                AppendLog("ERROR: Not a valid Source Engine BSP file.");
                StatusText = "Invalid BSP file";
                IsBusy = false;
                return;
            }

            _cachedPakFiles = _currentBsp.ExtractPakfile();

            BspFilePath = path;
            InfoLabel = "Map Info";
            MapName = System.IO.Path.GetFileNameWithoutExtension(path);
            BspVersion = _currentBsp.Header.Version;
            MapRevision = _currentBsp.Header.MapRevision;
            var entities = _currentBsp.ParseEntities();
            EntityCount = entities.Count;
            SkyboxName = _currentBsp.GetSkyboxName() ?? "(none)";

            InfoField1Label = "Name: ";
            InfoField1Value = MapName;
            InfoField2Label = "Ver: ";
            InfoField2Value = BspVersion.ToString();
            InfoField3Label = "Ents: ";
            InfoField3Value = EntityCount.ToString();

            StatusText = "Discovering assets...";
            _allAssets = AssetDiscovery.DiscoverFromBsp(_currentBsp);
            BuildAssetTree();
            StatusText = $"Loaded {MapName} — {_allAssets.Count} assets discovered";
            AssetCountText = $"{_allAssets.Count} assets";
            _ = MountGameVpksAsync(path);
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            StatusText = "Error loading BSP";
        }
        finally { IsBusy = false; }
    }

    private void OpenVpkDirect(string vpkPath)
    {
        try
        {
            IsBusy = true;
            StatusText = "Loading VPK...";

            // Handle multi-part VPKs
            if (System.Text.RegularExpressions.Regex.IsMatch(vpkPath, @"_\d{3}\.vpk$"))
            {
                string dirVpk = System.Text.RegularExpressions.Regex.Replace(vpkPath, @"_\d{3}\.vpk$", "_dir.vpk");
                if (System.IO.File.Exists(dirVpk)) vpkPath = dirVpk;
            }

            _currentBsp?.Dispose(); _currentBsp = null; BspFilePath = string.Empty;
            _currentGma?.Dispose(); _currentGma = null; _currentGmaPath = string.Empty;
            _currentVpk?.Dispose();
            _currentVpk = new VpkReader(vpkPath);
            _currentVpkPath = vpkPath;

            MapName = System.IO.Path.GetFileNameWithoutExtension(vpkPath);
            BspVersion = _currentVpk.Version;
            InfoLabel = "VPK Info";
            InfoField1Label = "Files: "; InfoField1Value = _currentVpk.Entries.Count.ToString();
            InfoField2Label = "Ver: "; InfoField2Value = _currentVpk.Version.ToString();
            InfoField3Label = "Archives: "; InfoField3Value = "0";

            _allAssets = AssetDiscovery.DiscoverFromVpk(_currentVpk);
            AssetCountText = $"{_allAssets.Count} assets";
            BuildAssetTree();
            StatusText = $"Loaded {MapName} — {_allAssets.Count} assets discovered";
            _ = MountGameVpksAsync(vpkPath);
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            StatusText = "Error loading VPK";
        }
        finally { IsBusy = false; }
    }

    private void OpenGmaDirect(string gmaPath)
    {
        try
        {
            IsBusy = true;
            StatusText = "Loading GMA...";

            _currentBsp?.Dispose(); _currentBsp = null; BspFilePath = string.Empty;
            _currentVpk?.Dispose(); _currentVpk = null; _currentVpkPath = string.Empty;
            _currentGma?.Dispose();
            _currentGma = new GmaReader(gmaPath);
            _currentGmaPath = gmaPath;

            if (!_currentGma.IsValid) { StatusText = "Invalid GMA"; IsBusy = false; return; }

            InfoLabel = "Addon Info";
            MapName = _currentGma.AddonName;
            InfoField1Label = "Name: "; InfoField1Value = _currentGma.AddonName;
            InfoField2Label = "Author: "; InfoField2Value = _currentGma.AddonAuthor;
            InfoField3Label = "Desc: "; InfoField3Value = _currentGma.AddonDescription;

            _allAssets = AssetDiscovery.DiscoverFromGma(_currentGma);
            AssetCountText = $"{_allAssets.Count} assets";
            BuildAssetTree();
            StatusText = $"Loaded addon '{_currentGma.AddonName}' — {_allAssets.Count} assets discovered";
            _ = MountGameVpksAsync(gmaPath);
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            StatusText = "Error loading GMA";
        }
        finally { IsBusy = false; }
    }

    private async Task ConvertMdlDirectFromPath(string mdlPath)
    {
        try
        {
            IsBusy = true;
            string baseName = System.IO.Path.GetFileNameWithoutExtension(mdlPath);
            string? dir = System.IO.Path.GetDirectoryName(mdlPath);
            if (dir == null) return;

            StatusText = $"Converting {baseName}.mdl...";
            AppendLog($"Drag & Drop MDL → OBJ: {mdlPath}");

            string vvdPath = System.IO.Path.ChangeExtension(mdlPath, ".vvd");
            string vtxPath = System.IO.Path.Combine(dir, baseName + ".dx90.vtx");
            if (!System.IO.File.Exists(vtxPath)) vtxPath = System.IO.Path.Combine(dir, baseName + ".dx80.vtx");
            if (!System.IO.File.Exists(vtxPath)) vtxPath = System.IO.Path.Combine(dir, baseName + ".sw.vtx");
            if (!System.IO.File.Exists(vtxPath)) vtxPath = System.IO.Path.Combine(dir, baseName + ".vtx");

            byte[]? mdlData = System.IO.File.Exists(mdlPath) ? System.IO.File.ReadAllBytes(mdlPath) : null;
            byte[]? vvdData = System.IO.File.Exists(vvdPath) ? System.IO.File.ReadAllBytes(vvdPath) : null;
            byte[]? vtxData = System.IO.File.Exists(vtxPath) ? System.IO.File.ReadAllBytes(vtxPath) : null;

            if (mdlData == null || vvdData == null || vtxData == null)
            {
                AppendLog("ERROR: Missing VVD/VTX sibling files");
                StatusText = "Missing VVD/VTX files";
                return;
            }

            await Task.Run(() =>
            {
                var reader = new MdlReader();
                var model = reader.Load(mdlData, vvdData, vtxData);
                if (!model.IsValid) { StatusText = "MDL parse error"; return; }

                if (ModelConverter.ExportObj(model, OutputDirectory, baseName))
                    StatusText = $"Converted {baseName}.mdl → OBJ";
                else
                    StatusText = "OBJ export error";
            });
        }
        catch (Exception ex) { AppendLog($"ERROR: {ex.Message}"); StatusText = "MDL conversion failed"; }
        finally { IsBusy = false; }
    }

    private async Task ConvertVtfDirectFromPath(string vtfPath)
    {
        try
        {
            IsBusy = true;
            string baseName = System.IO.Path.GetFileNameWithoutExtension(vtfPath);
            var texFormat = (TextureFormat)TextureFormatIndex;
            string texExt = TextureConverter.GetExtension(texFormat);
            StatusText = $"Converting {baseName}.vtf → {texExt.TrimStart('.').ToUpper()}...";

            await Task.Run(() =>
            {
                byte[] vtfData = System.IO.File.ReadAllBytes(vtfPath);
                string outPath = System.IO.Path.Combine(OutputDirectory, baseName + texExt);
                if (TextureConverter.VtfToFile(vtfData, outPath, texFormat))
                    StatusText = $"Converted {baseName}.vtf → {texExt.TrimStart('.').ToUpper()}";
                else
                    StatusText = "VTF decode error";
            });
        }
        catch (Exception ex) { AppendLog($"ERROR: {ex.Message}"); StatusText = "VTF conversion failed"; }
        finally { IsBusy = false; }
    }

    // ══════════════════════════════════════
    // ██  v1.7 — EXPORT PRESETS
    // ══════════════════════════════════════

    private void SaveExportPreset()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save Export Preset",
            Filter = "Preset Files (*.supreset)|*.supreset|All Files (*.*)|*.*",
            DefaultExt = ".supreset",
            FileName = "MyPreset"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var lines = new List<string>
            {
                $"GameDirectory={GameDirectory}",
                $"OutputDirectory={OutputDirectory}",
                $"ExtractGameDependencies={ExtractGameDependencies}",
                $"GenerateMissingPlaceholders={GenerateMissingPlaceholders}",
                $"SkipExistingFiles={SkipExistingFiles}",
                $"KeepMdlFile={KeepMdlFile}",
                $"PreserveDirectoryStructure={PreserveDirectoryStructure}",
                $"ModelConversionFormatIndex={ModelConversionFormatIndex}",
                $"CustomAssetsDirectory={CustomAssetsDirectory}",
                $"TextureFormatIndex={TextureFormatIndex}"
            };
            System.IO.File.WriteAllLines(dialog.FileName, lines);
            AppendLog($"Preset saved: {System.IO.Path.GetFileName(dialog.FileName)}");
            StatusText = "Preset saved";
        }
        catch (Exception ex) { AppendLog($"ERROR saving preset: {ex.Message}"); }
    }

    private void LoadExportPreset()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load Export Preset",
            Filter = "Preset Files (*.supreset)|*.supreset|All Files (*.*)|*.*",
            DefaultExt = ".supreset"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var lines = System.IO.File.ReadAllLines(dialog.FileName);
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines)
            {
                int eq = line.IndexOf('=');
                if (eq > 0) dict[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }

            if (dict.TryGetValue("GameDirectory", out var gd)) GameDirectory = gd;
            if (dict.TryGetValue("OutputDirectory", out var od)) OutputDirectory = od;
            if (dict.TryGetValue("ExtractGameDependencies", out var egd) && bool.TryParse(egd, out var egdv)) ExtractGameDependencies = egdv;
            if (dict.TryGetValue("GenerateMissingPlaceholders", out var gmp) && bool.TryParse(gmp, out var gmpv)) GenerateMissingPlaceholders = gmpv;
            if (dict.TryGetValue("SkipExistingFiles", out var sef) && bool.TryParse(sef, out var sefv)) SkipExistingFiles = sefv;
            if (dict.TryGetValue("KeepMdlFile", out var kmf) && bool.TryParse(kmf, out var kmfv)) KeepMdlFile = kmfv;
            if (dict.TryGetValue("PreserveDirectoryStructure", out var pds) && bool.TryParse(pds, out var pdsv)) PreserveDirectoryStructure = pdsv;
            if (dict.TryGetValue("ModelConversionFormatIndex", out var mcf) && int.TryParse(mcf, out var mcfv)) ModelConversionFormatIndex = mcfv;
            if (dict.TryGetValue("CustomAssetsDirectory", out var cad)) CustomAssetsDirectory = cad;
            if (dict.TryGetValue("TextureFormatIndex", out var tfi) && int.TryParse(tfi, out var tfiv)) TextureFormatIndex = tfiv;

            AppendLog($"Preset loaded: {System.IO.Path.GetFileName(dialog.FileName)}");
            StatusText = "Preset loaded";
        }
        catch (Exception ex) { AppendLog($"ERROR loading preset: {ex.Message}"); }
    }

    // ══════════════════════════════════════
    // ██  v1.7 — BATCH QUEUE
    // ══════════════════════════════════════

    private void AddToBatch()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add Files to Batch Queue",
            Filter = "Source Files (*.bsp;*.vpk;*.gma)|*.bsp;*.vpk;*.gma|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() != true) return;

        foreach (var file in dialog.FileNames)
        {
            if (!BatchQueue.Contains(file))
            {
                BatchQueue.Add(file);
                AppendLog($"Batch: Added {System.IO.Path.GetFileName(file)}");
            }
        }
        BatchQueueText = $"{BatchQueue.Count} file(s) in queue";
    }

    private async Task RunBatchQueue()
    {
        if (BatchQueue.Count == 0) return;

        try
        {
            IsBusy = true;
            int total = BatchQueue.Count;
            int current = 0;

            AppendLog($"\n═══ BATCH PROCESSING: {total} files ═══");

            // Snapshot queue to avoid modification during iteration
            var files = BatchQueue.ToList();

            foreach (var file in files)
            {
                current++;
                string fileName = System.IO.Path.GetFileName(file);
                StatusText = $"Batch [{current}/{total}]: {fileName}";
                ProgressValue = (double)current / total * 100.0;
                AppendLog($"\n──── [{current}/{total}] {fileName} ────");

                string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();

                try
                {
                    var options = new ConversionOptions
                    {
                        OutputDirectory = OutputDirectory,
                        PreserveDirectoryStructure = PreserveDirectoryStructure,
                        ExportSeparateMaterialMaps = true,
                        AssembleSkyboxCubemap = true,
                        ExtractGameDependencies = ExtractGameDependencies,
                        GenerateMissingPlaceholders = GenerateMissingPlaceholders,
                        SkipExistingFiles = SkipExistingFiles,
                        KeepMdlFile = KeepMdlFile,
                        ModelExportFormat = (ModelFormat)ModelConversionFormatIndex,
                        TextureOutputFormat = (TextureFormat)TextureFormatIndex
                    };

                    var pipeline = new ExtractionPipeline(options);
                    pipeline.LogMessage += (_, msg) => System.Windows.Application.Current?.Dispatcher.Invoke(() => AppendLog(msg));
                    pipeline.SystemMessage += (_, msg) => System.Windows.Application.Current?.Dispatcher.Invoke(() => AppendSystemLog(msg));

                    List<AssetInfo> assets;

                    switch (ext)
                    {
                        case ".bsp":
                            assets = pipeline.LoadBsp(file);
                            break;
                        case ".vpk":
                            pipeline.LoadVpk(file);
                            assets = new List<AssetInfo>();
                            // VPK batch: extract all discovered assets
                            var vpk = new VpkReader(file);
                            assets = AssetDiscovery.DiscoverFromVpk(vpk);
                            break;
                        case ".gma":
                            pipeline.LoadGma(file);
                            var gma = new GmaReader(file);
                            assets = AssetDiscovery.DiscoverFromGma(gma);
                            break;
                        default:
                            AppendLog($"  Skipping unsupported file type: {ext}");
                            continue;
                    }

                    // Mount game VPKs if configured
                    if (options.ExtractGameDependencies)
                    {
                        var vpkList = FindGameVpks(file);
                        foreach (var vpkFile in vpkList.Take(50))
                            pipeline.LoadVpk(vpkFile);
                    }

                    await pipeline.ExtractAsync(assets, CancellationToken.None);
                    AppendLog($"  ✓ Completed: {fileName} ({assets.Count} assets)");
                }
                catch (Exception ex)
                {
                    AppendLog($"  ✗ FAILED: {fileName} — {ex.Message}");
                }
            }

            AppendLog($"\n═══ BATCH COMPLETE: {total} files processed ═══");
            StatusText = $"Batch complete — {total} files processed";
            ProgressValue = 100;
            BatchQueue.Clear();
            BatchQueueText = "Queue complete";
        }
        catch (Exception ex)
        {
            AppendLog($"BATCH ERROR: {ex.Message}");
            StatusText = "Batch processing failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void PlayAudio()
    {
        try
        {
            _soundPlayer?.Play();
            IsAudioPlaying = true;
        }
        catch { }
    }

    private void StopAudio()
    {
        try
        {
            _soundPlayer?.Stop();
            _soundPlayer?.Dispose();
            _soundPlayer = null;
            IsAudioPlaying = false;
        }
        catch { }
    }

    /// <summary>
    /// Resolves raw asset data from the currently loaded BSP, VPK, or GMA.
    /// </summary>
    private byte[]? ResolveAssetData(string path)
    {
        // Normalize path separators
        path = path.Replace('\\', '/').TrimStart('/');

        // 1. BSP embedded files (pakfile lump → ZIP entries)
        if (_currentBsp != null)
        {
            try
            {
                // Use cached pakfile entries instead of re-extracting on every call
                var pakEntries = _cachedPakFiles ?? _currentBsp.ExtractPakfile();
                // Case-insensitive lookup
                var match = pakEntries.Keys.FirstOrDefault(k => 
                    string.Equals(k.Replace('\\', '/').TrimStart('/'), path, StringComparison.OrdinalIgnoreCase));
                if (match != null) return pakEntries[match];
            }
            catch { }
        }

        // 2. Main GMA entry
        if (_currentGma != null)
        {
            var entry = _currentGma.Entries.FirstOrDefault(e => 
                string.Equals(e.Path.Replace('\\', '/').TrimStart('/'), path, StringComparison.OrdinalIgnoreCase));
            if (entry != null)
            {
                return _currentGma.ExtractEntry(entry);
            }
        }

        // 2.5 Dependency GMAs
        foreach (var gma in _dependencyGmas.Values)
        {
            var entry = gma.Entries.FirstOrDefault(e => 
                string.Equals(e.Path.Replace('\\', '/').TrimStart('/'), path, StringComparison.OrdinalIgnoreCase));
            if (entry != null)
            {
                return gma.ExtractEntry(entry);
            }
        }

        // 3. VPK entries
        if (_currentVpk != null)
        {
            var entry = _currentVpk.FindEntry(path);
            if (entry != null)
            {
                return _currentVpk.ExtractEntry(entry);
            }
        }

        // 4. Mounted Game VPKs
        VpkReader[]? mountedCopy = null;
        lock (_mountedGameVpks)
        {
            mountedCopy = _mountedGameVpks.ToArray();
        }

        foreach (var vpk in mountedCopy)
        {
            var entry = vpk.FindEntry(path);
            if (entry != null)
            {
                return vpk.ExtractEntry(entry);
            }
        }

        // 5. Loose Files on Disk
        try
        {
            string diskPath = path.Replace('/', '\\');

            // Explicit Custom Assets Directories
            if (!string.IsNullOrWhiteSpace(CustomAssetsDirectory))
            {
                var paths = CustomAssetsDirectory.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var cp in paths)
                {
                    string p = cp.Trim();
                    if (System.IO.Directory.Exists(p)) // it's a folder
                    {
                        string physicalPath = System.IO.Path.Combine(p, diskPath);
                        if (System.IO.File.Exists(physicalPath)) return System.IO.File.ReadAllBytes(physicalPath);
                    }
                }
            }

            string? currentFileDir = null;
            if (!string.IsNullOrEmpty(BspFilePath)) currentFileDir = System.IO.Path.GetDirectoryName(BspFilePath);
            else if (!string.IsNullOrEmpty(_currentGmaPath)) currentFileDir = System.IO.Path.GetDirectoryName(_currentGmaPath);
            else if (!string.IsNullOrEmpty(_currentVpkPath)) currentFileDir = System.IO.Path.GetDirectoryName(_currentVpkPath);

            if (!string.IsNullOrEmpty(currentFileDir))
            {
                string physicalPath = System.IO.Path.Combine(currentFileDir, diskPath);
                if (System.IO.File.Exists(physicalPath)) return System.IO.File.ReadAllBytes(physicalPath);

                string? parentDir = System.IO.Directory.GetParent(currentFileDir)?.FullName;
                if (!string.IsNullOrEmpty(parentDir))
                {
                    physicalPath = System.IO.Path.Combine(parentDir, diskPath);
                    if (System.IO.File.Exists(physicalPath)) return System.IO.File.ReadAllBytes(physicalPath);
                }
            }

            if (!string.IsNullOrEmpty(GameDirectory) && System.IO.Directory.Exists(GameDirectory))
            {
                string physicalPath = System.IO.Path.Combine(GameDirectory, diskPath);
                if (System.IO.File.Exists(physicalPath)) return System.IO.File.ReadAllBytes(physicalPath);
            }
        }
        catch { }

        return null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public record SteamGameOption(string Name, int AppId);
