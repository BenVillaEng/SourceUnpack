using SourceUnpack.Core.Conversion;
using SourceUnpack.Core.Formats.Bsp;
using SourceUnpack.Core.Formats.Gma;
using SourceUnpack.Core.Formats.Mdl;
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
/// Coordinates BSP/VPK reading, asset discovery, and conversion to output formats.
/// </summary>
public class ExtractionPipeline
{
    public event EventHandler<ExtractionProgressEventArgs>? ProgressChanged;
    public event EventHandler<string>? LogMessage;
    public event EventHandler<string>? SystemMessage;

    private readonly ConversionOptions _options;
    private BspReader? _bsp;
    private List<VpkReader> _vpks = new();
    private Dictionary<string, byte[]> _pakFiles = new();
    private GmaReader? _gma;
    private Dictionary<string, byte[]> _gmaFiles = new();
    private List<string> _customDirectories = new();
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

        var assets = AssetDiscovery.DiscoverFromBsp(_bsp);
        var stats = AssetDiscovery.GetStats(assets);
        LogSystem($"Discovered: {stats.textures} textures, {stats.models} models, {stats.sounds} sounds, {stats.skyboxes} skybox faces");

        return assets;
    }

    /// <summary>
    /// Load a VPK game directory or specific file.
    /// </summary>
    public void LoadVpk(string vpkPath)
    {
        try 
        {
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
        _vpks.Clear();
        LogSystem("Unmounted all VPKs.");
    }

    /// <summary>
    /// Load a GMA (Garry's Mod Addon) archive and store its files.
    /// </summary>
    public void LoadGma(string gmaPath)
    {
        LogSystem($"Loading GMA: {Path.GetFileName(gmaPath)}");
        _gma = new GmaReader(gmaPath);

        if (!_gma.IsValid)
        {
            LogSystem("ERROR: Invalid GMA file.");
            return;
        }

        LogSystem($"GMA Addon: {_gma.AddonName} | Files: {_gma.Entries.Count}");
        var files = _gma.ExtractAll();
        foreach (var file in files)
        {
            _gmaFiles[file.Key] = file.Value;
        }
        LogSystem($"Extracted {_gmaFiles.Count} total files from GMAs.");
    }

    /// <summary>
    /// Load a loose directory to use for resolving custom assets (e.g. from a decompiled addon).
    /// </summary>
    public void LoadDirectory(string directoryPath)
    {
        if (Directory.Exists(directoryPath))
        {
            if (!_customDirectories.Contains(directoryPath, StringComparer.OrdinalIgnoreCase))
            {
                _customDirectories.Add(directoryPath);
                LogSystem($"Mounted Custom Folder: {directoryPath}");
            }
        }
        else
        {
            LogSystem($"  ERROR mounting Custom Folder: Directory not found - {directoryPath}");
        }
    }

    /// <summary>
    /// Extract and convert selected assets.
    /// </summary>
    public async Task ExtractAsync(List<AssetInfo> assets, CancellationToken cancellationToken = default)
    {
        int total = assets.Count;
        int current = 0;
        var progressLock = new object();
        _extractedFiles.Clear();

        Log($"Starting extraction of {total} assets to: {_options.OutputDirectory}");
        Log($"Using {Environment.ProcessorCount} parallel workers for maximum CPU utilization.");
        Directory.CreateDirectory(_options.OutputDirectory);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(assets, parallelOptions, async (asset, ct) =>
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
                        await ExtractMaterial(asset, ct);
                        break;
                    case AssetType.Sound:
                        ExtractSound(asset);
                        break;
                    case AssetType.Model:
                        await ExtractModel(asset, ct);
                        break;
                    case AssetType.Skybox:
                        ExtractSkybox(asset);
                        break;
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
        });

        Log($"Extraction complete. {current}/{total} assets processed.");
    }


    private void ExtractTexture(AssetInfo asset)
    {
        string texExt = TextureConverter.GetExtension(_options.TextureOutputFormat);
        string outputPath = GetOutputPath(asset.FullPath, texExt);

        if (!_extractedFiles.TryAdd(outputPath.ToLowerInvariant(), true)) return;

        if (_options.SkipExistingFiles && File.Exists(outputPath)) {
            Log($"  SKIP: {asset.FullPath} (already exists on disk)");
            return;
        }

        byte[]? data = GetAssetData(asset.FullPath, out string sourceDescription);
        if (data == null) 
        { 
            if (_options.GenerateMissingPlaceholders)
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
        if (TextureConverter.VtfToFile(data, outputPath, _options.TextureOutputFormat))
            Log($"  OK: {asset.FullPath} → {Path.GetFileName(outputPath)}");
        else
            Log($"  FAIL: {asset.FullPath} (decode error)");
    }

    private async Task ExtractMaterial(AssetInfo asset, CancellationToken ct)
    {
        string outputPath = GetOutputPath(asset.FullPath);

        if (!_extractedFiles.TryAdd(outputPath.ToLowerInvariant(), true)) return;

        byte[]? data = GetAssetData(asset.FullPath);
        if (data == null) return;

        if (!(_options.SkipExistingFiles && File.Exists(outputPath))) {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, data);
        } else {
            Log($"  SKIP: {asset.FullPath} (already exists on disk)");
        }

        // Recursive: Parse and extract referenced textures
        try
        {
            string vmtContent = System.Text.Encoding.UTF8.GetString(data);
            var info = SourceUnpack.Core.Formats.Vmt.VmtReader.Parse(vmtContent);

            var texturesToExtract = new List<string?>
            {
                info.BaseTexture,
                info.BumpMap,
                info.NormalMap,
                info.EnvMapMask,
                info.DetailTexture,
                info.PhongExponentTexture,
                info.SelfIllumMask
            };

            // Handle Patch/Include materials
            if (!string.IsNullOrEmpty(info.Include))
            {
                 // Recursively extract the included base material
                 string baseMatPath = info.Include;
                 if (!baseMatPath.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase))
                     baseMatPath += ".vmt";
                 
                 // Normalize path (Source engine style)
                 baseMatPath = baseMatPath.Replace('\\', '/');
                 if (!baseMatPath.StartsWith("materials/", StringComparison.OrdinalIgnoreCase) && 
                      !baseMatPath.Contains("/")) 
                     baseMatPath = $"materials/{baseMatPath}";
                 else if (!baseMatPath.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
                     baseMatPath = $"materials/{baseMatPath}"; // Force materials prefix if not absolute

                 // Recursively call ExtractMaterial on the base material
                 // But wait, ExtractMaterial usually takes an AssetInfo.
                 // We need to resolve the base material's data first.
                 
                 // Logic: If included path exists, parse IT and add its textures to our list.
                 byte[]? baseData = GetAssetData(baseMatPath);
                 if (baseData != null)
                 {
                     try
                     {
                         var baseInfo = SourceUnpack.Core.Formats.Vmt.VmtReader.Parse(baseData);
                         // Merge textures from base material
                         texturesToExtract.Add(baseInfo.BaseTexture);
                         texturesToExtract.Add(baseInfo.BumpMap);
                         texturesToExtract.Add(baseInfo.NormalMap);
                         texturesToExtract.Add(baseInfo.EnvMapMask);
                         texturesToExtract.Add(baseInfo.DetailTexture);
                         texturesToExtract.Add(baseInfo.PhongExponentTexture);
                         texturesToExtract.Add(baseInfo.SelfIllumMask);
                         
                         // Also extract the base material VMT itself (optional)
                         // await ExtractMaterial(new AssetInfo { FullPath = baseMatPath, Type = AssetType.Material }, ct);
                     }
                     catch (Exception ex)
                     {
                         Log($"  WARN: Failed to parse base material {baseMatPath}: {ex.Message}");
                     }
                 }
                 else
                 {
                     Log($"  WARN: Missing base material {baseMatPath} for patch {asset.FileName}");
                 }
            }

            foreach (var texName in texturesToExtract.Where(t => !string.IsNullOrEmpty(t)))
            {
                string texPath = texName!.Replace('\\', '/');
                if (!texPath.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase))
                    texPath += ".vtf";
                
                // Ensure path starts with "materials/" if not present (Source engine quirk)
                // Actually VMT paths are relative to materials/ usually, or absolute from root.
                // Standard: "brick/brickwall01" -> "materials/brick/brickwall01.vtf"
                if (!texPath.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
                    texPath = $"materials/{texPath}";

                var texAsset = new AssetInfo
                {
                    FullPath = texPath,
                    Type = AssetType.Texture
                };

                await Task.Run(() => ExtractTexture(texAsset), ct);
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

        if (_options.SkipExistingFiles && File.Exists(outputPath)) {
            Log($"  SKIP: {asset.FullPath} (already exists on disk)");
            return;
        }

        byte[]? data = GetAssetData(asset.FullPath);
        if (data == null) { Log($"  SKIP: {asset.FullPath} (not found)"); return; }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (AudioConverter.ExtractAudio(data, asset.FullPath, outputPath))
            Log($"  OK: {asset.FullPath} → {Path.GetFileName(outputPath)}");
    }

    private async Task ExtractModel(AssetInfo asset, CancellationToken ct)
    {
        string basePath = asset.FullPath;
        if (!basePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) return;

        string modelDir = GetOutputPath(Path.GetDirectoryName(basePath) ?? "models");
        string modelName = Path.GetFileNameWithoutExtension(basePath);
        string modelExt = _options.ModelExportFormat switch
        {
            ModelFormat.Fbx => ".mdl",
            ModelFormat.Gltf => ".gltf",
            ModelFormat.Glb => ".glb",
            _ => ".obj"
        };
        string outputPath = Path.Combine(modelDir, $"{modelName}{modelExt}");

        if (!_extractedFiles.TryAdd(outputPath.ToLowerInvariant(), true)) return;

        if (_options.SkipExistingFiles && File.Exists(outputPath)) {
            Log($"  SKIP: {asset.FullPath} (already exists on disk)");
            return;
        }

        string vvdPath = Path.ChangeExtension(basePath, ".vvd");

        byte[]? mdlData = GetAssetData(basePath);

        // Diagnostic: detect MDL version and checksum for companion validation
        int mdlVersion = -1;
        int mdlChecksum = 0;
        if (mdlData != null && mdlData.Length >= 12)
        {
            int magic = BitConverter.ToInt32(mdlData, 0);
            if (magic == 0x54534449) // "IDST"
            {
                mdlVersion = BitConverter.ToInt32(mdlData, 4);
                mdlChecksum = BitConverter.ToInt32(mdlData, 8);
            }
        }

        // VVD resolution with checksum validation
        byte[]? vvdData = GetAssetData(vvdPath);
        if (vvdData != null && mdlChecksum != 0 && vvdData.Length >= 12)
        {
            int vvdChecksum = BitConverter.ToInt32(vvdData, 8);
            if (vvdChecksum != mdlChecksum)
            {
                Log($"  WARN: VVD checksum mismatch ({vvdChecksum:X8} vs MDL {mdlChecksum:X8}), skipping");
                vvdData = null;
            }
        }

        // VTX resolution — Crowbar order: .dx11.vtx -> .dx90.vtx -> .dx80.vtx -> .sw.vtx -> .vtx
        // Each candidate is validated against the MDL checksum
        byte[]? vtxData = null;
        string[] vtxExtensions = { ".dx11.vtx", ".dx90.vtx", ".dx80.vtx", ".sw.vtx", ".vtx" };
        foreach (var ext in vtxExtensions)
        {
            string vtxPath = Path.ChangeExtension(basePath, ext);
            byte[]? candidate = GetAssetData(vtxPath);
            if (candidate != null)
            {
                if (mdlChecksum != 0 && candidate.Length >= 20)
                {
                    int vtxChecksum = BitConverter.ToInt32(candidate, 16);
                    if (vtxChecksum != mdlChecksum)
                    {
                        Log($"  INFO: {ext} checksum mismatch ({vtxChecksum:X8} vs MDL {mdlChecksum:X8}), trying next");
                        continue;
                    }
                }
                vtxData = candidate;
                break;
            }
        }

        // Log diagnostic info about companion file resolution
        if (vvdData == null || vtxData == null)
        {
            string missing = "";
            if (vvdData == null) missing += ".vvd ";
            if (vtxData == null) missing += ".vtx ";
            Log($"  WARN: {asset.FullPath} — Missing companion files: {missing.Trim()}");
            if (mdlVersion > 0)
            {
                Log($"  INFO: MDL format version {mdlVersion}" + 
                    (mdlVersion > 53 ? " (Source 2 / unknown format — NOT supported)" :
                     mdlVersion >= 44 ? " (supported v44-v53)" : " (too old — unsupported)"));
            }
            Log($"  TIP: Set Game Directory to your game folder, or add custom assets path containing VVD/VTX files");
        }

        var reader = new MdlReader();
        var model = (mdlData != null && vvdData != null && vtxData != null) 
            ? reader.Load(mdlData, vvdData, vtxData) 
            : new MdlModelData(); // empty/invalid model

        // If Load returned invalid but we had all files, log version info
        if (!model.IsValid && mdlData != null && vvdData != null && vtxData != null && mdlVersion > 0)
        {
            Log($"  WARN: MDL v{mdlVersion} loaded all 3 files but parsing failed — possible file corruption or unsupported sub-format");
        }

        if (_options.ModelExportFormat == ModelFormat.Fbx)
        {
            if (mdlData != null)
            {
                Directory.CreateDirectory(modelDir);
                File.WriteAllBytes(outputPath, mdlData);
                if (vvdData != null) File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.vvd"), vvdData);
                if (vtxData != null) File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.vtx"), vtxData);
                
                string ext = vtxData == null ? "vtx(missing)" : ".vtx";
                Log($"  RAW (FBX Placeholder): {asset.FullPath} → copied .mdl, .vvd, {ext}");
            }
            else
            {
                Log($"  FAIL: {asset.FullPath} (MDL not found)");
                return;
            }
        }
        else if (_options.ModelExportFormat == ModelFormat.Gltf)
        {
            if (!model.IsValid)
            {
                if (mdlData != null)
                {
                    string rawOutput = GetOutputPath(basePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(rawOutput)!);
                    File.WriteAllBytes(rawOutput, mdlData);
                    Log($"  RAW: {asset.FullPath} (missing VVD/VTX for conversion)");
                }
                return;
            }

            if (GltfConverter.ExportGltf(model, modelDir, modelName))
            {
                Log($"  OK: {asset.FullPath} → {modelName}.gltf");
                if (_options.KeepMdlFile && mdlData != null)
                {
                    File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.mdl"), mdlData);
                    if (vvdData != null)
                        File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.vvd"), vvdData);
                    if (vtxData != null)
                        File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.vtx"), vtxData);
                    Log($"  KEEP: Saved original {modelName}.mdl" +
                        (vvdData != null ? " + .vvd" : "") +
                        (vtxData != null ? " + .vtx" : ""));
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
                if (mdlData != null)
                {
                    string rawOutput = GetOutputPath(basePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(rawOutput)!);
                    File.WriteAllBytes(rawOutput, mdlData);
                    Log($"  RAW: {asset.FullPath} (missing VVD/VTX for conversion)");
                }
                return;
            }

            string glbOutput = Path.Combine(modelDir, $"{modelName}.glb");
            if (GltfConverter.ExportGlb(model, glbOutput))
            {
                Log($"  OK: {asset.FullPath} → {modelName}.glb");
                if (_options.KeepMdlFile && mdlData != null)
                {
                    File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.mdl"), mdlData);
                    if (vvdData != null)
                        File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.vvd"), vvdData);
                    if (vtxData != null)
                        File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.vtx"), vtxData);
                    Log($"  KEEP: Saved original {modelName}.mdl" +
                        (vvdData != null ? " + .vvd" : "") +
                        (vtxData != null ? " + .vtx" : ""));
                }
            }
            else
            {
                Log($"  FAIL: {asset.FullPath} (GLB export error)");
            }
        }
        else
        {
            if (!model.IsValid)
            {
                if (mdlData != null)
                {
                    string rawOutput = GetOutputPath(basePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(rawOutput)!);
                    File.WriteAllBytes(rawOutput, mdlData);
                    Log($"  RAW: {asset.FullPath} (missing VVD/VTX for conversion)");
                }
                return;
            }

            if (ModelConverter.ExportObj(model, modelDir, modelName))
            {
                Log($"  OK: {asset.FullPath} → {modelName}.obj");
                if (_options.KeepMdlFile && mdlData != null)
                {
                    File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.mdl"), mdlData);
                    if (vvdData != null)
                        File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.vvd"), vvdData);
                    if (vtxData != null)
                        File.WriteAllBytes(Path.Combine(modelDir, $"{modelName}.vtx"), vtxData);
                    Log($"  KEEP: Saved original {modelName}.mdl" +
                        (vvdData != null ? " + .vvd" : "") +
                        (vtxData != null ? " + .vtx" : ""));
                }
            }
            else
            {
                Log($"  FAIL: {asset.FullPath} (OBJ export error)");
            }
        }

        if (!model.IsValid) return;

        // Extract Model Materials
        foreach (string matName in model.MaterialNames)
        {
            bool found = false;
            foreach (string matPath in model.MaterialPaths)
            {
                string fullMatPath = Path.Combine(matPath, matName).Replace('\\', '/');
                if (!fullMatPath.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase))
                    fullMatPath += ".vmt";
                
                if (!fullMatPath.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
                     fullMatPath = $"materials/{fullMatPath}";

                // Try to resolve
                if (GetAssetData(fullMatPath) != null)
                {
                    await Task.Run(() => ExtractMaterial(new AssetInfo { FullPath = fullMatPath, Type = AssetType.Material }, ct), ct);
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                // Try literal path in materials root
                 string fallbackPath = $"materials/{matName}.vmt";
                 if (GetAssetData(fallbackPath) != null)
                     await Task.Run(() => ExtractMaterial(new AssetInfo { FullPath = fallbackPath, Type = AssetType.Material }, ct), ct);
            }
        }
    }

    private void ExtractSkybox(AssetInfo asset)
    {
        string skyboxName;

        // Determine skybox name from asset or map
        if (asset.Type == AssetType.Skybox && asset.Source == AssetSource.BspPakfile && !asset.FullPath.Contains("skybox"))
        {
             // Fallback for metadata-only skybox entries (if any)
             skyboxName = _bsp?.GetSkyboxName() ?? "";
        }
        else
        {
            // Try to guess from filename (e.g., sky_day01_01up.vtf -> sky_day01_01)
            string fileName = Path.GetFileNameWithoutExtension(asset.FullPath);
            skyboxName = fileName;
            
            // Check suffixes
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

        string skyDir = Path.Combine(_options.OutputDirectory, "skybox");
        string texExt = TextureConverter.GetExtension(_options.TextureOutputFormat);
        string indicatorPath = Path.Combine(skyDir, $"{skyboxName}_ft{texExt}");
        
        if (!_extractedFiles.TryAdd((skyboxName + "_skybox").ToLowerInvariant(), true)) return;

        if (_options.SkipExistingFiles && File.Exists(indicatorPath)) {
            Log($"  SKIP: Skybox {skyboxName} (already exists on disk)");
            return;
        }

        var faceData = new Dictionary<string, byte[]>();
        foreach (string suffix in SkyboxConverter.FaceSuffixes)
        {
            string facePath = $"materials/skybox/{skyboxName}{suffix}.vtf";
            byte[]? data = GetAssetData(facePath);
            if (data != null) faceData[suffix] = data;
        }

        if (faceData.Count == 0) return;

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

        if (_options.SkipExistingFiles && File.Exists(outputPath)) {
            Log($"  SKIP: {asset.FullPath} (already exists on disk)");
            return;
        }

        byte[]? data = GetAssetData(asset.FullPath);
        if (data == null) return;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, data);
    }

    /// <summary>
    /// Resolve asset data from pakfile or VPK.
    /// </summary>
    public byte[]? GetAssetData(string path)
    {
        return GetAssetData(path, out _);
    }

    /// <summary>
    /// Resolve asset data from pakfile or VPK and get its source description.
    /// </summary>
    public byte[]? GetAssetData(string path, out string sourceDescription)
    {
        sourceDescription = string.Empty;
        path = path.Replace('\\', '/');

        // Check BSP pakfile first
        if (_pakFiles.TryGetValue(path, out byte[]? pakData))
        {
            sourceDescription = "BSP Pakfile";
            return pakData;
        }

        // Try case-insensitive match
        var match = _pakFiles.Keys.FirstOrDefault(k =>
            k.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            sourceDescription = "BSP Pakfile";
            return _pakFiles[match];
        }

        // Check GMA files
        if (_gmaFiles.TryGetValue(path, out byte[]? gmaData))
        {
            sourceDescription = "GMA Archive";
            return gmaData;
        }

        // Try case-insensitive GMA match
        var gmaMatch = _gmaFiles.Keys.FirstOrDefault(k =>
            k.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (gmaMatch != null)
        {
            sourceDescription = "GMA Archive";
            return _gmaFiles[gmaMatch];
        }

        // Check custom explicitly loaded directories (loose files)
        foreach (var dir in _customDirectories)
        {
            try
            {
                // Source paths might be 'materials/skybox/sky.vtf', so we combine dir + path
                string fullPath = Path.Combine(dir, path).Replace('\\', '/');
                if (File.Exists(fullPath))
                {
                    sourceDescription = $"Custom Folder ({dir})";
                    return File.ReadAllBytes(fullPath);
                }
                
                // FALLBACK: Search by filename only in the custom directory tree
                // This handles the common case where users drop VVD/VTX files
                // directly into a folder without preserving the models/ subdirectory
                string fileName = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(fileName))
                {
                    try
                    {
                        var found = Directory.GetFiles(dir, fileName, SearchOption.AllDirectories);
                        if (found.Length > 0)
                        {
                            sourceDescription = $"Custom Folder ({dir}) [filename match]";
                            return File.ReadAllBytes(found[0]);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Check VPKs — ALWAYS check for model companion files (.vvd, .vtx)
        // even when ExtractGameDependencies is off, because models are useless without them
        bool isCompanionFile = path.EndsWith(".vvd", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".vtx", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".dx90.vtx", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".dx80.vtx", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".sw.vtx", StringComparison.OrdinalIgnoreCase);

        if (_options.ExtractGameDependencies || isCompanionFile)
        {
            foreach (var vpk in _vpks)
            {
                var entry = vpk.FindEntry(path);
                if (entry != null)
                {
                    sourceDescription = $"VPK: {Path.GetFileName(vpk.FilePath)}";
                    return vpk.ExtractEntry(entry);
                }
            }
        }

        // Log exactly what we checked for debugging
        string checkedSources = $"pakfile({_pakFiles.Count}), GMA({_gmaFiles.Count}), custom_dirs({_customDirectories.Count}), VPKs({_vpks.Count})";
        LogSystem($"  Could not find asset: {path} (Checked: {checkedSources})");
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

    private void Log(string message)
    {
        LogMessage?.Invoke(this, message);
    }

    private void LogSystem(string message)
    {
        SystemMessage?.Invoke(this, message);
    }
}
