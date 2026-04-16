using System.Globalization;
using System.Text;
using System.Text.Json;
using SourceUnpack.Core.Formats.Mdl;

namespace SourceUnpack.Core.Conversion;

/// <summary>
/// Converts parsed Source Engine MDL model data to glTF 2.0 format (.gltf + .bin).
/// Supports static props with mesh geometry, normals, and UVs.
/// </summary>
public static class GltfConverter
{
    /// <summary>
    /// Export a model to glTF 2.0 format (separate .gltf + .bin files).
    /// Returns true if successful.
    /// </summary>
    public static bool ExportGltf(MdlModelData model, string outputDirectory, string baseName)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);

            // Collect all vertex/index data
            var positions = new List<float>();
            var normals = new List<float>();
            var texCoords = new List<float>();
            var indices = new List<int>();
            int vertexOffset = 0;
            var meshInfos = new List<(int indexStart, int indexCount, string materialName)>();

            foreach (var bodyPart in model.BodyParts)
            {
                foreach (var mesh in bodyPart.Meshes)
                {
                    int indexStart = indices.Count;

                    foreach (var v in mesh.Vertices)
                    {
                        positions.Add(v.PosX); positions.Add(v.PosZ); positions.Add(-v.PosY); // Source→glTF coord swap
                        normals.Add(v.NormX); normals.Add(v.NormZ); normals.Add(-v.NormY);
                        texCoords.Add(v.TexU); texCoords.Add(v.TexV);
                    }

                    for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
                    {
                        indices.Add(mesh.Indices[i] + vertexOffset);
                        indices.Add(mesh.Indices[i + 1] + vertexOffset);
                        indices.Add(mesh.Indices[i + 2] + vertexOffset);
                    }

                    meshInfos.Add((indexStart, indices.Count - indexStart, mesh.MaterialName));
                    vertexOffset += mesh.Vertices.Count;
                }
            }

            if (positions.Count == 0) return false;

            // Build binary buffer
            var binData = BuildBinaryBuffer(positions, normals, texCoords, indices);
            string binFileName = baseName + ".bin";
            File.WriteAllBytes(Path.Combine(outputDirectory, binFileName), binData);

            // Build glTF JSON
            string gltfJson = BuildGltfJson(model, binFileName, binData.Length, positions, normals, texCoords, indices, meshInfos);
            File.WriteAllText(Path.Combine(outputDirectory, baseName + ".gltf"), gltfJson);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Export a model to a single self-contained GLB binary file.
    /// Returns true if successful.
    /// </summary>
    public static bool ExportGlb(MdlModelData model, string outputPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPath)!;
            Directory.CreateDirectory(dir);

            // Collect all vertex/index data
            var positions = new List<float>();
            var normals = new List<float>();
            var texCoords = new List<float>();
            var indices = new List<int>();
            int vertexOffset = 0;
            var meshInfos = new List<(int indexStart, int indexCount, string materialName)>();

            foreach (var bodyPart in model.BodyParts)
            {
                foreach (var mesh in bodyPart.Meshes)
                {
                    int indexStart = indices.Count;
                    foreach (var v in mesh.Vertices)
                    {
                        positions.Add(v.PosX); positions.Add(v.PosZ); positions.Add(-v.PosY);
                        normals.Add(v.NormX); normals.Add(v.NormZ); normals.Add(-v.NormY);
                        texCoords.Add(v.TexU); texCoords.Add(v.TexV);
                    }

                    for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
                    {
                        indices.Add(mesh.Indices[i] + vertexOffset);
                        indices.Add(mesh.Indices[i + 1] + vertexOffset);
                        indices.Add(mesh.Indices[i + 2] + vertexOffset);
                    }

                    meshInfos.Add((indexStart, indices.Count - indexStart, mesh.MaterialName));
                    vertexOffset += mesh.Vertices.Count;
                }
            }

            if (positions.Count == 0) return false;

            byte[] binData = BuildBinaryBuffer(positions, normals, texCoords, indices);

            // GLB uses embedded buffer (no URI)
            string gltfJson = BuildGltfJson(model, null, binData.Length, positions, normals, texCoords, indices, meshInfos);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(gltfJson);

            // Pad JSON to 4-byte alignment
            int jsonPadding = (4 - (jsonBytes.Length % 4)) % 4;
            byte[] paddedJson = new byte[jsonBytes.Length + jsonPadding];
            Array.Copy(jsonBytes, paddedJson, jsonBytes.Length);
            for (int i = jsonBytes.Length; i < paddedJson.Length; i++) paddedJson[i] = 0x20; // space

            // Pad binary to 4-byte alignment
            int binPadding = (4 - (binData.Length % 4)) % 4;
            byte[] paddedBin = new byte[binData.Length + binPadding];
            Array.Copy(binData, paddedBin, binData.Length);

            // GLB header: magic(4) + version(4) + length(4)
            // Chunk 0 (JSON): length(4) + type(4) + data
            // Chunk 1 (BIN):  length(4) + type(4) + data
            int totalLength = 12 + 8 + paddedJson.Length + 8 + paddedBin.Length;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // GLB header
            bw.Write(0x46546C67); // "glTF"
            bw.Write(2);          // version
            bw.Write(totalLength);

            // JSON chunk
            bw.Write(paddedJson.Length);
            bw.Write(0x4E4F534A); // "JSON"
            bw.Write(paddedJson);

            // BIN chunk
            bw.Write(paddedBin.Length);
            bw.Write(0x004E4942); // "BIN\0"
            bw.Write(paddedBin);

            File.WriteAllBytes(outputPath, ms.ToArray());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] BuildBinaryBuffer(List<float> positions, List<float> normals, List<float> texCoords, List<int> indices)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Write positions
        foreach (float f in positions) bw.Write(f);
        // Write normals
        foreach (float f in normals) bw.Write(f);
        // Write texCoords
        foreach (float f in texCoords) bw.Write(f);
        // Write indices as uint32
        foreach (int i in indices) bw.Write((uint)i);

        return ms.ToArray();
    }

    private static string BuildGltfJson(MdlModelData model, string? binUri, int bufferByteLength,
        List<float> positions, List<float> normals, List<float> texCoords, List<int> indices,
        List<(int indexStart, int indexCount, string materialName)> meshInfos)
    {
        int vertexCount = positions.Count / 3;
        int posOffset = 0;
        int posLength = positions.Count * 4;
        int normOffset = posOffset + posLength;
        int normLength = normals.Count * 4;
        int texOffset = normOffset + normLength;
        int texLength = texCoords.Count * 4;
        int idxOffset = texOffset + texLength;
        int idxLength = indices.Count * 4;

        // Calculate bounds
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < positions.Count; i += 3)
        {
            minX = Math.Min(minX, positions[i]); maxX = Math.Max(maxX, positions[i]);
            minY = Math.Min(minY, positions[i + 1]); maxY = Math.Max(maxY, positions[i + 1]);
            minZ = Math.Min(minZ, positions[i + 2]); maxZ = Math.Max(maxZ, positions[i + 2]);
        }

        // Build materials list
        var uniqueMaterials = meshInfos.Select(m => SanitizeName(m.materialName)).Distinct().ToList();
        var materialMap = new Dictionary<string, int>();
        for (int i = 0; i < uniqueMaterials.Count; i++) materialMap[uniqueMaterials[i]] = i;

        // Build primitives
        var primitives = new List<object>();
        foreach (var (indexStart, indexCount, materialName) in meshInfos)
        {
            if (indexCount == 0) continue;
            // Each sub-mesh needs its own index accessor
            primitives.Add(new
            {
                attributes = new Dictionary<string, int>
                {
                    ["POSITION"] = 0,
                    ["NORMAL"] = 1,
                    ["TEXCOORD_0"] = 2
                },
                indices = 3, // We'll use a single index accessor for simplicity
                material = materialMap.GetValueOrDefault(SanitizeName(materialName), 0)
            });
        }

        // For simplicity, use a single primitive with all indices
        var gltf = new Dictionary<string, object>
        {
            ["asset"] = new { version = "2.0", generator = "SourceUnpack v1.8" },
            ["scene"] = 0,
            ["scenes"] = new[] { new { nodes = new[] { 0 } } },
            ["nodes"] = new[] { new { mesh = 0, name = SanitizeName(model.Name) } },
            ["meshes"] = new[] { new {
                name = SanitizeName(model.Name),
                primitives = new[] { new {
                    attributes = new Dictionary<string, int> {
                        ["POSITION"] = 0,
                        ["NORMAL"] = 1,
                        ["TEXCOORD_0"] = 2
                    },
                    indices = 3,
                    material = 0
                }}
            }},
            ["accessors"] = new object[]
            {
                new { bufferView = 0, componentType = 5126, count = vertexCount, type = "VEC3",
                      min = new[] { minX, minY, minZ }, max = new[] { maxX, maxY, maxZ } },
                new { bufferView = 1, componentType = 5126, count = vertexCount, type = "VEC3" },
                new { bufferView = 2, componentType = 5126, count = texCoords.Count / 2, type = "VEC2" },
                new { bufferView = 3, componentType = 5125, count = indices.Count, type = "SCALAR" }
            },
            ["bufferViews"] = new object[]
            {
                new { buffer = 0, byteOffset = posOffset, byteLength = posLength, target = 34962 },
                new { buffer = 0, byteOffset = normOffset, byteLength = normLength, target = 34962 },
                new { buffer = 0, byteOffset = texOffset, byteLength = texLength, target = 34962 },
                new { buffer = 0, byteOffset = idxOffset, byteLength = idxLength, target = 34963 }
            },
            ["materials"] = uniqueMaterials.Select(m => new Dictionary<string, object> {
                ["name"] = m,
                ["pbrMetallicRoughness"] = new { baseColorFactor = new[] { 0.8, 0.8, 0.8, 1.0 }, metallicFactor = 0.0, roughnessFactor = 0.7 }
            }).ToArray()
        };

        // Buffer with or without URI
        if (binUri != null)
        {
            gltf["buffers"] = new[] { new { uri = binUri, byteLength = bufferByteLength } };
        }
        else
        {
            gltf["buffers"] = new[] { new { byteLength = bufferByteLength } };
        }

        return JsonSerializer.Serialize(gltf, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "unnamed";
        string sanitized = Path.GetFileNameWithoutExtension(name);
        sanitized = sanitized.Replace(' ', '_').Replace('/', '_').Replace('\\', '_');
        return string.IsNullOrEmpty(sanitized) ? "unnamed" : sanitized;
    }
}
