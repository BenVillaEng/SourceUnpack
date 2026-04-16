using System.Text;

namespace SourceUnpack.Core.Formats.Gma;

/// <summary>
/// Represents a single file entry within a GMA archive.
/// </summary>
public class GmaEntry
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public uint Crc { get; set; }
    public long DataOffset { get; set; }
}

/// <summary>
/// Reads Garry's Mod Addon (GMA) archive files.
/// GMA format: Magic "GMAD" → version (u8) → SteamID (u64) → timestamp (u64) 
/// → required content (null-term) → addon name (null-term) → addon desc (null-term) 
/// → author (null-term) → addon version (i32) → file table → file data.
/// </summary>
public class GmaReader : IDisposable
{
    private readonly BinaryReader _reader;
    private readonly FileStream _stream;

    public bool IsValid { get; private set; }
    public byte FormatVersion { get; private set; }
    public string AddonName { get; private set; } = string.Empty;
    public string AddonDescription { get; private set; } = string.Empty;
    public string AddonAuthor { get; private set; } = string.Empty;
    public int AddonVersion { get; private set; }
    public List<GmaEntry> Entries { get; private set; } = new();

    public GmaReader(string filePath)
    {
        _stream = File.OpenRead(filePath);
        _reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
        Parse();
    }

    private void Parse()
    {
        try
        {
            // Magic: "GMAD" (4 bytes)
            byte[] magic = _reader.ReadBytes(4);
            if (Encoding.ASCII.GetString(magic) != "GMAD")
            {
                IsValid = false;
                return;
            }

            FormatVersion = _reader.ReadByte();      // Format version (usually 3)
            _reader.ReadUInt64();                      // SteamID (unused)
            _reader.ReadUInt64();                      // Timestamp

            // Required content (null-terminated string, may be empty)
            // In format v3, this is a loop of null-terminated strings ending with empty string
            if (FormatVersion > 1)
            {
                while (true)
                {
                    string req = ReadNullTermString();
                    if (string.IsNullOrEmpty(req)) break;
                }
            }

            AddonName = ReadNullTermString();
            AddonDescription = ReadNullTermString();
            AddonAuthor = ReadNullTermString();
            AddonVersion = _reader.ReadInt32();

            // File table
            // Each entry: file number (u32), path (null-term), size (i64), crc (u32)
            // Terminates when file number == 0
            while (true)
            {
                uint fileNumber = _reader.ReadUInt32();
                if (fileNumber == 0) break;

                var entry = new GmaEntry
                {
                    Path = ReadNullTermString().Replace('\\', '/'),
                    Size = _reader.ReadInt64(),
                    Crc = _reader.ReadUInt32()
                };

                Entries.Add(entry);
            }

            // Calculate data offsets — file data follows immediately after the table
            long dataStart = _stream.Position;
            foreach (var entry in Entries)
            {
                entry.DataOffset = dataStart;
                dataStart += entry.Size;
            }

            IsValid = true;
        }
        catch
        {
            IsValid = false;
        }
    }

    /// <summary>
    /// Extract the byte data of a specific GMA entry.
    /// </summary>
    public byte[]? ExtractEntry(GmaEntry entry)
    {
        try
        {
            _stream.Seek(entry.DataOffset, SeekOrigin.Begin);
            byte[] data = new byte[entry.Size];
            int totalRead = 0;
            while (totalRead < entry.Size)
            {
                int read = _stream.Read(data, totalRead, (int)(entry.Size - totalRead));
                if (read == 0) break;
                totalRead += read;
            }
            return totalRead == entry.Size ? data : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extract all files from the GMA to a dictionary (path → data).
    /// </summary>
    public Dictionary<string, byte[]> ExtractAll()
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            byte[]? data = ExtractEntry(entry);
            if (data != null)
                result[entry.Path] = data;
        }
        return result;
    }

    private string ReadNullTermString()
    {
        var sb = new StringBuilder();
        while (true)
        {
            byte b = _reader.ReadByte();
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}
