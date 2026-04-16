using System.Text;

namespace SourceUnpack.Core.Formats.Vpk;

/// <summary>
/// Entry in a VPK directory tree.
/// </summary>
public class VpkEntry
{
    public string FullPath { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public uint CRC { get; set; }
    public ushort ArchiveIndex { get; set; }
    public uint EntryOffset { get; set; }
    public uint EntryLength { get; set; }
    public byte[] PreloadData { get; set; } = Array.Empty<byte>();

    /// <summary>0x7FFF means data is in the directory file itself.</summary>
    public bool IsInDirectoryFile => ArchiveIndex == 0x7FFF;
}

/// <summary>
/// Reads Valve VPK (Valve Pak) v1/v2 archives.
/// Used to locate and extract game assets from Source Engine installations.
/// </summary>
public class VpkReader : IDisposable
{
    private readonly string _dirFilePath;
    private readonly BinaryReader _reader;
    private readonly FileStream _stream;
    
    // Changing from List to Dictionary for O(1) lookup
    private readonly Dictionary<string, VpkEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public int Version { get; private set; }
    public uint TreeSize { get; private set; }
    
    // Provide a way to iterate values if needed
    public IReadOnlyCollection<VpkEntry> Entries => _entries.Values;

    public string FilePath => _dirFilePath;

    // V2 fields
    public uint FileDataSectionSize { get; private set; }
    public uint ArchiveMD5SectionSize { get; private set; }
    public uint OtherMD5SectionSize { get; private set; }
    public uint SignatureSectionSize { get; private set; }

    private long _treeStart;

    public VpkReader(string dirFilePath)
    {
        _dirFilePath = dirFilePath;
        _stream = File.OpenRead(dirFilePath);
        _reader = new BinaryReader(_stream);
        ReadHeader();
        ReadTree();
    }

    private void ReadHeader()
    {
        uint signature = _reader.ReadUInt32();
        if (signature != 0x55AA1234)
            throw new InvalidDataException("Not a valid VPK file.");

        Version = (int)_reader.ReadUInt32();
        TreeSize = _reader.ReadUInt32();

        if (Version == 2)
        {
            FileDataSectionSize = _reader.ReadUInt32();
            ArchiveMD5SectionSize = _reader.ReadUInt32();
            OtherMD5SectionSize = _reader.ReadUInt32();
            SignatureSectionSize = _reader.ReadUInt32();
        }

        _treeStart = _stream.Position;
    }

    private void ReadTree()
    {
        while (true)
        {
            string extension = ReadNullTermString();
            if (string.IsNullOrEmpty(extension)) break;

            while (true)
            {
                string directory = ReadNullTermString();
                if (string.IsNullOrEmpty(directory)) break;

                while (true)
                {
                    string filename = ReadNullTermString();
                    if (string.IsNullOrEmpty(filename)) break;

                    var entry = new VpkEntry
                    {
                        Extension = extension,
                        Directory = directory == " " ? "" : directory,
                        FileName = filename,
                        CRC = _reader.ReadUInt32(),
                    };

                    ushort preloadBytes = _reader.ReadUInt16();
                    entry.ArchiveIndex = _reader.ReadUInt16();
                    entry.EntryOffset = _reader.ReadUInt32();
                    entry.EntryLength = _reader.ReadUInt32();

                    ushort terminator = _reader.ReadUInt16();
                    // terminator should be 0xFFFF

                    if (preloadBytes > 0)
                        entry.PreloadData = _reader.ReadBytes(preloadBytes);

                    string dir = entry.Directory.Length > 0 ? entry.Directory + "/" : "";
                    
                    // Normalize separators
                    entry.FullPath = $"{dir}{entry.FileName}.{entry.Extension}".Replace('\\', '/');

                    // Add to dictionary. Handle duplicates gracefully (last one wins or first one wins?)
                    // VPK structure implies iterating tree gives entries. Source engine behavior?
                    // Typically overwritten if duplicate.
                    _entries[entry.FullPath] = entry;
                }
            }
        }
    }

    private string ReadNullTermString()
    {
        var bytes = new List<byte>();
        byte b;
        while ((b = _reader.ReadByte()) != 0)
            bytes.Add(b);
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    /// <summary>
    /// Find an entry by its full path (case-insensitive).
    /// </summary>
    public VpkEntry? FindEntry(string path)
    {
        path = path.Replace('\\', '/').TrimStart('/');
        
        if (_entries.TryGetValue(path, out var entry))
            return entry;
            
        return null;
    }

    /// <summary>
    /// Find all entries matching a pattern (e.g. "materials/skybox/*.vtf").
    /// </summary>
    public List<VpkEntry> FindEntries(string directory, string? extension = null)
    {
        directory = directory.Replace('\\', '/').TrimEnd('/');
        
        // This is still O(N), but less frequent than single file lookups
        return _entries.Values.Where(e =>
        {
            bool dirMatch = e.Directory.Replace('\\', '/').Equals(directory, StringComparison.OrdinalIgnoreCase);
            if (extension != null)
                dirMatch &= string.Equals(e.Extension, extension.TrimStart('.'), StringComparison.OrdinalIgnoreCase);
            return dirMatch;
        }).ToList();
    }

    /// <summary>
    /// Extract the data for a given VPK entry.
    /// </summary>
    public byte[] ExtractEntry(VpkEntry entry)
    {
        byte[] data;

        if (entry.IsInDirectoryFile)
        {
            // Data is after the tree in the directory file
            long dataStart = _treeStart + TreeSize;
            lock (_stream) // Check thread safety if parallel extraction
            {
                _stream.Seek(dataStart + entry.EntryOffset, SeekOrigin.Begin);
                data = _reader.ReadBytes((int)entry.EntryLength);
            }
        }
        else
        {
            // Data is in an archive file (_000.vpk, _001.vpk, etc.)
            string archivePath = GetArchivePath(entry.ArchiveIndex);
            
            // Check if archive path exists
            if (!File.Exists(archivePath)) return Array.Empty<byte>();

            using var archiveStream = File.OpenRead(archivePath);
            archiveStream.Seek(entry.EntryOffset, SeekOrigin.Begin);
            data = new byte[entry.EntryLength];
            archiveStream.ReadExactly(data, 0, data.Length);
        }

        // Combine preload data + archive data
        if (entry.PreloadData.Length > 0)
        {
            var combined = new byte[entry.PreloadData.Length + data.Length];
            Buffer.BlockCopy(entry.PreloadData, 0, combined, 0, entry.PreloadData.Length);
            Buffer.BlockCopy(data, 0, combined, entry.PreloadData.Length, data.Length);
            return combined;
        }

        return data;
    }

    private string GetArchivePath(ushort archiveIndex)
    {
        // _dir.vpk → _000.vpk, _001.vpk, etc.
        string basePath = _dirFilePath;
        if (basePath.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase))
            basePath = basePath[..^8];
        else
            basePath = Path.Combine(
                Path.GetDirectoryName(basePath) ?? "",
                Path.GetFileNameWithoutExtension(basePath));

        return $"{basePath}_{archiveIndex:D3}.vpk";
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}
