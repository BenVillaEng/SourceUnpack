using System.IO.Compression;
using System.Text;

namespace SourceUnpack.Core.Formats.Bsp;

/// <summary>
/// BSP lump types used in Source Engine maps.
/// </summary>
public enum LumpType
{
    Entities = 0,
    Planes = 1,
    Texdata = 2,
    Vertices = 3,
    Texinfo = 6,
    Models = 14,
    Brushes = 18,
    Brushsides = 19,
    GameLump = 35,
    Pakfile = 40,
    TexdataStringData = 43,
    TexdataStringTable = 44,
}

/// <summary>
/// Represents a BSP lump directory entry.
/// </summary>
public struct BspLump
{
    public int Offset;
    public int Length;
    public int Version;
    public int FourCC;
}

/// <summary>
/// Parsed BSP header information.
/// </summary>
public class BspHeader
{
    public string Magic { get; set; } = string.Empty;
    public int Version { get; set; }
    public BspLump[] Lumps { get; set; } = new BspLump[64];
    public int MapRevision { get; set; }
}

// ─── Structs for VMF decompilation (used by BspToVmfConverter) ───

/// <summary>
/// BSP plane: defines a half-space via normal + distance.
/// </summary>
public struct BspPlane
{
    public float NormalX, NormalY, NormalZ;
    public float Distance;
    public int Type;
}

/// <summary>
/// BSP texinfo: texture mapping vectors and texdata reference.
/// </summary>
public struct BspTexinfo
{
    public float[] TextureVecs;  // [8] = s0,s1,s2,sOffset, t0,t1,t2,tOffset
    public float[] LightmapVecs; // [8]
    public int Flags;
    public int TexdataIndex;
}

/// <summary>
/// BSP texdata: references a texture name via the string table.
/// </summary>
public struct BspTexdata
{
    public float ReflectivityR, ReflectivityG, ReflectivityB;
    public int NameStringTableID;
    public int Width, Height;
    public int ViewWidth, ViewHeight;
}

/// <summary>
/// BSP brush: a convex solid defined by a range of brushsides.
/// </summary>
public struct BspBrush
{
    public int FirstSide;
    public int NumSides;
    public int Contents;
}

/// <summary>
/// BSP brushside: one face of a brush, referencing a plane and texinfo.
/// </summary>
public struct BspBrushSide
{
    public ushort PlaneNum;
    public short TexinfoIndex;
    public short DispInfo;
    public short Bevel;
}

/// <summary>
/// BSP model: bounding box and brush/face ranges. Model 0 = worldspawn.
/// </summary>
public struct BspModel
{
    public float MinX, MinY, MinZ;
    public float MaxX, MaxY, MaxZ;
    public float OriginX, OriginY, OriginZ;
    public int HeadNode;
    public int FirstFace, NumFaces;
}

/// <summary>
/// Reads and parses Source Engine BSP (Binary Space Partition) files.
/// Supports VBSP versions 19-21 (HL2, GMod, CSS, TF2, etc.)
/// </summary>
public class BspReader : IDisposable
{
    private readonly BinaryReader _reader;
    private readonly Stream _stream;
    public BspHeader Header { get; private set; } = new();
    public bool IsValid { get; private set; }

    public BspReader(string filePath)
    {
        _stream = File.OpenRead(filePath);
        _reader = new BinaryReader(_stream);
        ReadHeader();
    }

    public BspReader(Stream stream)
    {
        _stream = stream;
        _reader = new BinaryReader(_stream);
        ReadHeader();
    }

    private void ReadHeader()
    {
        try
        {
            byte[] magic = _reader.ReadBytes(4);
            Header.Magic = Encoding.ASCII.GetString(magic);
            Header.Version = _reader.ReadInt32();

            IsValid = Header.Magic == "VBSP" && Header.Version >= 17 && Header.Version <= 29;
            if (!IsValid) return;

            for (int i = 0; i < 64; i++)
            {
                Header.Lumps[i] = new BspLump
                {
                    Offset = _reader.ReadInt32(),
                    Length = _reader.ReadInt32(),
                    Version = _reader.ReadInt32(),
                    FourCC = _reader.ReadInt32()
                };
            }

            Header.MapRevision = _reader.ReadInt32();
        }
        catch
        {
            IsValid = false;
        }
    }

    /// <summary>
    /// Read raw lump data by lump index.
    /// </summary>
    public byte[] ReadLump(int lumpIndex)
    {
        if (lumpIndex < 0 || lumpIndex >= 64)
            throw new ArgumentOutOfRangeException(nameof(lumpIndex));

        var lump = Header.Lumps[lumpIndex];
        if (lump.Length == 0) return Array.Empty<byte>();

        _stream.Seek(lump.Offset, SeekOrigin.Begin);
        return _reader.ReadBytes(lump.Length);
    }

    public byte[] ReadLump(LumpType type) => ReadLump((int)type);

    // ─── Entity / Texture / Prop parsing (existing logic, unchanged) ───

    /// <summary>
    /// Parse the entity lump (lump 0) to extract key-value pairs.
    /// </summary>
    public List<Dictionary<string, string>> ParseEntities()
    {
        var data = ReadLump(LumpType.Entities);
        if (data.Length == 0) return new();

        var text = Encoding.ASCII.GetString(data).TrimEnd('\0');
        var entities = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;

        using var sr = new StringReader(text);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            line = line.Trim();
            if (line == "{")
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            else if (line == "}" && current != null)
            {
                entities.Add(current);
                current = null;
            }
            else if (current != null && line.StartsWith("\""))
            {
                var parts = ParseKeyValue(line);
                if (parts != null)
                    current[parts.Value.Key] = parts.Value.Value;
            }
        }
        return entities;
    }

    private static KeyValuePair<string, string>? ParseKeyValue(string line)
    {
        // Format: "key" "value"
        int firstQuote = line.IndexOf('"');
        int secondQuote = line.IndexOf('"', firstQuote + 1);
        int thirdQuote = line.IndexOf('"', secondQuote + 1);
        int fourthQuote = line.IndexOf('"', thirdQuote + 1);

        if (firstQuote < 0 || secondQuote < 0 || thirdQuote < 0 || fourthQuote < 0)
            return null;

        string key = line.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
        string value = line.Substring(thirdQuote + 1, fourthQuote - thirdQuote - 1);
        return new KeyValuePair<string, string>(key, value);
    }

    /// <summary>
    /// Get skybox name from the worldspawn entity.
    /// </summary>
    public string? GetSkyboxName()
    {
        var entities = ParseEntities();
        var worldspawn = entities.FirstOrDefault(e =>
            e.TryGetValue("classname", out var cls) && cls == "worldspawn");
        
        if (worldspawn != null && worldspawn.TryGetValue("skyname", out var skyname))
        {
            return skyname;
        }
        
        return null;
    }

    /// <summary>
    /// Get all texture data string references from the BSP.
    /// </summary>
    public List<string> GetTextureNames()
    {
        var stringData = ReadLump(LumpType.TexdataStringData);
        var tableData = ReadLump(LumpType.TexdataStringTable);

        if (stringData.Length == 0 || tableData.Length == 0)
            return new();

        var names = new List<string>();
        using var tableReader = new BinaryReader(new MemoryStream(tableData));

        int count = tableData.Length / 4;
        for (int i = 0; i < count; i++)
        {
            int offset = tableReader.ReadInt32();
            if (offset >= 0 && offset < stringData.Length)
            {
                int end = Array.IndexOf(stringData, (byte)0, offset);
                if (end < 0) end = stringData.Length;
                string name = Encoding.ASCII.GetString(stringData, offset, end - offset);
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
        }

        return names.Distinct().ToList();
    }

    /// <summary>
    /// Get static prop model names from the game lump.
    /// </summary>
    public List<string> GetStaticPropNames()
    {
        var data = ReadLump(LumpType.GameLump);
        if (data.Length < 4) return new();

        var models = new List<string>();
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);

        try
        {
            int lumpCount = br.ReadInt32();
            for (int i = 0; i < lumpCount; i++)
            {
                int id = br.ReadInt32();
                ushort flags = br.ReadUInt16();
                ushort version = br.ReadUInt16();
                int fileofs = br.ReadInt32();
                int filelen = br.ReadInt32();

                // Static props lump id = 'sprp' = 0x73707270
                if (id == 0x73707270)
                {
                    long savedPos = ms.Position;
                    ms.Seek(fileofs - Header.Lumps[(int)LumpType.GameLump].Offset, SeekOrigin.Begin);

                    if (ms.Position >= 0 && ms.Position < ms.Length - 4)
                    {
                        int dictCount = br.ReadInt32();
                        for (int j = 0; j < dictCount && ms.Position + 128 <= ms.Length; j++)
                        {
                            byte[] nameBytes = br.ReadBytes(128);
                            int nullIdx = Array.IndexOf(nameBytes, (byte)0);
                            if (nullIdx < 0) nullIdx = 128;
                            string name = Encoding.ASCII.GetString(nameBytes, 0, nullIdx);
                            if (!string.IsNullOrWhiteSpace(name))
                                models.Add(name);
                        }
                    }

                    ms.Position = savedPos;
                }
            }
        }
        catch { /* Game lump parsing can be fragile across versions */ }

        return models.Distinct().ToList();
    }

    /// <summary>
    /// Extract the embedded pakfile (ZIP) from the BSP and return its entries.
    /// </summary>
    public Dictionary<string, byte[]> ExtractPakfile()
    {
        var pakData = ReadLump(LumpType.Pakfile);
        if (pakData.Length == 0) return new();

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var ms = new MemoryStream(pakData);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // skip directories

                using var entryStream = entry.Open();
                using var buffer = new MemoryStream();
                entryStream.CopyTo(buffer);
                files[entry.FullName.Replace('\\', '/')] = buffer.ToArray();
            }
        }
        catch { /* Pakfile may be empty or corrupt */ }

        return files;
    }

    // ─── Brush geometry parsing (used by BspToVmfConverter) ───

    /// <summary>
    /// Read all planes from lump 1. Each plane is 20 bytes.
    /// </summary>
    public BspPlane[] ReadPlanes()
    {
        var data = ReadLump(LumpType.Planes);
        int count = data.Length / 20;
        var planes = new BspPlane[count];
        using var br = new BinaryReader(new MemoryStream(data));
        for (int i = 0; i < count; i++)
        {
            planes[i] = new BspPlane
            {
                NormalX = br.ReadSingle(),
                NormalY = br.ReadSingle(),
                NormalZ = br.ReadSingle(),
                Distance = br.ReadSingle(),
                Type = br.ReadInt32()
            };
        }
        return planes;
    }

    /// <summary>
    /// Read all texinfo entries from lump 6. Each is 72 bytes.
    /// </summary>
    public BspTexinfo[] ReadTexinfo()
    {
        var data = ReadLump(LumpType.Texinfo);
        int count = data.Length / 72;
        var texinfos = new BspTexinfo[count];
        using var br = new BinaryReader(new MemoryStream(data));
        for (int i = 0; i < count; i++)
        {
            var ti = new BspTexinfo
            {
                TextureVecs = new float[8],
                LightmapVecs = new float[8]
            };
            for (int j = 0; j < 8; j++) ti.TextureVecs[j] = br.ReadSingle();
            for (int j = 0; j < 8; j++) ti.LightmapVecs[j] = br.ReadSingle();
            ti.Flags = br.ReadInt32();
            ti.TexdataIndex = br.ReadInt32();
            texinfos[i] = ti;
        }
        return texinfos;
    }

    /// <summary>
    /// Read all texdata entries from lump 2. Each is 32 bytes.
    /// </summary>
    public BspTexdata[] ReadTexdata()
    {
        var data = ReadLump(LumpType.Texdata);
        int count = data.Length / 32;
        var texdatas = new BspTexdata[count];
        using var br = new BinaryReader(new MemoryStream(data));
        for (int i = 0; i < count; i++)
        {
            texdatas[i] = new BspTexdata
            {
                ReflectivityR = br.ReadSingle(),
                ReflectivityG = br.ReadSingle(),
                ReflectivityB = br.ReadSingle(),
                NameStringTableID = br.ReadInt32(),
                Width = br.ReadInt32(),
                Height = br.ReadInt32(),
                ViewWidth = br.ReadInt32(),
                ViewHeight = br.ReadInt32()
            };
        }
        return texdatas;
    }

    /// <summary>
    /// Read all brushes from lump 18. Each is 12 bytes.
    /// </summary>
    public BspBrush[] ReadBrushes()
    {
        var data = ReadLump(LumpType.Brushes);
        int count = data.Length / 12;
        var brushes = new BspBrush[count];
        using var br = new BinaryReader(new MemoryStream(data));
        for (int i = 0; i < count; i++)
        {
            brushes[i] = new BspBrush
            {
                FirstSide = br.ReadInt32(),
                NumSides = br.ReadInt32(),
                Contents = br.ReadInt32()
            };
        }
        return brushes;
    }

    /// <summary>
    /// Read all brushsides from lump 19. Each is 8 bytes.
    /// </summary>
    public BspBrushSide[] ReadBrushSides()
    {
        var data = ReadLump(LumpType.Brushsides);
        int count = data.Length / 8;
        var sides = new BspBrushSide[count];
        using var br = new BinaryReader(new MemoryStream(data));
        for (int i = 0; i < count; i++)
        {
            sides[i] = new BspBrushSide
            {
                PlaneNum = br.ReadUInt16(),
                TexinfoIndex = br.ReadInt16(),
                DispInfo = br.ReadInt16(),
                Bevel = br.ReadInt16()
            };
        }
        return sides;
    }

    /// <summary>
    /// Read all models from lump 14. Each is 48 bytes.
    /// </summary>
    public BspModel[] ReadModels()
    {
        var data = ReadLump(LumpType.Models);
        int count = data.Length / 48;
        var models = new BspModel[count];
        using var br = new BinaryReader(new MemoryStream(data));
        for (int i = 0; i < count; i++)
        {
            models[i] = new BspModel
            {
                MinX = br.ReadSingle(), MinY = br.ReadSingle(), MinZ = br.ReadSingle(),
                MaxX = br.ReadSingle(), MaxY = br.ReadSingle(), MaxZ = br.ReadSingle(),
                OriginX = br.ReadSingle(), OriginY = br.ReadSingle(), OriginZ = br.ReadSingle(),
                HeadNode = br.ReadInt32(),
                FirstFace = br.ReadInt32(),
                NumFaces = br.ReadInt32()
            };
        }
        return models;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}
