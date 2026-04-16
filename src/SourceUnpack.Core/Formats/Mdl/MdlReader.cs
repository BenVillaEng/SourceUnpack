using System.Text;

namespace SourceUnpack.Core.Formats.Mdl;

/// <summary>
/// Parsed vertex data from a VVD file.
/// </summary>
public struct MdlVertex
{
    public float PosX, PosY, PosZ;
    public float NormX, NormY, NormZ;
    public float TexU, TexV;
}

/// <summary>
/// A mesh within a model body part.
/// </summary>
public class MdlMesh
{
    public string MaterialName { get; set; } = string.Empty;
    public int MaterialIndex { get; set; }
    public List<MdlVertex> Vertices { get; set; } = new();
    public List<int> Indices { get; set; } = new();
}

/// <summary>
/// A body part containing one or more meshes.
/// </summary>
public class MdlBodyPart
{
    public string Name { get; set; } = string.Empty;
    public List<MdlMesh> Meshes { get; set; } = new();
}

/// <summary>
/// Complete parsed model data.
/// </summary>
public class MdlModelData
{
    public int Version { get; set; }
    public int Checksum { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string> MaterialNames { get; set; } = new();
    public List<string> MaterialPaths { get; set; } = new();
    public List<MdlBodyPart> BodyParts { get; set; } = new();
    public bool IsValid { get; set; }
}

/// <summary>
/// Reads Source Engine MDL/VVD/VTX model files and extracts mesh geometry.
/// Supports static props from MDL v44-v53 (HL2, GMod, CSS, TF2, CS:GO, Titanfall).
/// </summary>
public class MdlReader
{
    /// <summary>
    /// Load model data from MDL, VVD, and VTX file byte arrays.
    /// </summary>
    public MdlModelData Load(byte[] mdlData, byte[] vvdData, byte[] vtxData)
    {
        var model = new MdlModelData();

        try
        {
            if (!ParseMdlHeader(mdlData, model)) return model;
            var vertices = ParseVvd(vvdData, model.Version);
            if (vertices == null) return model;

            var indices = ParseVtx(vtxData);
            if (indices == null) return model;

            // Build meshes from parsed data
            BuildMeshes(mdlData, model, vertices, indices);
            model.IsValid = model.BodyParts.Count > 0;
        }
        catch
        {
            model.IsValid = false;
        }

        return model;
    }

    private bool ParseMdlHeader(byte[] data, MdlModelData model)
    {
        if (data.Length < 408) return false;

        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);

        // Magic: IDST
        int magic = br.ReadInt32();
        if (magic != 0x54534449) return false; // "IDST"

        model.Version = br.ReadInt32();
        // Support v44-v53 (HL2 through CS:GO/Titanfall)
        if (model.Version < 44 || model.Version > 53) return false;

        // Checksum — used to validate companion VTX/VVD files
        model.Checksum = br.ReadInt32();

        // Name (64 bytes)
        byte[] nameBytes = br.ReadBytes(64);
        int nullIdx = Array.IndexOf(nameBytes, (byte)0);
        if (nullIdx < 0) nullIdx = 64;
        model.Name = Encoding.ASCII.GetString(nameBytes, 0, nullIdx);

        // Data length
        br.ReadInt32();

        // Skip to texture info
        // eyeposition(12) + illumposition(12) + hull_min(12) + hull_max(12) +
        // view_bbmin(12) + view_bbmax(12) + flags(4) = 76
        ms.Seek(76, SeekOrigin.Current);

        // Bones
        int numBones = br.ReadInt32();
        int boneOffset = br.ReadInt32();

        // Bone controllers
        int numBoneControllers = br.ReadInt32();
        int boneControllerOffset = br.ReadInt32();

        // Hitbox sets
        int numHitboxSets = br.ReadInt32();
        int hitboxSetOffset = br.ReadInt32();

        // Local animations
        int numLocalAnim = br.ReadInt32();
        int localAnimOffset = br.ReadInt32();

        // Local sequences
        int numLocalSeq = br.ReadInt32();
        int localSeqOffset = br.ReadInt32();

        // Activitylistversion + eventsindexed
        br.ReadInt32();
        br.ReadInt32();

        // Textures
        int numTextures = br.ReadInt32();
        int textureOffset = br.ReadInt32();

        // Texture directories (cdtextures)
        int numCdTextures = br.ReadInt32();
        int cdTextureOffset = br.ReadInt32();

        // Read texture names
        if (textureOffset > 0 && numTextures > 0)
        {
            for (int i = 0; i < numTextures && i < 256; i++)
            {
                long entryPos = textureOffset + i * 64;
                if (entryPos + 4 > data.Length) break;

                ms.Seek(entryPos, SeekOrigin.Begin);
                int nameOfs = br.ReadInt32();

                long namePos = entryPos + nameOfs;
                if (namePos > 0 && namePos < data.Length)
                {
                    ms.Seek(namePos, SeekOrigin.Begin);
                    string texName = ReadNullTermString(br, data.Length - (int)namePos);
                    model.MaterialNames.Add(texName);
                }
            }
        }

        // Read texture paths
        if (cdTextureOffset > 0 && numCdTextures > 0)
        {
            ms.Seek(cdTextureOffset, SeekOrigin.Begin);
            for (int i = 0; i < numCdTextures && i < 64; i++)
            {
                if (ms.Position + 4 > data.Length) break;
                int pathOfs = br.ReadInt32();
                long savedPos = ms.Position;

                long pathPos = pathOfs;
                if (pathPos > 0 && pathPos < data.Length)
                {
                    ms.Seek(pathPos, SeekOrigin.Begin);
                    string texPath = ReadNullTermString(br, data.Length - (int)pathPos);
                    model.MaterialPaths.Add(texPath.Replace('\\', '/'));
                }
                ms.Seek(savedPos, SeekOrigin.Begin);
            }
        }

        return true;
    }

    private MdlVertex[]? ParseVvd(byte[] data, int mdlVersion)
    {
        if (data.Length < 64) return null;

        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);

        // Magic: IDSV
        int magic = br.ReadInt32();
        if (magic != 0x56534449) return null; // "IDSV"

        int version = br.ReadInt32();
        br.ReadInt32(); // checksum

        int numLods = br.ReadInt32();
        int[] numLodVertices = new int[8];
        for (int i = 0; i < 8; i++)
            numLodVertices[i] = br.ReadInt32();

        int numFixups = br.ReadInt32();
        int fixupTableOffset = br.ReadInt32();
        int vertexDataOffset = br.ReadInt32();
        int tangentDataOffset = br.ReadInt32();

        // Read LOD 0 vertices (highest quality)
        int vertexCount = numLodVertices[0];
        if (vertexCount <= 0 || vertexCount > 1_000_000) return null;

        var vertices = new MdlVertex[vertexCount];

        if (numFixups == 0)
        {
            // Simple case: vertices are sequential
            ms.Seek(vertexDataOffset, SeekOrigin.Begin);
            for (int i = 0; i < vertexCount; i++)
            {
                vertices[i] = ReadVertex(br);
            }
        }
        else
        {
            // Fixup table: need to remap vertices for LOD 0
            ms.Seek(fixupTableOffset, SeekOrigin.Begin);
            var fixups = new List<(int lod, int sourceVertexId, int numVertices)>();
            for (int i = 0; i < numFixups; i++)
            {
                fixups.Add((br.ReadInt32(), br.ReadInt32(), br.ReadInt32()));
            }

            int destIdx = 0;
            foreach (var (lod, sourceVertexId, numVerts) in fixups)
            {
                if (lod != 0) continue;

                long vertPos = vertexDataOffset + (long)sourceVertexId * 48;
                ms.Seek(vertPos, SeekOrigin.Begin);
                for (int i = 0; i < numVerts && destIdx < vertexCount; i++)
                {
                    vertices[destIdx++] = ReadVertex(br);
                }
            }
        }

        return vertices;
    }

    private MdlVertex ReadVertex(BinaryReader br)
    {
        // VVD vertex format: boneWeight(32) + position(12) + normal(12) + uv(8) = 64 bytes
        // Bone weights: numBones(1byte) + pad(3bytes) + weights(12) + bones(12) = 28??? no...
        // Actually: float[3] weights, byte[3] bones, byte numBones = 16 bytes total for weight struct
        // Wait, the actual VertexFileHeader_t has boneweights as:
        // float weight[3](12) + char bone[3](3) + byte numbones(1) = 16 bytes

        // Read bone weight data (skip it for static models)
        br.ReadBytes(16); // bone weights

        var v = new MdlVertex();
        v.PosX = br.ReadSingle();
        v.PosY = br.ReadSingle();
        v.PosZ = br.ReadSingle();
        v.NormX = br.ReadSingle();
        v.NormY = br.ReadSingle();
        v.NormZ = br.ReadSingle();
        v.TexU = br.ReadSingle();
        v.TexV = br.ReadSingle();

        // tangent (16 bytes) — in tangent data section, not here
        // Actually VVD vertex is 48 bytes: boneweights(16) + pos(12) + norm(12) + uv(8) = 48
        return v;
    }

    private List<List<int>>? ParseVtx(byte[] data)
    {
        if (data.Length < 36) return null;

        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);

        // VTX header
        int vtxVersion = br.ReadInt32();
        int vertCacheSize = br.ReadInt32();
        ushort maxBonesPerStrip = br.ReadUInt16();
        ushort maxBonesPerTriangle = br.ReadUInt16();
        int maxBonesPerVertex = br.ReadInt32();
        br.ReadInt32(); // checksum
        int numLods = br.ReadInt32();
        int materialReplacementListOffset = br.ReadInt32();
        int numBodyParts = br.ReadInt32();
        int bodyPartOffset = br.ReadInt32();

        var allIndices = new List<List<int>>();

        // Parse body parts
        for (int bp = 0; bp < numBodyParts; bp++)
        {
            long bpPos = bodyPartOffset + bp * 8;
            if (bpPos + 8 > data.Length) break;

            ms.Seek(bpPos, SeekOrigin.Begin);
            int numModels = br.ReadInt32();
            int modelOffset = br.ReadInt32();

            for (int m = 0; m < numModels; m++)
            {
                long modelPos = bpPos + modelOffset + m * 8;
                if (modelPos + 8 > data.Length) break;

                ms.Seek(modelPos, SeekOrigin.Begin);
                int numModelLods = br.ReadInt32();
                int lodOffset = br.ReadInt32();

                // Only use LOD 0
                long lodPos = modelPos + lodOffset;
                if (lodPos + 12 > data.Length) continue;

                ms.Seek(lodPos, SeekOrigin.Begin);
                int numMeshes = br.ReadInt32();
                int meshOffset = br.ReadInt32();
                float switchPoint = br.ReadSingle();

                for (int mesh = 0; mesh < numMeshes; mesh++)
                {
                    long meshPos = lodPos + meshOffset + mesh * 9;
                    if (meshPos + 9 > data.Length) break;

                    ms.Seek(meshPos, SeekOrigin.Begin);
                    int numStripGroups = br.ReadInt32();
                    int stripGroupOffset = br.ReadInt32();
                    byte meshFlags = br.ReadByte();

                    var meshIndices = new List<int>();

                    for (int sg = 0; sg < numStripGroups; sg++)
                    {
                        long sgPos = meshPos + stripGroupOffset + sg * 25;
                        if (sgPos + 25 > data.Length) break;

                        ms.Seek(sgPos, SeekOrigin.Begin);
                        int sgNumVerts = br.ReadInt32();
                        int sgVertOffset = br.ReadInt32();
                        int sgNumIndices = br.ReadInt32();
                        int sgIndexOffset = br.ReadInt32();
                        int sgNumStrips = br.ReadInt32();
                        int sgStripOffset = br.ReadInt32();
                        byte sgFlags = br.ReadByte();

                        // Read strip group vertices to get original vertex indices
                        var sgVertices = new int[sgNumVerts];
                        long vertDataPos = sgPos + sgVertOffset;
                        for (int v = 0; v < sgNumVerts; v++)
                        {
                            long vPos = vertDataPos + v * 9;
                            if (vPos + 9 > data.Length) break;

                            ms.Seek(vPos, SeekOrigin.Begin);
                            br.ReadByte();    // boneWeightIndex[0]
                            br.ReadByte();    // boneWeightIndex[1]
                            br.ReadByte();    // boneWeightIndex[2]
                            br.ReadByte();    // numBones
                            ushort origMeshVertId = br.ReadUInt16();
                            br.ReadBytes(3);  // bone IDs
                            sgVertices[v] = origMeshVertId;
                        }

                        // Read indices
                        long indexDataPos = sgPos + sgIndexOffset;
                        for (int idx = 0; idx < sgNumIndices; idx++)
                        {
                            long iPos = indexDataPos + idx * 2;
                            if (iPos + 2 > data.Length) break;

                            ms.Seek(iPos, SeekOrigin.Begin);
                            ushort sgIdx = br.ReadUInt16();
                            if (sgIdx < sgNumVerts)
                                meshIndices.Add(sgVertices[sgIdx]);
                        }
                    }

                    allIndices.Add(meshIndices);
                }
            }
        }

        return allIndices;
    }

    private void BuildMeshes(byte[] mdlData, MdlModelData model, MdlVertex[] vertices, List<List<int>> indexSets)
    {
        using var ms = new MemoryStream(mdlData);
        using var br = new BinaryReader(ms);

        // Skip to body parts in MDL
        // After the texture/cdtexture sections: skinref, bodyparts
        // We'll do a simpler approach — create one body part with meshes from VTX index sets
        var bodyPart = new MdlBodyPart { Name = model.Name };

        for (int i = 0; i < indexSets.Count; i++)
        {
            var meshIndices = indexSets[i];
            if (meshIndices.Count == 0) continue;

            var mesh = new MdlMesh
            {
                MaterialIndex = i < model.MaterialNames.Count ? i : 0,
                MaterialName = i < model.MaterialNames.Count ? model.MaterialNames[i] : $"material_{i}"
            };

            // Collect unique vertices referenced by this mesh
            var vertexMap = new Dictionary<int, int>();
            foreach (int origIdx in meshIndices)
            {
                if (!vertexMap.ContainsKey(origIdx) && origIdx >= 0 && origIdx < vertices.Length)
                {
                    vertexMap[origIdx] = mesh.Vertices.Count;
                    mesh.Vertices.Add(vertices[origIdx]);
                }
            }

            // Build remapped indices
            foreach (int origIdx in meshIndices)
            {
                if (vertexMap.TryGetValue(origIdx, out int newIdx))
                    mesh.Indices.Add(newIdx);
            }

            if (mesh.Vertices.Count > 0 && mesh.Indices.Count > 0)
                bodyPart.Meshes.Add(mesh);
        }

        if (bodyPart.Meshes.Count > 0)
            model.BodyParts.Add(bodyPart);
    }

    private static string ReadNullTermString(BinaryReader br, int maxLen)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < maxLen; i++)
        {
            byte b = br.ReadByte();
            if (b == 0) break;
            bytes.Add(b);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
}
