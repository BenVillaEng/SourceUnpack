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
    public string? BumpMap { get; set; }
    public string? EnvMapMask { get; set; }
    public string? NormalMap { get; set; }
    public string? PhongExponentTexture { get; set; }
    public string? DetailTexture { get; set; }
    public string? SelfIllumMask { get; set; }
    public string? Include { get; set; } // For Patch materials

    public Dictionary<string, string> AllProperties { get; set; } = new();

    public bool HasNormalMap => !string.IsNullOrEmpty(BumpMap) || !string.IsNullOrEmpty(NormalMap);
    public bool HasSpecular => !string.IsNullOrEmpty(EnvMapMask) || !string.IsNullOrEmpty(PhongExponentTexture);
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
}

public enum ModelFormat
{
    Obj,
    Fbx,
    Gltf,
    Glb
}

public enum TextureFormat
{
    Png,
    Jpg
}
