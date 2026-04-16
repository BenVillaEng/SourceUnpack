using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SourceUnpack.Core.Formats.Vtf;

namespace SourceUnpack.Core.Conversion;

/// <summary>
/// Converts Source Engine skybox textures (6 cubemap faces) to PNG images.
/// Optionally assembles them into a single horizontal-cross cubemap layout.
/// </summary>
public static class SkyboxConverter
{
    /// <summary>Skybox face suffixes used by Source Engine.</summary>
    public static readonly string[] FaceSuffixes = { "ft", "bk", "lf", "rt", "up", "dn" };

    /// <summary>Human-readable face names.</summary>
    public static readonly string[] FaceNames = { "Front", "Back", "Left", "Right", "Up", "Down" };

    /// <summary>
    /// Convert individual skybox face VTF data to separate PNG files.
    /// </summary>
    /// <param name="skyboxName">Base skybox name (e.g., "sky_day01_01")</param>
    /// <param name="faceData">Dictionary of suffix → VTF byte data</param>
    /// <param name="outputDirectory">Where to save the PNGs</param>
    /// <returns>Number of faces successfully converted</returns>
    public static int ConvertFaces(string skyboxName, Dictionary<string, byte[]> faceData, string outputDirectory, SourceUnpack.Core.Models.TextureFormat format = SourceUnpack.Core.Models.TextureFormat.Png)
    {
        Directory.CreateDirectory(outputDirectory);
        int count = 0;
        string ext = TextureConverter.GetExtension(format);

        foreach (var suffix in FaceSuffixes)
        {
            if (!faceData.TryGetValue(suffix, out byte[]? vtfBytes)) continue;

            string outputPath = Path.Combine(outputDirectory, $"{skyboxName}{suffix}{ext}");
            if (TextureConverter.VtfToFile(vtfBytes, outputPath, format))
                count++;
        }

        return count;
    }

    /// <summary>
    /// Assemble skybox faces into a horizontal cross cubemap layout and save as PNG.
    /// Layout:
    ///        [UP]
    /// [LF] [FT] [RT] [BK]
    ///        [DN]
    /// </summary>
    public static bool AssembleCubemap(string skyboxName, Dictionary<string, byte[]> faceData, string outputPath)
    {
        try
        {
            // Decode all faces
            var faces = new Dictionary<string, Image<Rgba32>>();
            int faceSize = 0;

            foreach (var suffix in FaceSuffixes)
            {
                if (!faceData.TryGetValue(suffix, out byte[]? vtfBytes)) continue;

                var vtf = new VtfReader();
                if (!vtf.Load(vtfBytes)) continue;

                var img = Image.LoadPixelData<Rgba32>(vtf.PixelData, vtf.Header.Width, vtf.Header.Height);
                faces[suffix] = img;
                faceSize = Math.Max(faceSize, vtf.Header.Width);
            }

            if (faces.Count == 0 || faceSize == 0) return false;

            // Horizontal cross layout: 4 wide x 3 tall
            int width = faceSize * 4;
            int height = faceSize * 3;

            using var cubemap = new Image<Rgba32>(width, height);

            // Place faces in cross layout
            PlaceFace(cubemap, faces, "up", 1, 0, faceSize); // Top center
            PlaceFace(cubemap, faces, "lf", 0, 1, faceSize); // Middle left
            PlaceFace(cubemap, faces, "ft", 1, 1, faceSize); // Middle center
            PlaceFace(cubemap, faces, "rt", 2, 1, faceSize); // Middle right
            PlaceFace(cubemap, faces, "bk", 3, 1, faceSize); // Middle far right
            PlaceFace(cubemap, faces, "dn", 1, 2, faceSize); // Bottom center

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            cubemap.SaveAsPng(outputPath);

            // Dispose individual faces
            foreach (var face in faces.Values)
                face.Dispose();

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void PlaceFace(Image<Rgba32> target, Dictionary<string, Image<Rgba32>> faces,
        string suffix, int gridX, int gridY, int faceSize)
    {
        if (!faces.TryGetValue(suffix, out var face)) return;

        // Resize if needed
        if (face.Width != faceSize || face.Height != faceSize)
            face.Mutate(x => x.Resize(faceSize, faceSize));

        // Copy pixels
        for (int y = 0; y < faceSize && y < face.Height; y++)
        {
            for (int x = 0; x < faceSize && x < face.Width; x++)
            {
                target[gridX * faceSize + x, gridY * faceSize + y] = face[x, y];
            }
        }
    }

    /// <summary>
    /// Get the VTF file paths for a skybox by name.
    /// Returns paths like "materials/skybox/sky_day01_01ft.vtf"
    /// </summary>
    public static string[] GetSkyboxPaths(string skyboxName)
    {
        return FaceSuffixes.Select(s => $"materials/skybox/{skyboxName}{s}.vtf").ToArray();
    }
}
