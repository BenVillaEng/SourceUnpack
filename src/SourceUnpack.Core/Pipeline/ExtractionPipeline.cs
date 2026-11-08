using SourceUnpack.Core.Conversion;
using SourceUnpack.Core.Formats.Bsp;
using SourceUnpack.Core.Formats.Gma;
using SourceUnpack.Core.Formats.Mdl;
using SourceUnpack.Core.Formats.Vmt;
using SourceUnpack.Core.Formats.Vpk;
using SourceUnpack.Core.Models;

namespace SourceUnpack.Core.Pipeline;

/// <summary>
/// Progress event arguments for extraction pipeline updates.
/// </summary>
public class ExtractionProgressEventArgs : EventArgs
{
    public int Current { get; set; }
    public int Total { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public double Percentage => Total > 0 ? (double)Current / Total * 100.0 : 0;
}

/// <summary>
/// Orchestrates the full asset extraction and conversion workflow.
/// Coordinates BSP/VPK/GMA reading, asset discovery, and conversion or original extraction.
/// </summary>
public class ExtractionPipeline : IDisposable
{
    public event EventHandler<ExtractionProgressEventArgs>? ProgressChanged;
    public event EventHandler<string>? LogMessage;
    public event EventHandler<string>? SystemMessage;

    private readonly ConversionOptions _options;
    private BspReader? _bsp;
    private string? _bspPath;
    private readonly List<VpkReader> _vpks = new();
    private Dictionary<string, byte[]> _pakFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<GmaReader> _gmas = new();
    private readonly Dictionary<string, byte[]> _gmaFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _customDirectories = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _extractedFiles = new();

    public ExtractionPipeline(ConversionOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Load a BSP file and discover all embedded assets.
    /// </summary>
    public List<AssetInfo> LoadBsp(string bspPath)
    {
        LogSystem($"Loading BSP: {Path.GetFileName(bspPath)}");
        _bspPath = bspPath;
        _bsp?.Dispose();
        _bsp = new BspReader(bspPath);

        if (!_bsp.IsValid)
        {
            LogSystem("ERROR: Invalid BSP file.");
            return new();
        }

        LogSystem($"BSP Version: {_bsp.Header.Version} | Revision: {_bsp.Header.MapRevision}");

        LogSystem("Extracting embedded pakfile...");
        _pakFiles = _bsp.ExtractPakfile();
        LogSystem($"Found {_pakFiles.Count} files in pakfile lump.");

        var assets = AssetDiscovery.DiscoverFromBsp(_bsp, bspPath);
        var stats = AssetDiscovery.GetStats(assets);
        LogSystem($"Discovered: {stats.textures} textures, {stats.models} models, {stats.sounds} sounds, {stats.skyboxes} skybox faces, {stats.other} other");

        return assets;
    }

    /// <summary>
    /// Load a BSP from a stream (e.g. from an embedded GMA map).
    /// </summary>
    public List<AssetInfo> LoadBsp(Stream stream, string virtualPath = "map.bsp")
    {
        LogSystem($"Loading embedded BSP: {Path.GetFileName(virtualPath)}");
        _bspPath = virtualPath;
        _bsp?.Dispose();
        _bsp = new BspReader(stream);

        if (!_bsp.IsValid)
        {
            LogSystem("ERROR: Invalid BSP data.");
            return new();
        }

        _pakFiles = _bsp.ExtractPakfile();
        LogSystem($"Found {_pakFiles.Count} files in pakfile lump.");

        var assets = AssetDiscovery.DiscoverFromBsp(_bsp, virtualPath);
        return assets;
    }

    /// <summary>
    /// Load a BSP from byte data.
    /// </summary>
    public List<AssetInfo> LoadBspData(byte[] bspData, string virtualPath = "map.bsp")
    {
        return LoadBsp(new MemoryStream(bspData), virtualPath);
    }

    /// <summary>
    /// Load a VPK game directory or specific file.
    /// </summary>
    public void LoadVpk(string vpkPath)
    {
        try
        {
            if (_vpks.Any(v => string.Equals(v.FilePath, vpkPath, StringComparison.OrdinalIgnoreCase)))
                return;

            LogSystem($"Mounting VPK: {Path.GetFileName(vpkPath)}");
            var vpk = new VpkReader(vpkPath);
            _vpks.Add(vpk);
            LogSystem($"  Mounted v{vpk.Version} — {vpk.Entries.Count} entries");
        }
        catch (Exception ex)
        {
            LogSystem($"  ERROR mounting VPK: {ex.Message}");
        }
    }

    /// <summary>
    /// Unload all VPKs.
    /// </summary>
    public void ClearVpks()
    {
        foreach (var vpk in _vpks) vpk.Dispose();
        _vpks.Clear();
        LogSystem("Unmounted all VPKs.");
    }

    /// <summary>
    /// Load a GMA (Garry's Mod Addon) archive.
    /// Uses on-demand streaming to minimize memory usage.
    /// </summary>
    public void LoadGma(string gmaPath)
    {
        try
        {
            if (!File.Exists(gmaPath)) return;
            if (_gmas.Any(g => string.Equals(g.FilePath, gmaPath, StringComparison.OrdinalIgnoreCase)))
                return;

            LogSystem($"Mounting GMA: {Path.GetFileName(gmaPath)}");
            var gma = new GmaReader(gmaPath);

            if (!gma.IsValid)
            {
                LogSystem("ERROR: Invalid GMA file.");
                gma.Dispose();
                return;
            }

            _gmas.Add(gma);
            LogSystem($"  Mounted GMA: '{gma.AddonName}' — {gma.Entries.Count} entries");
        }
        catch (Exception ex)
        {
            LogSystem($"  ERROR mounting GMA: {ex.Message}");
        }
    }

    /// <summary>
    /// Load raw files from a GMA in-memory dictionary.
    /// </summary>
    public void LoadGmaFiles(Dictionary<string, byte[]> files)
    {
        foreach (var file in files)
        {
            string clean = file.Key.Replace('\\', '/').TrimStart('/');
            _gmaFiles[clean] = file.Value;
        }
    }

    /// <summary>
    /// Load a loose directory to use for resolving custom assets (e.g. from an addon or workshop download).
    /// Automatically mounts any embedded .gma and *_dir.vpk files inside it.
    /// </summary>
    public void LoadDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            LogSystem($"  ERROR mounting Custom Folder: Directory not found - {directoryPath}");
            return;
        }

        if (!_customDirectories.Contains(directoryPath, StringComparer.OrdinalIgnoreCase))
        {
            _customDirectories.Add(directoryPath);
            LogSystem($"Mounted Custom Folder: {directoryPath}");
        }

        // Auto-mount any GMAs inside this directory (e.g. garrysmod/addons or workshop)
        try
        {
            var gmaFiles = Directory.GetFiles(directoryPath, "*.gma", SearchOption.AllDirectories);
            foreach (var gma in gmaFiles.Take(30))
            {
                LoadGma(gma);
            }

            var vpkFiles = Directory.GetFiles(directoryPath, "*_dir.vpk", SearchOption.AllDirectories);
            foreach (var vpk in vpkFiles.Take(30))
            {
                LoadVpk(vpk);
            }
        }
        catch { }
    }

    /// <summary>
    /// Extract and convert or unpack selected assets.
    /// </summary>
    public async Task ExtractAsync(List<AssetInfo> assets, CancellationToken cancellationToken = default)
    {
        int total = assets.Count;
        int current = 0;
        var progressLock = new object();
        _extractedFiles.Clear();

        string modeDesc = _options.ExtractOriginalAssets
            ? "ORIGINAL VALVE FORMATS (.vtf, .mdl+companions, .vmt, .nav)"
            : $"CONVERTED FORMATS (Models: {_options.ModelExportFormat}, Textures: {_options.TextureOutputFormat})";

        Log($"Starting extraction of {total} assets to: {_options.OutputDirectory}");
        Log($"Mode: {modeDesc}");
        Log($"Using {Environment.ProcessorCount} parallel workers for maximum CPU utilization.");
        Directory.CreateDirectory(_options.OutputDirectory);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(assets, parallelOptions, (asset, ct) =>
        {
            int progress = Interlocked.Increment(ref current);
            lock (progressLock)
            {
                ReportProgress(progress, total, asset.FullPath, $"Processing {asset.FileName}");
            }

            try
            {
                switch (asset.Type)
                {
                    case AssetType.Texture:
                        ExtractTexture(asset);
                        break;
                    case AssetType.Material:
                        ExtractMaterial(asset, ct);
                        break;
                    case AssetType.Sound:
                        ExtractSound(asset);
                        break;
                    case AssetType.Model:
                        ExtractModel(asset, ct);
                        break;
                    case AssetType.Skybox:
                        ExtractSkybox(asset);
                        break;
                    case AssetType.NavMesh:
                    default:
                        ExtractRaw(asset);
                        break;
                }
            }
            catch (Exception ex)
            {
                lock (progressLock)
                {
                    Log($"  ERROR: {asset.FullPath} — {ex.Message}");
                }
            }
            return ValueTask.CompletedTask;
        });

        Log($"Extraction complete. {current}/{total} assets processed.");
    }

    private void ExtractTexture(AssetInfo asset)
    {
        bool isOriginal = _options.ExtractOriginalAssets || _options.TextureOutputFormat == TextureFormat.Original;
        string texExt = isOriginal ? ".vtf" : TextureConverter.GetExtension(_options.TextureOutputFormat);
        string outputPath = GetOutputPath(asset.FullPath, texExt);

        if (!_extractedFiles.TryAdd(outputPath.ToLowerInvariant(), true)) return;

        if (_options.SkipExistingFiles && File.Exists(outputPath))
        {
            Log($"  SKIP: {asset.FullPath} (already exists on disk)");
            return;
        }

        byte[]? data = GetAssetData(asset.FullPath, out string sourceDescription);
        if (data == null)
        {
            if (_options.GenerateMissingPlaceholders && !isOriginal)
            {
                byte[] placeholder = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z/C/HgAGgwJ/lK3Q6wAAAABJRU5ErkJggg==");
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllBytes(outputPath, placeholder);
                Log($"  PLACEHOLDER: {asset.FullPath} → {Path.GetFileName(outputPath)}");
            }
            else
            {
                Log($"  SKIP: {asset.FullPath} (not found)");
            }
            return;
        }

        if (sourceDescription.StartsWith("VPK:"))
        {
            LogSystem($"MISSING TEXTURE FOUND: {asset.FullPath} (auto-extracted from {sourceDescription.Substring(5)})");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        if (isOriginal)
        {
            File.WriteAllBytes(outputPath, data);
            Log($"  OK: {asset.FullPath} → {Path.GetFileName(outputPath)} (Original VTF)");
        }
        else
        {
            if (TextureConverter.VtfToFile(data, outputPath, _options.TextureOutputFormat))
                Log($"  OK: {asset.FullPath} → {Path.GetFileName(outputPath)}");
            else
                Log($"  FAIL: {asset.FullPath} (decode error)");
        }
    }

    private void ExtractMaterial(AssetInfo asset, CancellationToken ct)
    {
        string outputPath = GetOutputPath(asset.FullPath);

        if (!_extractedFiles.TryAdd(outputPath.ToLowerInvariant(), true)) return;

        byte[]? data = GetAssetData(asset.FullPath);
        if (data == null) return;

        if (!(_options.SkipExistingFiles && File.Exists(outputPath)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, data);
            Log($"  OK: {asset.FullPath} → {Path.GetFileName(outputPath)}");
        }
        else
        {
            Log($"  SKIP: {asset.FullPath} (already exists on disk)");
        }

        // Parse and extract all referenced textures
        try
        {
            string vmtContent = System.Text.Encoding.UTF8.GetString(data);
            var info = VmtReader.Parse(vmtContent);

            var texturesToExtract = info.GetAllReferencedTextures().ToList();

            // Handle Patch/Include materials recursively
            if (!string.IsNullOrEmpty(info.Include))
            {
                string baseMatPath = info.Include.Replace('\\', '/');
                if (!baseMatPath.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase))
                    baseMatPath += ".vmt";
                if (!baseMatPath.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
                    baseMatPath = $"materials/{baseMatPath}";

                byte[]? baseData = GetAssetData(baseMatPath);
                if (baseData != null)
                {
                    try
                    {
                        var baseInfo = VmtReader.Parse(baseData);
                        foreach (var t in baseInfo.GetAllReferencedTextures())
                        {
                            if (!texturesToExtract.Contains(t, StringComparer.OrdinalIgnoreCase))
                                texturesToExtract.Add(t);
                        }
                    }
                    catch { }
                }
            }

            foreach (var rawTexName in texturesToExtract)
            {
                string texPath = rawTexName.Replace('\\', '/').TrimStart('/');
                if (!texPath.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase))
                    texPath += ".vtf";

                if (!texPath.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
                    texPath = $"materials/{texPath}";

                var texAsset = new AssetInfo
                {
                    FullPath = texPath,
                    Type = AssetType.Texture
                };

                ExtractTexture(texAsset);
            }
        }
        catch (Exception ex)
        {
            Log($"  WARN: Failed to parse VMT for {asset.FileName}: {ex.Message}");
        }
    }

    private void ExtractSound(AssetInfo asset)
    {
        string ext = AudioConverter.GetOutputExtension(asset.FullPath);
        string outputPath = GetOutputPath(asset.FullPath, ext);

        if (!_extractedFiles.TryAdd(outputPath.ToLowerInvariant(), true)) return;

        if (_options.SkipExistingFiles && File.Exists(outputPath))
        {
            Log($"  SKIP: {asset.FullPath} (already exists on disk)");
            return;
        }

        byte[]? data = GetAssetData(asset.FullPath);
        if (data == null) { Log($"  SKIP: {asset.FullPath} (not found)"); return; }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (AudioConverter.ExtractAudio(data, asset.FullPath, outputPath))
            Log($"  OK: {asset.FullPath} → {Path.GetFileName(outputPath)}");
    }

    private void ExtractModel(AssetInfo asset, CancellationToken ct)
    {
        string basePath = asset.FullPath.Replace('\\', '/');
        if (!basePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) return;

        string modelDir = GetOutputPath(Path.GetDirectoryName(basePath) ?? "models");
        string modelName = Path.GetFileNameWithoutExtension(basePath);

        bool isOriginal = _options.ExtractOriginalAssets || _options.ModelExportFormat == ModelFormat.Original;

        string modelExt = isOriginal ? ".mdl" : _options.ModelExportFormat switch
        {
            ModelFormat.Fbx => ".mdl",
            ModelFormat.Gltf => ".gltf",
            ModelFormat.Glb => ".glb",
            _ => ".obj"
        };
        string outputPath = Path.Combine(modelDir, $"{modelName}{modelExt}");

        if (!_extractedFiles.TryAdd(outputPath.ToLowerInvariant(), true)) return;

        if (_options.SkipExistingFiles && File.Exists(outputPath))
        {
            Log($"  SKIP: {asset.FullPath} (already exists on disk)");
            return;
        }

        byte[]? mdlData = GetAssetData(basePath);
        if (mdlData == null)
        {
            Log($"  FAIL: {asset.FullPath} (MDL not found)");
            return;
        }

        // Companion files resolution (.vvd, .vtx variants, .phy, .ani)
        int mdlVersion = -1;
        int mdlChecksum = 0;
        if (mdlData.Length >= 12)
        {
            int magic = BitConverter.ToInt32(mdlData, 0);
            if (magic == 0x54534449) // "IDST"
            {
                mdlVersion = BitConverter.ToInt32(mdlData, 4);
                mdlChecksum = BitConverter.ToInt32(mdlData, 8);
            }
        }

        string vvdPath = Path.ChangeExtension(basePath, ".vvd");
        byte[]? vvdData = GetAssetData(vvdPath);

        byte[]? vtxData = null;
        string[] vtxExtensions = { ".dx11.vtx", ".dx90.vtx", ".dx80.vtx", ".sw.vtx", ".vtx" };
        byte[]? fallbackVtx = null;

        foreach (var ext in vtxExtensions)
        {
            string candidatePath = Path.ChangeExtension(basePath, ext);
            byte[]? candidate = GetAssetData(candidatePath);
            if (candidate != null)
            {
                if (fallbackVtx == null) fallbackVtx = candidate;
                if (mdlChecksum != 0 && candidate.Length >= 20)
                {
                    int vtxChecksum = BitConverter.ToInt32(candidate, 16);
                    if (vtxChecksum == mdlChecksum)
                    {
                        vtxData = candidate;
                        break;
                    }
                }
                else
                {
                    vtxData = candidate;
                    break;
                }
            }
        }
        if (vtxData == null && fallbackVtx != null)
        {
            vtxData = fallbackVtx;
        }

        // Additional companions: .phy (physics) and .ani (extra animations)
        byte[]? phyData = GetAssetData(Path.ChangeExtension(basePath, ".phy"));
        byte[]? aniData = GetAssetData(Path.ChangeExtension(basePath, ".ani"));

        // ── 1. ORIGINAL VALVE FORMAT EXTRACTION ──────────────────────────────
        if (isOriginal)
        {
            Directory.CreateDirectory(modelDir);
            File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.mdl"), mdlData);

            if (vvdData != null) File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.vvd"), vvdData);
            if (vtxData != null) File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.vtx"), vtxData);
            if (phyData != null) File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.phy"), phyData);
            if (aniData != null) File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.ani"), aniData);

            string comps = (vvdData != null ? " .vvd" : "") +
                           (vtxData != null ? " .vtx" : "") +
                           (phyData != null ? " .phy" : "") +
                           (aniData != null ? " .ani" : "");

            Log($"  OK: {asset.FullPath} → {modelName}.mdl{comps} (Original Valve Model)");

            // Extract model materials
            var (matNames, matPaths) = MdlReader.GetMaterialReferences(mdlData);
            ExtractModelMaterials(matNames, matPaths, ct);
            return;
        }

        // ── 2. CONVERTED FORMAT EXTRACTION ──────────────────────────────────
        var reader = new MdlReader();
        var model = (vvdData != null && vtxData != null)
            ? reader.Load(mdlData, vvdData, vtxData)
            : new MdlModelData();

        if (_options.ModelExportFormat == ModelFormat.Fbx)
        {
            Directory.CreateDirectory(modelDir);
            File.WriteAllBytes(outputPath, mdlData);
            if (vvdData != null) File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.vvd"), vvdData);
            if (vtxData != null) File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.vtx"), vtxData);
            if (phyData != null) File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.phy"), phyData);
            Log($"  RAW (FBX Placeholder): {asset.FullPath} → copied .mdl + companions");
        }
        else if (_options.ModelExportFormat == ModelFormat.Gltf)
        {
            if (!model.IsValid)
            {
                Directory.CreateDirectory(modelDir);
                File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.mdl"), mdlData);
                Log($"  RAW: {asset.FullPath} (missing valid VVD/VTX for conversion)");
            }
            else if (GltfConverter.ExportGltf(model, modelDir, modelName))
            {
                Log($"  OK: {asset.FullPath} → {modelName}.gltf");
                if (_options.KeepMdlFile)
                {
                    File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.mdl"), mdlData);
                    if (vvdData != null) File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.vvd"), vvdData);
                    if (vtxData != null) File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.vtx"), vtxData);
                    if (phyData != null) File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.phy"), phyData);
                }
            }
            else
            {
                Log($"  FAIL: {asset.FullPath} (glTF export error)");
            }
        }
        else if (_options.ModelExportFormat == ModelFormat.Glb)
        {
            if (!model.IsValid)
            {
                Directory.CreateDirectory(modelDir);
                File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.mdl"), mdlData);
                Log($"  RAW: {asset.FullPath} (missing valid VVD/VTX for conversion)");
            }
            else
            {
                string glbOutput = Path.Combine(modelDir, $"{modelName}.glb");
                if (GltfConverter.ExportGlb(model, glbOutput))
                {
                    Log($"  OK: {asset.FullPath} → {modelName}.glb");
                    if (_options.KeepMdlFile)
                    {
                        File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.mdl"), mdlData);
                        if (vvdData != null) File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.vvd"), vvdData);
                        if (vtxData != null) File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.vtx"), vtxData);
                        if (phyData != null) File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.phy"), phyData);
                    }
                }
                else
                {
                    Log($"  FAIL: {asset.FullPath} (GLB export error)");
                }
            }
        }
        else // ModelFormat.Obj default
        {
            if (!model.IsValid)
            {
                Directory.CreateDirectory(modelDir);
                File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.mdl"), mdlData);
                Log($"  RAW: {asset.FullPath} (missing valid VVD/VTX for conversion)");
            }
            else if (ModelConverter.ExportObj(model, modelDir, modelName))
            {
                Log($"  OK: {asset.FullPath} → {modelName}.obj");
                if (_options.KeepMdlFile)
                {
                    File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.mdl"), mdlData);
                    if (vvdData != null) File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.vvd"), vvdData);
                    if (vtxData != null) File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.vtx"), vtxData);
                    if (phyData != null) File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.phy"), phyData);
                }
            }
            else
            {
                Log($"  FAIL: {asset.FullPath} (OBJ export error)");
            }
        }

        // Extract Model Materials even if model.IsValid is false
        var matRefNames = model.IsValid ? model.MaterialNames : null;
        var matRefPaths = model.IsValid ? model.MaterialPaths : null;

        if (matRefNames == null || matRefNames.Count == 0)
        {
            var refs = MdlReader.GetMaterialReferences(mdlData);
            matRefNames = refs.Names;
            matRefPaths = refs.Paths;
        }

        ExtractModelMaterials(matRefNames, matRefPaths, ct);
    }

    private void ExtractModelMaterials(List<string>? matNames, List<string>? matPaths, CancellationToken ct)
    {
        if (matNames == null || matNames.Count == 0) return;
        matPaths ??= new List<string>();

        foreach (string matName in matNames)
        {
            bool found = false;
            foreach (string matPath in matPaths)
            {
                string fullMatPath = Path.Combine(matPath, matName).Replace('\\', '/');
                if (!fullMatPath.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase))
                    fullMatPath += ".vmt";
                if (!fullMatPath.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
                    fullMatPath = $"materials/{fullMatPath}";

                if (GetAssetData(fullMatPath) != null)
                {
                    ExtractMaterial(new AssetInfo { FullPath = fullMatPath, Type = AssetType.Material }, ct);
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                string fallbackPath = $"materials/{matName}.vmt";
                if (GetAssetData(fallbackPath) != null)
                {
                    ExtractMaterial(new AssetInfo { FullPath = fallbackPath, Type = AssetType.Material }, ct);
                }
            }
        }
    }

    private void ExtractSkybox(AssetInfo asset)
    {
        string skyboxName;

        if (asset.Type == AssetType.Skybox && asset.Source == AssetSource.BspPakfile && !asset.FullPath.Contains("skybox"))
        {
            skyboxName = _bsp?.GetSkyboxName() ?? "";
        }
        else
        {
            string fileName = Path.GetFileNameWithoutExtension(asset.FullPath);
            skyboxName = fileName;

            foreach (var suffix in SkyboxConverter.FaceSuffixes)
            {
                if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    skyboxName = fileName.Substring(0, fileName.Length - suffix.Length);
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(skyboxName)) return;

        string skyDir = Path.Combine(_options.OutputDirectory, "materials", "skybox");
        bool isOriginal = _options.ExtractOriginalAssets || _options.TextureOutputFormat == TextureFormat.Original;

        if (!_extractedFiles.TryAdd((skyboxName + "_skybox").ToLowerInvariant(), true)) return;

        var faceData = new Dictionary<string, byte[]>();
        foreach (string suffix in SkyboxConverter.FaceSuffixes)
        {
            string facePath = $"materials/skybox/{skyboxName}{suffix}.vtf";
            byte[]? data = GetAssetData(facePath);
            if (data != null) faceData[suffix] = data;

            // Also check for corresponding .vmt
            string vmtPath = $"materials/skybox/{skyboxName}{suffix}.vmt";
            byte[]? vmtData = GetAssetData(vmtPath);
            if (vmtData != null && isOriginal)
            {
                Directory.CreateDirectory(skyDir);
                File.WriteAllBytes(Path.Combine(skyDir, $"{skyboxName}{suffix}.vmt"), vmtData);
            }
        }

        if (faceData.Count == 0) return;

        if (isOriginal)
        {
            Directory.CreateDirectory(skyDir);
            foreach (var kvp in faceData)
            {
                File.WriteAllBytes(Path.Combine(skyDir, $"{skyboxName}{kvp.Key}.vtf"), kvp.Value);
            }
            Log($"  OK: Skybox '{skyboxName}' — original VTF faces extracted ({faceData.Count} faces)");
            return;
        }

        int converted = SkyboxConverter.ConvertFaces(skyboxName, faceData, skyDir, _options.TextureOutputFormat);
        Log($"  OK: Skybox '{skyboxName}' — {converted}/{SkyboxConverter.FaceSuffixes.Length} faces");

        if (_options.AssembleSkyboxCubemap && faceData.Count >= 4)
        {
            string cubemapPath = Path.Combine(skyDir, $"{skyboxName}_cubemap.png");
            if (SkyboxConverter.AssembleCubemap(skyboxName, faceData, cubemapPath))
                Log($"  OK: Cubemap assembled → {skyboxName}_cubemap.png");
        }
    }

    private void ExtractRaw(AssetInfo asset)
    {
        string outputPath = GetOutputPath(asset.FullPath);
        if (!_extractedFiles.TryAdd(outputPath.ToLowerInvariant(), true)) return;

        if (_options.SkipExistingFiles && File.Exists(outputPath))
        {
            Log($"  SKIP: {asset.FullPath} (already exists on disk)");
            return;
        }

        byte[]? data = GetAssetData(asset.FullPath);
        if (data == null)
        {
            Log($"  SKIP: {asset.FullPath} (not found)");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, data);
        Log($"  OK: {asset.FullPath} → {Path.GetFileName(outputPath)}");
    }

    /// <summary>
    /// Resolve asset data from pakfile, GMA, disk (BSP folder), custom directories, or VPKs.
    /// </summary>
    public byte[]? GetAssetData(string path)
    {
        return GetAssetData(path, out _);
    }

    /// <summary>
    /// Resolve asset data and identify its source description.
    /// </summary>
    public byte[]? GetAssetData(string path, out string sourceDescription)
    {
        sourceDescription = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return null;

        string normPath = path.Replace('\\', '/').Trim().TrimStart('/');

        // Build list of potential relative paths to check
        var candidates = new List<string> { normPath };

        // Alternative prefixes
        if (normPath.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase) || normPath.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase))
        {
            if (!normPath.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
                candidates.Add($"materials/{normPath}");
            else
                candidates.Add(normPath.Substring(10));
        }
        else if (normPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) ||
                 normPath.EndsWith(".vvd", StringComparison.OrdinalIgnoreCase) ||
                 normPath.Contains(".vtx", StringComparison.OrdinalIgnoreCase) ||
                 normPath.EndsWith(".phy", StringComparison.OrdinalIgnoreCase) ||
                 normPath.EndsWith(".ani", StringComparison.OrdinalIgnoreCase))
        {
            if (!normPath.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                candidates.Add($"models/{normPath}");
            else
                candidates.Add(normPath.Substring(7));
        }
        else if (normPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                 normPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                 normPath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            if (!normPath.StartsWith("sound/", StringComparison.OrdinalIgnoreCase))
                candidates.Add($"sound/{normPath}");
            else
                candidates.Add(normPath.Substring(6));
        }
        else if (normPath.EndsWith(".nav", StringComparison.OrdinalIgnoreCase) ||
                 normPath.EndsWith(".ain", StringComparison.OrdinalIgnoreCase) ||
                 normPath.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase))
        {
            if (!normPath.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
                candidates.Add($"maps/{normPath}");
            else
                candidates.Add(normPath.Substring(5));
        }

        foreach (var cand in candidates)
        {
            // 1. Check BSP pakfile
            if (_pakFiles.TryGetValue(cand, out byte[]? pakData))
            {
                sourceDescription = "BSP Pakfile";
                return pakData;
            }

            // 2. Check in-memory GMA files
            if (_gmaFiles.TryGetValue(cand, out byte[]? gmaData))
            {
                sourceDescription = "GMA Archive";
                return gmaData;
            }

            // 3. Check mounted streaming GMAs (O(1) lookup per GMA)
            foreach (var gma in _gmas)
            {
                var entry = gma.FindEntry(cand);
                if (entry != null)
                {
                    byte[]? data = gma.ExtractEntry(entry);
                    if (data != null)
                    {
                        sourceDescription = $"GMA: {gma.AddonName}";
                        return data;
                    }
                }
            }

            // 4. Check BSP folder on disk (crucial for .nav, .ain, loose map textures)
            if (!string.IsNullOrEmpty(_bspPath) && File.Exists(_bspPath))
            {
                try
                {
                    string bspDir = Path.GetDirectoryName(_bspPath)!;
                    string diskPath = cand.Replace('/', '\\');

                    // Check bspDir + cand
                    string directPath = Path.Combine(bspDir, diskPath);
                    if (File.Exists(directPath))
                    {
                        sourceDescription = $"Map Folder ({bspDir})";
                        return File.ReadAllBytes(directPath);
                    }

                    // Check filename only in bspDir
                    string fileName = Path.GetFileName(cand);
                    string localFile = Path.Combine(bspDir, fileName);
                    if (File.Exists(localFile))
                    {
                        sourceDescription = $"Map Folder ({bspDir})";
                        return File.ReadAllBytes(localFile);
                    }

                    // Check parent dir if bspDir is "maps"
                    string parentDir = Directory.GetParent(bspDir)?.FullName ?? "";
                    if (!string.IsNullOrEmpty(parentDir))
                    {
                        string parentCombined = Path.Combine(parentDir, diskPath);
                        if (File.Exists(parentCombined))
                        {
                            sourceDescription = $"Map Parent Folder ({parentDir})";
                            return File.ReadAllBytes(parentCombined);
                        }
                    }
                }
                catch { }
            }

            // 5. Check explicitly loaded custom directories
            foreach (var dir in _customDirectories)
            {
                try
                {
                    string diskPath = cand.Replace('/', '\\');
                    string fullPath = Path.Combine(dir, diskPath);
                    if (File.Exists(fullPath))
                    {
                        sourceDescription = $"Custom Folder ({dir})";
                        return File.ReadAllBytes(fullPath);
                    }

                    // Also check filename directly in custom dir
                    string fileName = Path.GetFileName(cand);
                    string localFile = Path.Combine(dir, fileName);
                    if (File.Exists(localFile))
                    {
                        sourceDescription = $"Custom Folder ({dir})";
                        return File.ReadAllBytes(localFile);
                    }
                }
                catch { }
            }

            // 6. Check mounted VPKs
            bool isCompanionFile = cand.EndsWith(".vvd", StringComparison.OrdinalIgnoreCase) ||
                                   cand.EndsWith(".vtx", StringComparison.OrdinalIgnoreCase) ||
                                   cand.EndsWith(".phy", StringComparison.OrdinalIgnoreCase) ||
                                   cand.EndsWith(".ani", StringComparison.OrdinalIgnoreCase);

            if (_options.ExtractGameDependencies || isCompanionFile)
            {
                foreach (var vpk in _vpks)
                {
                    var entry = vpk.FindEntry(cand);
                    if (entry != null)
                    {
                        sourceDescription = $"VPK: {Path.GetFileName(vpk.FilePath)}";
                        return vpk.ExtractEntry(entry);
                    }
                }
            }
        }

        // Fallback: search custom directory tree by filename
        string targetFileName = Path.GetFileName(normPath);
        if (!string.IsNullOrEmpty(targetFileName))
        {
            foreach (var dir in _customDirectories)
            {
                try
                {
                    var found = Directory.GetFiles(dir, targetFileName, SearchOption.AllDirectories);
                    if (found.Length > 0)
                    {
                        sourceDescription = $"Custom Folder ({dir}) [filename match]";
                        return File.ReadAllBytes(found[0]);
                    }
                }
                catch { }
            }
        }

        string checkedSources = $"pakfile({_pakFiles.Count}), GMAs({_gmas.Count}+{_gmaFiles.Count}), custom_dirs({_customDirectories.Count}), VPKs({_vpks.Count})";
        LogSystem($"  Could not find asset: {normPath} (Checked: {checkedSources})");
        return null;
    }

    private string GetOutputPath(string sourcePath, string? newExtension = null)
    {
        string relativePath = sourcePath.Replace('\\', '/').TrimStart('/');

        if (!_options.PreserveDirectoryStructure)
            relativePath = Path.GetFileName(relativePath);

        if (newExtension != null)
            relativePath = Path.ChangeExtension(relativePath, newExtension);

        return Path.Combine(_options.OutputDirectory, relativePath);
    }

    private void ReportProgress(int current, int total, string file, string message)
    {
        ProgressChanged?.Invoke(this, new ExtractionProgressEventArgs
        {
            Current = current,
            Total = total,
            CurrentFile = file,
            Message = message
        });
    }

    private void Log(string message) => LogMessage?.Invoke(this, message);
    private void LogSystem(string message) => SystemMessage?.Invoke(this, message);

    public void Dispose()
    {
        _bsp?.Dispose();
        _bsp = null;
        ClearVpks();
        foreach (var gma in _gmas) gma.Dispose();
        _gmas.Clear();
    }
}
