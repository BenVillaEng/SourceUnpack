using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SourceUnpack.Core.Formats.Vtf;
using SourceUnpack.Core.Models;

namespace SourceUnpack.Core.Conversion;

/// <summary>
/// Converts VTF texture data to PNG or JPG images.
/// Handles material maps (color, normal, roughness, etc.) based on VMT info.
/// </summary>
public static class TextureConverter
{
    /// <summary>
    /// Convert raw VTF file bytes to a PNG byte array.
    /// Returns null if the VTF cannot be decoded.
    /// </summary>
    public static byte[]? VtfToPng(byte[] vtfData)
    {
        var reader = new VtfReader();
        if (!reader.Load(vtfData))
            return null;

        return RgbaToPng(reader.PixelData, reader.Header.Width, reader.Header.Height);
    }

    /// <summary>
    /// Convert raw VTF file bytes to a JPG byte array.
    /// Returns null if the VTF cannot be decoded.
    /// </summary>
    public static byte[]? VtfToJpg(byte[] vtfData, int quality = 90)
    {
        var reader = new VtfReader();
        if (!reader.Load(vtfData))
            return null;

        return RgbaToJpg(reader.PixelData, reader.Header.Width, reader.Header.Height, quality);
    }

    /// <summary>
    /// Convert VTF data and save directly to a file path as PNG.
    /// Returns true if successful.
    /// </summary>
    public static bool VtfToPngFile(byte[] vtfData, string outputPath)
    {
        var pngData = VtfToPng(vtfData);
        if (pngData == null) return false;

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);
        System.IO.File.WriteAllBytes(outputPath, pngData);
        return true;
    }

    /// <summary>
    /// Convert VTF data and save directly to a file path as JPG.
    /// Returns true if successful.
    /// </summary>
    public static bool VtfToJpgFile(byte[] vtfData, string outputPath, int quality = 90)
    {
        var jpgData = VtfToJpg(vtfData, quality);
        if (jpgData == null) return false;

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);
        System.IO.File.WriteAllBytes(outputPath, jpgData);
        return true;
    }

    /// <summary>
    /// Convert VTF data and save to a file using the specified texture format.
    /// Returns true if successful.
    /// </summary>
    public static bool VtfToFile(byte[] vtfData, string outputPath, TextureFormat format)
    {
        return format switch
        {
            TextureFormat.Jpg => VtfToJpgFile(vtfData, outputPath),
            _ => VtfToPngFile(vtfData, outputPath)
        };
    }

    /// <summary>
    /// Get the file extension for the specified texture format.
    /// </summary>
    public static string GetExtension(TextureFormat format)
    {
        return format switch
        {
            TextureFormat.Jpg => ".jpg",
            _ => ".png"
        };
    }

    /// <summary>
    /// Save RGBA pixel data as PNG using ImageSharp.
    /// </summary>
    public static byte[] RgbaToPng(byte[] rgba, int width, int height)
    {
        using var image = Image.LoadPixelData<Rgba32>(rgba, width, height);
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Save RGBA pixel data as JPG using ImageSharp.
    /// </summary>
    public static byte[] RgbaToJpg(byte[] rgba, int width, int height, int quality = 90)
    {
        using var image = Image.LoadPixelData<Rgba32>(rgba, width, height);
        using var ms = new MemoryStream();
        var encoder = new JpegEncoder { Quality = quality };
        image.Save(ms, encoder);
        return ms.ToArray();
    }

    /// <summary>
    /// Get the suffix for a texture map type based on VMT property name.
    /// </summary>
    public static string GetMapSuffix(string vmtProperty)
    {
        return vmtProperty.ToLowerInvariant() switch
        {
            "$basetexture" => "_color",
            "$bumpmap" or "$normalmap" => "_normal",
            "$envmapmask" => "_roughness",
            "$phongexponenttexture" => "_specular",
            "$detail" => "_detail",
            "$selfillummask" => "_emissive",
            _ => ""
        };
    }
}
