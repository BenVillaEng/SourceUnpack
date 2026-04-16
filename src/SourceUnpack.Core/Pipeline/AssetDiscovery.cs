using SourceUnpack.Core.Formats.Bsp;
using SourceUnpack.Core.Formats.Gma;
using SourceUnpack.Core.Formats.Vmt;
using SourceUnpack.Core.Formats.Vpk;
using SourceUnpack.Core.Models;

namespace SourceUnpack.Core.Pipeline;

/// <summary>
/// Scans BSP pakfiles and VPK archives to discover and categorize all assets.
/// Builds a structured asset tree for UI display. 
/// </summary>
public class AssetDiscovery
{
    /// <summary>
    /// Discover all assets embedded in a BSP file's pakfile lump.
    /// </summary>
    public static List<AssetInfo> DiscoverFromBsp(BspReader bsp)
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



        // Add model references from static props
        foreach (string modelName in bsp.GetStaticPropNames())
        {
            string modelPath = modelName.Replace('\\', '/');
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

        // Add sound references from entities
        var soundKeys = new[] { "message", "sound", "noise1", "noise2", "startsound", "stopsound", "shoot", "flysound" };
        var entities = bsp.ParseEntities();
        foreach (var entity in entities)
        {
            foreach (var key in soundKeys)
            {
                if (entity.TryGetValue(key, out string? soundPath) && !string.IsNullOrWhiteSpace(soundPath))
                {
                   // Clean up path
                   soundPath = soundPath.Replace('\\', '/').TrimStart('/', '\\');
                   
                   // Some sounds start with *, #, etc. for special playback
                   soundPath = soundPath.TrimStart('*', '#', '!', '@', ')');

                   // Strict extension check to avoid SoundScript keys (e.g. "Breakable.Concrete")
                   var ext = System.IO.Path.GetExtension(soundPath).ToLowerInvariant();
                   if (ext != ".wav" && ext != ".mp3" && ext != ".ogg") continue;

                   string fullPath = $"sound/{soundPath}";
                   if (!soundPath.StartsWith("sound/", StringComparison.OrdinalIgnoreCase))
                       fullPath = $"sound/{soundPath}";
                   else
                       fullPath = soundPath;

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

        return assets;
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
    /// </summary>
    public static List<AssetInfo> DiscoverFromGma(GmaReader gma)
    {
        var assets = new List<AssetInfo>();

        foreach (var entry in gma.Entries)
        {
            assets.Add(new AssetInfo
            {
                FullPath = entry.Path,
                Source = AssetSource.GmaArchive,
                SizeBytes = entry.Size,
                Type = ClassifyAsset(entry.Path)
            });
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
            assets.Count(a => a.Type == AssetType.Other)
        );
    }
}
