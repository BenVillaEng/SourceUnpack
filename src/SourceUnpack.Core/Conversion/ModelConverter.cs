using System.Globalization;
using System.Text;
using SourceUnpack.Core.Formats.Mdl;

namespace SourceUnpack.Core.Conversion;

/// <summary>
/// Converts parsed Source Engine MDL model data to OBJ format.
/// Supports static props with materials. Skeletal/animated models are not yet supported.
/// </summary>
public static class ModelConverter
{
    /// <summary>
    /// Export a parsed model to Wavefront OBJ format.
    /// Returns the OBJ file content as a string.
    /// </summary>
    public static string ToObj(MdlModelData model, string? mtlFileName = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# SourceUnpack — Exported from Source Engine MDL");
        sb.AppendLine($"# Model: {model.Name}");
        sb.AppendLine($"# Vertices: {model.BodyParts.Sum(bp => bp.Meshes.Sum(m => m.Vertices.Count))}");
        sb.AppendLine();

        if (mtlFileName != null)
            sb.AppendLine($"mtllib {mtlFileName}");

        int vertexOffset = 1; // OBJ indices are 1-based

        foreach (var bodyPart in model.BodyParts)
        {
            sb.AppendLine($"g {SanitizeName(bodyPart.Name)}");

            foreach (var mesh in bodyPart.Meshes)
            {
                sb.AppendLine($"usemtl {SanitizeName(mesh.MaterialName)}");

                // Write vertices
                foreach (var v in mesh.Vertices)
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "v {0:F6} {1:F6} {2:F6}", v.PosX, v.PosY, v.PosZ));
                }

                // Write normals
                foreach (var v in mesh.Vertices)
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "vn {0:F6} {1:F6} {2:F6}", v.NormX, v.NormY, v.NormZ));
                }

                // Write tex coords
                foreach (var v in mesh.Vertices)
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "vt {0:F6} {1:F6}", v.TexU, 1.0f - v.TexV)); // Flip V for OBJ
                }

                // Write faces (triangles)
                for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
                {
                    int a = mesh.Indices[i] + vertexOffset;
                    int b = mesh.Indices[i + 1] + vertexOffset;
                    int c = mesh.Indices[i + 2] + vertexOffset;
                    sb.AppendLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
                }

                vertexOffset += mesh.Vertices.Count;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generate a basic MTL (material library) file for the model's materials.
    /// </summary>
    public static string GenerateMtl(MdlModelData model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SourceUnpack — Material Library");
        sb.AppendLine();

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var bodyPart in model.BodyParts)
        {
            foreach (var mesh in bodyPart.Meshes)
            {
                string matName = SanitizeName(mesh.MaterialName);
                if (written.Contains(matName)) continue;
                written.Add(matName);

                sb.AppendLine($"newmtl {matName}");
                sb.AppendLine("Ka 0.200000 0.200000 0.200000");
                sb.AppendLine("Kd 0.800000 0.800000 0.800000");
                sb.AppendLine("Ks 0.000000 0.000000 0.000000");
                sb.AppendLine("Ns 10.000000");
                sb.AppendLine("d 1.000000");
                sb.AppendLine("illum 2");

                // Reference texture if material name is a path
                string texPath = mesh.MaterialName.Replace('\\', '/');
                if (!string.IsNullOrEmpty(texPath))
                    sb.AppendLine($"map_Kd {texPath}_color.png");

                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Export a model to OBJ + MTL files at the specified output directory.
    /// Returns true if successful.
    /// </summary>
    public static bool ExportObj(MdlModelData model, string outputDirectory, string baseName)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);

            string mtlFileName = baseName + ".mtl";
            string objContent = ToObj(model, mtlFileName);
            string mtlContent = GenerateMtl(model);

            File.WriteAllText(Path.Combine(outputDirectory, baseName + ".obj"), objContent);
            File.WriteAllText(Path.Combine(outputDirectory, mtlFileName), mtlContent);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "unnamed";

        string sanitized = Path.GetFileNameWithoutExtension(name);
        sanitized = sanitized.Replace(' ', '_').Replace('/', '_').Replace('\\', '_');

        if (string.IsNullOrEmpty(sanitized)) return "unnamed";
        return sanitized;
    }
}
