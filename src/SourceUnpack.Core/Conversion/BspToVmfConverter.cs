using System.Globalization;
using System.Text;
using SourceUnpack.Core.Formats.Bsp;

namespace SourceUnpack.Core.Conversion;

/// <summary>
/// Standalone BSP → VMF decompiler.
/// Completely optional — does not affect the main extraction pipeline.
/// Opens its own BspReader, converts brush geometry + entities, and writes a .vmf file.
/// </summary>
public static class BspToVmfConverter
{
    /// <summary>
    /// Convert a BSP file to VMF and save to the specified output path.
    /// Returns true on success.
    /// </summary>
    public static bool Convert(string bspPath, string vmfOutputPath)
    {
        using var bsp = new BspReader(bspPath);
        if (!bsp.IsValid) return false;

        return Convert(bsp, vmfOutputPath);
    }

    /// <summary>
    /// Convert using an already-opened BspReader.
    /// </summary>
    public static bool Convert(BspReader bsp, string vmfOutputPath)
    {
        try
        {
            var planes = bsp.ReadPlanes();
            var brushes = bsp.ReadBrushes();
            var brushSides = bsp.ReadBrushSides();
            var texinfos = bsp.ReadTexinfo();
            var texdatas = bsp.ReadTexdata();
            var textureNames = bsp.GetTextureNames();
            var entities = bsp.ParseEntities();

            // Build texture name lookup: texdata index → material name
            var texNameLookup = new Dictionary<int, string>();
            for (int i = 0; i < texdatas.Length; i++)
            {
                int nameId = texdatas[i].NameStringTableID;
                if (nameId >= 0 && nameId < textureNames.Count)
                    texNameLookup[i] = textureNames[nameId];
                else
                    texNameLookup[i] = "TOOLS/TOOLSNODRAW";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(vmfOutputPath)!);

            using var writer = new StreamWriter(vmfOutputPath, false, Encoding.UTF8);
            int nextId = 1;

            // ── versioninfo ──
            writer.WriteLine("versioninfo");
            writer.WriteLine("{");
            writer.WriteLine("\t\"editorversion\" \"400\"");
            writer.WriteLine($"\t\"mapversion\" \"{bsp.Header.MapRevision}\"");
            writer.WriteLine("\t\"formatversion\" \"100\"");
            writer.WriteLine("\t\"prefab\" \"0\"");
            writer.WriteLine("}");

            // ── visgroups ──
            writer.WriteLine("visgroups");
            writer.WriteLine("{");
            writer.WriteLine("}");

            // ── viewsettings ──
            writer.WriteLine("viewsettings");
            writer.WriteLine("{");
            writer.WriteLine("}");

            // ── world (worldspawn + all brushes) ──
            writer.WriteLine("world");
            writer.WriteLine("{");
            writer.WriteLine($"\t\"id\" \"{nextId++}\"");
            writer.WriteLine($"\t\"mapversion\" \"{bsp.Header.MapRevision}\"");
            writer.WriteLine("\t\"classname\" \"worldspawn\"");

            // Copy worldspawn keys from entity lump
            var worldspawn = entities.FirstOrDefault(e =>
                e.TryGetValue("classname", out var cls) && cls == "worldspawn");
            if (worldspawn != null)
            {
                foreach (var kvp in worldspawn)
                {
                    if (kvp.Key.Equals("classname", StringComparison.OrdinalIgnoreCase)) continue;
                    writer.WriteLine($"\t\"{kvp.Key}\" \"{kvp.Value}\"");
                }
            }

            // Write all brushes as solids
            for (int bi = 0; bi < brushes.Length; bi++)
            {
                var brush = brushes[bi];
                if (brush.NumSides < 4) continue; // Need at least 4 planes for a valid solid

                // Skip bevel-only sides
                bool hasRealSides = false;
                for (int si = 0; si < brush.NumSides; si++)
                {
                    int sideIdx = brush.FirstSide + si;
                    if (sideIdx >= brushSides.Length) break;
                    if (brushSides[sideIdx].Bevel == 0)
                    {
                        hasRealSides = true;
                        break;
                    }
                }
                if (!hasRealSides) continue;

                writer.WriteLine("\tsolid");
                writer.WriteLine("\t{");
                writer.WriteLine($"\t\t\"id\" \"{nextId++}\"");

                for (int si = 0; si < brush.NumSides; si++)
                {
                    int sideIdx = brush.FirstSide + si;
                    if (sideIdx >= brushSides.Length) break;

                    var side = brushSides[sideIdx];
                    if (side.Bevel != 0) continue; // Skip bevel sides

                    if (side.PlaneNum >= planes.Length) continue;
                    var plane = planes[side.PlaneNum];

                    // Get texture name
                    string material = "TOOLS/TOOLSNODRAW";
                    float uAxisX = 1, uAxisY = 0, uAxisZ = 0, uShift = 0, uScale = 0.25f;
                    float vAxisX = 0, vAxisY = -1, vAxisZ = 0, vShift = 0, vScale = 0.25f;

                    if (side.TexinfoIndex >= 0 && side.TexinfoIndex < texinfos.Length)
                    {
                        var ti = texinfos[side.TexinfoIndex];
                        if (ti.TexdataIndex >= 0 && texNameLookup.TryGetValue(ti.TexdataIndex, out var matName))
                            material = matName;

                        // Extract UV axes from texinfo vectors
                        uAxisX = ti.TextureVecs[0];
                        uAxisY = ti.TextureVecs[1];
                        uAxisZ = ti.TextureVecs[2];
                        uShift = ti.TextureVecs[3];

                        vAxisX = ti.TextureVecs[4];
                        vAxisY = ti.TextureVecs[5];
                        vAxisZ = ti.TextureVecs[6];
                        vShift = ti.TextureVecs[7];

                        // Compute scale from axis magnitude
                        float uMag = MathF.Sqrt(uAxisX * uAxisX + uAxisY * uAxisY + uAxisZ * uAxisZ);
                        float vMag = MathF.Sqrt(vAxisX * vAxisX + vAxisY * vAxisY + vAxisZ * vAxisZ);

                        if (uMag > 0.0001f)
                        {
                            uScale = 1.0f / uMag;
                            uAxisX /= uMag; uAxisY /= uMag; uAxisZ /= uMag;
                        }
                        if (vMag > 0.0001f)
                        {
                            vScale = 1.0f / vMag;
                            vAxisX /= vMag; vAxisY /= vMag; vAxisZ /= vMag;
                        }
                    }

                    // Convert plane (normal, distance) → 3 points
                    var (p1, p2, p3) = PlaneToThreePoints(
                        plane.NormalX, plane.NormalY, plane.NormalZ, plane.Distance);

                    writer.WriteLine("\t\tside");
                    writer.WriteLine("\t\t{");
                    writer.WriteLine($"\t\t\t\"id\" \"{nextId++}\"");
                    writer.WriteLine($"\t\t\t\"plane\" \"{FormatPoint(p1)} {FormatPoint(p2)} {FormatPoint(p3)}\"");
                    writer.WriteLine($"\t\t\t\"material\" \"{material}\"");
                    writer.WriteLine($"\t\t\t\"uaxis\" \"[{F(uAxisX)} {F(uAxisY)} {F(uAxisZ)} {F(uShift)}] {F(uScale)}\"");
                    writer.WriteLine($"\t\t\t\"vaxis\" \"[{F(vAxisX)} {F(vAxisY)} {F(vAxisZ)} {F(vShift)}] {F(vScale)}\"");
                    writer.WriteLine("\t\t\t\"rotation\" \"0\"");
                    writer.WriteLine("\t\t\t\"lightmapscale\" \"16\"");
                    writer.WriteLine("\t\t\t\"smoothing_groups\" \"0\"");
                    writer.WriteLine("\t\t}");
                }

                writer.WriteLine("\t}");
            }

            writer.WriteLine("}"); // close world

            // ── Point/brush entities ──
            foreach (var ent in entities)
            {
                if (ent.TryGetValue("classname", out var cls) && cls == "worldspawn")
                    continue; // Already handled

                writer.WriteLine("entity");
                writer.WriteLine("{");
                writer.WriteLine($"\t\"id\" \"{nextId++}\"");

                foreach (var kvp in ent)
                {
                    writer.WriteLine($"\t\"{kvp.Key}\" \"{kvp.Value}\"");
                }

                // If entity has a model reference like *1, *2, attach those brushes
                if (ent.TryGetValue("model", out var modelRef) && modelRef.StartsWith("*"))
                {
                    // Model references are like *1, *2, etc. — index into the BSP models array
                    // We don't have brush-per-model mapping in the lump data we parsed,
                    // so we note it as a comment. Full decompilers use face/leaf data for this.
                }

                writer.WriteLine("}");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Convert a plane (normal + distance) into 3 non-collinear points on the plane.
    /// </summary>
    private static ((float x, float y, float z), (float x, float y, float z), (float x, float y, float z))
        PlaneToThreePoints(float nx, float ny, float nz, float dist)
    {
        // Find two vectors tangent to the plane
        float ax, ay, az;
        if (MathF.Abs(nz) > 0.5f)
        {
            // Normal is mostly Z — use X as reference
            float d = 1.0f / MathF.Sqrt(ny * ny + nz * nz);
            ax = 0; ay = -nz * d; az = ny * d;
        }
        else
        {
            // Normal is mostly X or Y — use Z as reference
            float d = 1.0f / MathF.Sqrt(nx * nx + ny * ny);
            ax = -ny * d; ay = nx * d; az = 0;
        }

        // Second tangent = normal × first tangent
        float bx = ny * az - nz * ay;
        float by = nz * ax - nx * az;
        float bz = nx * ay - ny * ax;

        // Point on plane: P = normal * dist
        float px = nx * dist;
        float py = ny * dist;
        float pz = nz * dist;

        // Scale tangent offsets by a large value to avoid degenerate triangles
        const float S = 1024.0f;

        var p1 = (px, py, pz);
        var p2 = (px + ax * S, py + ay * S, pz + az * S);
        var p3 = (px + bx * S, py + by * S, pz + bz * S);

        return (p1, p2, p3);
    }

    private static string FormatPoint((float x, float y, float z) p)
        => $"({F(p.x)} {F(p.y)} {F(p.z)})";

    private static string F(float v)
        => v.ToString("G6", CultureInfo.InvariantCulture);
}
