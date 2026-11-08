namespace SourceUnpack.Core.Models;

/// <summary>
/// Types of assets that can be extracted from Source Engine files.
/// </summary>
public enum AssetType
{
    Texture,
    Model,
    Sound,
    Skybox,
    Material,
    NavMesh,
    Other
}

/// <summary>
/// Source of the asset (where it was found).
/// </summary>
public enum AssetSource
{
    BspPakfile,
    VpkArchive,
    LooseFile,
    GmaArchive
}

/// <summary>
/// Represents a discovered asset within a BSP or VPK.
/// </summary>
public class AssetInfo
{
    public string FullPath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(FullPath);
    public string Directory => Path.GetDirectoryName(FullPath)?.Replace('\\', '/') ?? string.Empty;
    public AssetType Type { get; set; }
    public AssetSource Source { get; set; }
    public long SizeBytes { get; set; }
    public string Extension => Path.GetExtension(FullPath).ToLowerInvariant();

    /// <summary>For textures: material properties parsed from VMT.</summary>
    public MaterialInfo? Material { get; set; }

    public override string ToString() => $"[{Type}] {FullPath} ({SizeBytes} bytes)";
}

/// <summary>
/// Material information parsed from a VMT file.
/// </summary>
public class MaterialInfo
{
    public string ShaderName { get; set; } = string.Empty;
    public string? BaseTexture { get; set; }
    public string? BaseTexture2 { get; set; }
    public string? BumpMap { get; set; }
    public string? BumpMap2 { get; set; }
    public string? EnvMapMask { get; set; }
    public string? NormalMap { get; set; }
    public string? PhongExponentTexture { get; set; }
    public string? DetailTexture { get; set; }
    public string? BlendModulateTexture { get; set; }
    public string? LightwarpTexture { get; set; }
    public string? SelfIllumMask { get; set; }
    public string? SelfIllumTexture { get; set; }
    public string? AmbientOcclTexture { get; set; }
    public string? RefractTexture { get; set; }
    public string? DuDvMap { get; set; }
    public string? Masks1 { get; set; }
    public string? Masks2 { get; set; }
    public string? Include { get; set; } // For Patch materials

    public Dictionary<string, string> AllProperties { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasNormalMap => !string.IsNullOrEmpty(BumpMap) || !string.IsNullOrEmpty(NormalMap);
    public bool HasSpecular => !string.IsNullOrEmpty(EnvMapMask) || !string.IsNullOrEmpty(PhongExponentTexture);

    /// <summary>
    /// Returns all texture asset paths referenced by this material.
    /// </summary>
    public IEnumerable<string> GetAllReferencedTextures()
    {
        var list = new List<string?>
        {
            BaseTexture,
            BaseTexture2,
            BumpMap,
            BumpMap2,
            NormalMap,
            EnvMapMask,
            PhongExponentTexture,
            DetailTexture,
            BlendModulateTexture,
            LightwarpTexture,
            SelfIllumMask,
            SelfIllumTexture,
            AmbientOcclTexture,
            RefractTexture,
            DuDvMap,
            Masks1,
            Masks2
        };

        foreach (var kvp in AllProperties)
        {
            string val = kvp.Value.Trim().Trim('"', '\'');
            if (string.IsNullOrEmpty(val)) continue;
            if (val.StartsWith("_") || val.StartsWith("[") || val.StartsWith("{")) continue;
            if (val.Equals("env_cubemap", StringComparison.OrdinalIgnoreCase)) continue;

            string key = kvp.Key;
            if (val.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("texture", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("map", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("mask", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(val);
            }
        }

        return list.Where(s => !string.IsNullOrWhiteSpace(s))
                   .Select(s => s!.Replace('\\', '/').Trim().Trim('"', '\''))
                   .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Options for how assets should be converted.
/// </summary>
public class ConversionOptions
{
    public string OutputDirectory { get; set; } = string.Empty;
    public ModelFormat ModelExportFormat { get; set; } = ModelFormat.Obj;
    public bool PreserveDirectoryStructure { get; set; } = true;
    public bool ExportSeparateMaterialMaps { get; set; } = true;
    public bool AssembleSkyboxCubemap { get; set; } = false;
    public bool ExtractGameDependencies { get; set; } = true;
    public bool GenerateMissingPlaceholders { get; set; } = false;
    public bool SkipExistingFiles { get; set; } = false;
    public bool KeepMdlFile { get; set; } = false;
    public TextureFormat TextureOutputFormat { get; set; } = TextureFormat.Png;

    /// <summary>
    /// When true, unpacks all assets with their original Valve file extensions (.vtf, .mdl+companions, .vmt, .nav, etc.)
    /// without converting to .obj, .png, etc.
    /// </summary>
    public bool ExtractOriginalAssets { get; set; } = false;
}

public enum ModelFormat
{
    Obj,
    Fbx,
    Gltf,
    Glb,
    Original
}

public enum TextureFormat
{
    Png,
    Jpg,
    Original
}
