using SourceUnpack.Core.Formats.Bsp;
using SourceUnpack.Core.Formats.Gma;
using SourceUnpack.Core.Formats.Vmt;
using SourceUnpack.Core.Formats.Vpk;
using SourceUnpack.Core.Models;

namespace SourceUnpack.Core.Pipeline;

/// <summary>
/// Scans BSP pakfiles, GMA addons, and VPK archives to discover and categorize all assets.
/// Builds a structured asset tree for UI display.
/// </summary>
public class AssetDiscovery
{
    /// <summary>
    /// Discover all assets embedded in a BSP file and its referenced dependencies.
    /// </summary>
    public static List<AssetInfo> DiscoverFromBsp(BspReader bsp, string? bspPath = null)
    {
        var assets = new List<AssetInfo>();
        var pakFiles = bsp.ExtractPakfile();

        foreach (var (path, data) in pakFiles)
        {
            var asset = new AssetInfo
            {
                FullPath = path.Replace('\\', '/'),
                Source = AssetSource.BspPakfile,
                SizeBytes = data.Length,
                Type = ClassifyAsset(path)
            };

            // Parse VMT for texture assets
            if (asset.Type == AssetType.Material)
            {
                try
                {
                    asset.Material = VmtReader.Parse(data);
                }
                catch { /* VMT parsing is best-effort */ }
            }

            assets.Add(asset);
        }

        // Also detect skybox from entity data
        string? skyboxName = bsp.GetSkyboxName();
        if (!string.IsNullOrEmpty(skyboxName))
        {
            AddSkyboxAssets(assets, skyboxName);
        }

        // Add texture references from BSP tex data
        foreach (string texName in bsp.GetTextureNames())
        {
            string texPath = $"materials/{texName.ToLowerInvariant()}.vmt";
            if (!assets.Any(a => a.FullPath.Equals(texPath, StringComparison.OrdinalIgnoreCase)))
            {
                assets.Add(new AssetInfo
                {
                    FullPath = texPath,
                    Type = AssetType.Material,
                    Source = AssetSource.VpkArchive,
                    SizeBytes = 0
                });
            }
        }

        // Add static props
        foreach (string modelName in bsp.GetStaticPropNames())
        {
            string modelPath = modelName.Replace('\\', '/');
            if (!modelPath.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                modelPath = $"models/{modelPath}";

            if (!assets.Any(a => a.FullPath.Equals(modelPath, StringComparison.OrdinalIgnoreCase)))
            {
                assets.Add(new AssetInfo
                {
                    FullPath = modelPath,
                    Type = AssetType.Model,
                    Source = AssetSource.VpkArchive,
                    SizeBytes = 0
                });
            }
        }

        // Parse entities for dynamic/physics props, NPC models, doors, overlays, sprites, detail props, and sounds
        var entities = bsp.ParseEntities();
        var soundKeys = new[]
        {
            "message", "sound", "noise1", "noise2", "noise", "startsound", "stopsound",
            "shoot", "flysound", "playsound", "soundname", "breaksound", "stepsound",
            "unlocked_sound", "locked_sound"
        };
        var matKeys = new[]
        {
            "texture", "material", "detailmaterial", "texturename", "bottommaterial", "overlayname"
        };

        foreach (var entity in entities)
        {
            // Worldspawn detail props
            if (entity.TryGetValue("classname", out var cls) && cls == "worldspawn")
            {
                if (entity.TryGetValue("detailvbsp", out string? detailVbsp) && !string.IsNullOrWhiteSpace(detailVbsp))
                {
                    detailVbsp = detailVbsp.Replace('\\', '/').Trim().Trim('"', '\'');
                    if (!assets.Any(a => a.FullPath.Equals(detailVbsp, StringComparison.OrdinalIgnoreCase)))
                    {
                        assets.Add(new AssetInfo
                        {
                            FullPath = detailVbsp,
                            Type = AssetType.Other,
                            Source = AssetSource.VpkArchive,
                            SizeBytes = 0
                        });
                    }
                }
                if (entity.TryGetValue("detailmaterial", out string? detailMat) && !string.IsNullOrWhiteSpace(detailMat))
                {
                    detailMat = detailMat.Replace('\\', '/').Trim().Trim('"', '\'');
                    if (!detailMat.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
                        detailMat = $"materials/{detailMat}";
                    if (!detailMat.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase))
                        detailMat += ".vmt";

                    if (!assets.Any(a => a.FullPath.Equals(detailMat, StringComparison.OrdinalIgnoreCase)))
                    {
                        assets.Add(new AssetInfo
                        {
                            FullPath = detailMat,
                            Type = AssetType.Material,
                            Source = AssetSource.VpkArchive,
                            SizeBytes = 0
                        });
                    }
                }
            }

            // Entity models (prop_physics, prop_dynamic, prop_door, npc_*, weapon_*, etc.)
            if (entity.TryGetValue("model", out string? modelVal) && !string.IsNullOrWhiteSpace(modelVal))
            {
                modelVal = modelVal.Replace('\\', '/').Trim().Trim('"', '\'');
                if (modelVal.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) || modelVal.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                {
                    if (!modelVal.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                        modelVal = $"models/{modelVal}";
                    if (!modelVal.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                        modelVal += ".mdl";

                    if (!assets.Any(a => a.FullPath.Equals(modelVal, StringComparison.OrdinalIgnoreCase)))
                    {
                        assets.Add(new AssetInfo
                        {
                            FullPath = modelVal,
                            Type = AssetType.Model,
                            Source = AssetSource.VpkArchive,
                            SizeBytes = 0
                        });
                    }
                }
                else if (modelVal.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase) || modelVal.StartsWith("materials/", StringComparison.OrdinalIgnoreCase) || modelVal.EndsWith(".spr", StringComparison.OrdinalIgnoreCase))
                {
                    // Sprites or material model
                    if (!modelVal.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
                        modelVal = $"materials/{modelVal}";
                    if (!modelVal.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase) && !modelVal.EndsWith(".spr", StringComparison.OrdinalIgnoreCase))
                        modelVal += ".vmt";

                    if (!assets.Any(a => a.FullPath.Equals(modelVal, StringComparison.OrdinalIgnoreCase)))
                    {
                        assets.Add(new AssetInfo
                        {
                            FullPath = modelVal,
                            Type = AssetType.Material,
                            Source = AssetSource.VpkArchive,
                            SizeBytes = 0
                        });
                    }
                }
            }

            // Extra model keys like headmodel
            if (entity.TryGetValue("headmodel", out string? headModel) && !string.IsNullOrWhiteSpace(headModel))
            {
                headModel = headModel.Replace('\\', '/').Trim().Trim('"', '\'');
                if (!headModel.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) headModel = $"models/{headModel}";
                if (!headModel.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) headModel += ".mdl";

                if (!assets.Any(a => a.FullPath.Equals(headModel, StringComparison.OrdinalIgnoreCase)))
                {
                    assets.Add(new AssetInfo
                    {
                        FullPath = headModel,
                        Type = AssetType.Model,
                        Source = AssetSource.VpkArchive,
                        SizeBytes = 0
                    });
                }
            }

            // Entity materials (infodecal, info_overlay, env_projectedtexture, etc.)
            foreach (var key in matKeys)
            {
                if (entity.TryGetValue(key, out string? matVal) && !string.IsNullOrWhiteSpace(matVal))
                {
                    matVal = matVal.Replace('\\', '/').Trim().Trim('"', '\'');
                    if (matVal.Contains("/") || matVal.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase) || matVal.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!matVal.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
                            matVal = $"materials/{matVal}";
                        if (!matVal.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase) && !matVal.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase))
                            matVal += ".vmt";

                        if (!assets.Any(a => a.FullPath.Equals(matVal, StringComparison.OrdinalIgnoreCase)))
                        {
                            assets.Add(new AssetInfo
                            {
                                FullPath = matVal,
                                Type = matVal.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase) ? AssetType.Texture : AssetType.Material,
                                Source = AssetSource.VpkArchive,
                                SizeBytes = 0
                            });
                        }
                    }
                }
            }

            // Entity sounds
            foreach (var key in soundKeys)
            {
                if (entity.TryGetValue(key, out string? soundPath) && !string.IsNullOrWhiteSpace(soundPath))
                {
                    soundPath = soundPath.Replace('\\', '/').TrimStart('/', '\\').Trim('"', '\'');
                    soundPath = soundPath.TrimStart('*', '#', '!', '@', ')', '^', '(', '<', '>');

                    var ext = Path.GetExtension(soundPath).ToLowerInvariant();
                    if (ext != ".wav" && ext != ".mp3" && ext != ".ogg") continue;

                    string fullPath = soundPath.StartsWith("sound/", StringComparison.OrdinalIgnoreCase)
                        ? soundPath
                        : $"sound/{soundPath}";

                    if (!assets.Any(a => a.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        assets.Add(new AssetInfo
                        {
                            FullPath = fullPath,
                            Type = AssetType.Sound,
                            Source = AssetSource.VpkArchive,
                            SizeBytes = 0
                        });
                    }
                }
            }
        }

        // Companion files discovery (.nav, .ain, .txt, .kv)
        string? mapFilePath = bspPath ?? bsp.FilePath;
        if (!string.IsNullOrEmpty(mapFilePath))
        {
            DiscoverCompanionFiles(assets, mapFilePath);
        }

        return assets;
    }

    private static void DiscoverCompanionFiles(List<AssetInfo> assets, string mapFilePath)
    {
        try
        {
            string mapDir = Path.GetDirectoryName(mapFilePath) ?? "";
            string mapName = Path.GetFileNameWithoutExtension(mapFilePath);
            if (string.IsNullOrEmpty(mapName) || string.IsNullOrEmpty(mapDir)) return;

            string parentDir = Directory.GetParent(mapDir)?.FullName ?? "";
            bool isInsideMapsFolder = Path.GetFileName(mapDir).Equals("maps", StringComparison.OrdinalIgnoreCase);

            // 1. Navigation Mesh (.nav)
            if (!assets.Any(a => a.Type == AssetType.NavMesh))
            {
                var navCandidates = new List<string>
                {
                    Path.Combine(mapDir, $"{mapName}.nav"),
                    Path.Combine(mapDir, $"{mapName}.bsp.nav"),
                    Path.Combine(mapDir, "maps", $"{mapName}.nav")
                };
                if (isInsideMapsFolder && !string.IsNullOrEmpty(parentDir))
                {
                    navCandidates.Add(Path.Combine(parentDir, "maps", $"{mapName}.nav"));
                    navCandidates.Add(Path.Combine(parentDir, $"{mapName}.nav"));
                }

                foreach (var cand in navCandidates)
                {
                    if (File.Exists(cand))
                    {
                        assets.Add(new AssetInfo
                        {
                            FullPath = $"maps/{mapName}.nav",
                            Type = AssetType.NavMesh,
                            Source = AssetSource.LooseFile,
                            SizeBytes = new FileInfo(cand).Length
                        });
                        break;
                    }
                }
            }

            // 2. Node Graph (.ain)
            var ainCandidates = new List<string>
            {
                Path.Combine(mapDir, "graphs", $"{mapName}.ain"),
                Path.Combine(mapDir, $"{mapName}.ain")
            };
            if (isInsideMapsFolder && !string.IsNullOrEmpty(parentDir))
            {
                ainCandidates.Add(Path.Combine(parentDir, "maps", "graphs", $"{mapName}.ain"));
            }
            foreach (var cand in ainCandidates)
            {
                if (File.Exists(cand))
                {
                    string path = $"maps/graphs/{mapName}.ain";
                    if (!assets.Any(a => a.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    {
                        assets.Add(new AssetInfo
                        {
                            FullPath = path,
                            Type = AssetType.Other,
                            Source = AssetSource.LooseFile,
                            SizeBytes = new FileInfo(cand).Length
                        });
                    }
                    break;
                }
            }

            // 3. Map Script / Story Briefing (.txt)
            var txtCandidates = new List<string>
            {
                Path.Combine(mapDir, $"{mapName}.txt"),
                Path.Combine(mapDir, "maps", $"{mapName}.txt")
            };
            foreach (var cand in txtCandidates)
            {
                if (File.Exists(cand))
                {
                    string path = $"maps/{mapName}.txt";
                    if (!assets.Any(a => a.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    {
                        assets.Add(new AssetInfo
                        {
                            FullPath = path,
                            Type = AssetType.Other,
                            Source = AssetSource.LooseFile,
                            SizeBytes = new FileInfo(cand).Length
                        });
                    }
                    break;
                }
            }

            // 4. Map KeyValues (.kv)
            var kvCandidates = new List<string>
            {
                Path.Combine(mapDir, $"{mapName}.kv"),
                Path.Combine(mapDir, "maps", $"{mapName}.kv")
            };
            foreach (var cand in kvCandidates)
            {
                if (File.Exists(cand))
                {
                    string path = $"maps/{mapName}.kv";
                    if (!assets.Any(a => a.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    {
                        assets.Add(new AssetInfo
                        {
                            FullPath = path,
                            Type = AssetType.Other,
                            Source = AssetSource.LooseFile,
                            SizeBytes = new FileInfo(cand).Length
                        });
                    }
                    break;
                }
            }
        }
        catch { }
    }

    // Helper to add skybox variants
    private static void AddSkyboxAssets(List<AssetInfo> assets, string skyboxName)
    {
        var variants = new[] { "", "_hdr" };
        foreach (var variant in variants)
        {
            string name = skyboxName + variant;
            var skyPaths = Conversion.SkyboxConverter.GetSkyboxPaths(name);
            foreach (string skyPath in skyPaths)
            {
                if (!assets.Any(a => a.FullPath.Equals(skyPath, StringComparison.OrdinalIgnoreCase)))
                {
                    assets.Add(new AssetInfo
                    {
                        FullPath = skyPath,
                        Type = AssetType.Skybox,
                        Source = AssetSource.VpkArchive,
                        SizeBytes = 0
                    });
                }
            }
        }
    }

    /// <summary>
    /// Discover assets from a VPK archive directory.
    /// </summary>
    public static List<AssetInfo> DiscoverFromVpk(VpkReader vpk)
    {
        var assets = new List<AssetInfo>();

        foreach (var entry in vpk.Entries)
        {
            assets.Add(new AssetInfo
            {
                FullPath = entry.FullPath,
                Source = AssetSource.VpkArchive,
                SizeBytes = entry.EntryLength + entry.PreloadData.Length,
                Type = ClassifyAsset(entry.FullPath)
            });
        }

        return assets;
    }

    /// <summary>
    /// Discover assets from a GMA (Garry's Mod Addon) archive.
    /// If the GMA contains an embedded map (.bsp), unpacks and discovers all its internal assets as well.
    /// </summary>
    public static List<AssetInfo> DiscoverFromGma(GmaReader gma)
    {
        var assets = new List<AssetInfo>();

        // Check if this GMA contains a map (.bsp)
        var bspEntry = gma.Entries.FirstOrDefault(e => e.Path.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase));
        if (bspEntry != null)
        {
            try
            {
                byte[]? bspData = gma.ExtractEntry(bspEntry);
                if (bspData != null && bspData.Length > 0)
                {
                    using var ms = new MemoryStream(bspData);
                    using var bsp = new BspReader(ms);
                    if (bsp.IsValid)
                    {
                        var bspAssets = DiscoverFromBsp(bsp, bspEntry.Path);
                        assets.AddRange(bspAssets);
                    }
                }
            }
            catch { }
        }

        // Add all loose files from the GMA
        foreach (var entry in gma.Entries)
        {
            string cleanPath = entry.Path.Replace('\\', '/').TrimStart('/');
            if (!assets.Any(a => a.FullPath.Equals(cleanPath, StringComparison.OrdinalIgnoreCase)))
            {
                var asset = new AssetInfo
                {
                    FullPath = cleanPath,
                    Source = AssetSource.GmaArchive,
                    SizeBytes = entry.Size,
                    Type = ClassifyAsset(cleanPath)
                };

                if (asset.Type == AssetType.Material)
                {
                    try
                    {
                        byte[]? vmtData = gma.ExtractEntry(entry);
                        if (vmtData != null)
                            asset.Material = VmtReader.Parse(vmtData);
                    }
                    catch { }
                }

                assets.Add(asset);
            }
        }

        return assets;
    }

    /// <summary>
    /// Classify an asset by its file path and extension.
    /// </summary>
    public static AssetType ClassifyAsset(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string dir = path.Replace('\\', '/').ToLowerInvariant();

        // Check directory context
        if (dir.Contains("skybox/"))
            return AssetType.Skybox;

        return ext switch
        {
            ".vtf" => AssetType.Texture,
            ".vmt" => AssetType.Material,
            ".mdl" => AssetType.Model,
            ".wav" or ".mp3" or ".ogg" => AssetType.Sound,
            ".nav" => AssetType.NavMesh,
            _ => AssetType.Other
        };
    }

    /// <summary>
    /// Group assets by type for UI display.
    /// </summary>
    public static Dictionary<AssetType, List<AssetInfo>> GroupByType(List<AssetInfo> assets)
    {
        return assets
            .GroupBy(a => a.Type)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.FullPath).ToList());
    }

    /// <summary>
    /// Get summary statistics for a list of assets.
    /// </summary>
    public static (int textures, int models, int sounds, int skyboxes, int other) GetStats(List<AssetInfo> assets)
    {
        return (
            assets.Count(a => a.Type == AssetType.Texture || a.Type == AssetType.Material),
            assets.Count(a => a.Type == AssetType.Model),
            assets.Count(a => a.Type == AssetType.Sound),
            assets.Count(a => a.Type == AssetType.Skybox),
            assets.Count(a => a.Type == AssetType.Other || a.Type == AssetType.NavMesh)
        );
    }
}
