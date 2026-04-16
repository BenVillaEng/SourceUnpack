namespace SourceUnpack.Core.Formats.Vtf;

/// <summary>
/// Image formats used in VTF files.
/// </summary>
public enum VtfImageFormat
{
    None = -1,
    RGBA8888 = 0,
    ABGR8888 = 1,
    RGB888 = 2,
    BGR888 = 3,
    RGB565 = 4,
    I8 = 5,
    IA88 = 6,
    P8 = 7,
    A8 = 8,
    RGB888_BlueScreen = 9,
    BGR888_BlueScreen = 10,
    ARGB8888 = 11,
    BGRA8888 = 12,
    DXT1 = 13,
    DXT3 = 14,
    DXT5 = 15,
    BGRX8888 = 16,
    BGR565 = 17,
    BGRX5551 = 18,
    BGRA4444 = 19,
    DXT1_OneBitAlpha = 20,
    BGRA5551 = 21,
    UV88 = 22,
    UVWQ8888 = 23,
    RGBA16161616F = 24,
    RGBA16161616 = 25,
    UVLX8888 = 26,
}

/// <summary>
/// VTF file header information.
/// </summary>
public class VtfHeader
{
    public string Signature { get; set; } = string.Empty;
    public uint VersionMajor { get; set; }
    public uint VersionMinor { get; set; }
    public uint HeaderSize { get; set; }
    public ushort Width { get; set; }
    public ushort Height { get; set; }
    public uint Flags { get; set; }
    public ushort Frames { get; set; }
    public ushort FirstFrame { get; set; }
    public VtfImageFormat HighResFormat { get; set; }
    public byte MipmapCount { get; set; }
    public VtfImageFormat LowResFormat { get; set; }
    public byte LowResWidth { get; set; }
    public byte LowResHeight { get; set; }
    public ushort Depth { get; set; } = 1;
}

/// <summary>
/// Reads Valve Texture Format (VTF) files and decodes image data.
/// </summary>
public class VtfReader
{
    public VtfHeader Header { get; private set; } = new();
    public byte[] PixelData { get; private set; } = Array.Empty<byte>(); // RGBA8888

    /// <summary>
    /// Parse a VTF file and decode the highest-resolution image to RGBA8888.
    /// </summary>
    public bool Load(byte[] data)
    {
        if (data.Length < 64) return false;

        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);

        try
        {
            // Signature
            byte[] sig = br.ReadBytes(4);
            Header.Signature = System.Text.Encoding.ASCII.GetString(sig);
            if (Header.Signature != "VTF\0") return false;

            Header.VersionMajor = br.ReadUInt32();
            Header.VersionMinor = br.ReadUInt32();
            Header.HeaderSize = br.ReadUInt32();
            Header.Width = br.ReadUInt16();
            Header.Height = br.ReadUInt16();
            Header.Flags = br.ReadUInt32();
            Header.Frames = br.ReadUInt16();
            Header.FirstFrame = br.ReadUInt16();

            br.ReadBytes(4); // padding
            float[] reflectivity = { br.ReadSingle(), br.ReadSingle(), br.ReadSingle() };
            br.ReadBytes(4); // padding

            br.ReadSingle(); // bumpmap scale
            Header.HighResFormat = (VtfImageFormat)br.ReadInt32();
            Header.MipmapCount = br.ReadByte();
            Header.LowResFormat = (VtfImageFormat)br.ReadInt32();
            Header.LowResWidth = br.ReadByte();
            Header.LowResHeight = br.ReadByte();

            if (Header.VersionMajor >= 7 && Header.VersionMinor >= 2)
            {
                Header.Depth = br.ReadUInt16();
                if (Header.Depth == 0) Header.Depth = 1;
            }

            // Seek to high-res image data
            // Calculate offset: after header + low-res image + all mipmaps except the largest
            ms.Seek(Header.HeaderSize, SeekOrigin.Begin);

            // Skip low-res thumbnail
            int lowResSize = ComputeImageSize(Header.LowResWidth, Header.LowResHeight, Header.LowResFormat);
            ms.Seek(lowResSize, SeekOrigin.Current);

            // Skip smaller mipmaps (stored smallest first)
            for (int mip = Header.MipmapCount - 1; mip > 0; mip--)
            {
                int mipW = Math.Max(1, Header.Width >> mip);
                int mipH = Math.Max(1, Header.Height >> mip);
                int mipSize = ComputeImageSize(mipW, mipH, Header.HighResFormat);
                ms.Seek(mipSize * Header.Frames, SeekOrigin.Current);
            }

            // Read highest resolution mipmap (first frame only)
            int highResSize = ComputeImageSize(Header.Width, Header.Height, Header.HighResFormat);
            byte[] imageData = br.ReadBytes(highResSize);

            // Decode to RGBA8888
            PixelData = DecodeImage(imageData, Header.Width, Header.Height, Header.HighResFormat);
            return PixelData.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Calculate size in bytes for a given image format and dimensions.
    /// </summary>
    public static int ComputeImageSize(int width, int height, VtfImageFormat format)
    {
        return format switch
        {
            VtfImageFormat.DXT1 or VtfImageFormat.DXT1_OneBitAlpha =>
                Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8,
            VtfImageFormat.DXT3 or VtfImageFormat.DXT5 =>
                Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16,
            VtfImageFormat.RGBA8888 or VtfImageFormat.ABGR8888 or
            VtfImageFormat.ARGB8888 or VtfImageFormat.BGRA8888 or
            VtfImageFormat.BGRX8888 or VtfImageFormat.UVWQ8888 or
            VtfImageFormat.UVLX8888 => width * height * 4,
            VtfImageFormat.RGB888 or VtfImageFormat.BGR888 or
            VtfImageFormat.RGB888_BlueScreen or VtfImageFormat.BGR888_BlueScreen => width * height * 3,
            VtfImageFormat.RGB565 or VtfImageFormat.BGR565 or
            VtfImageFormat.BGRA4444 or VtfImageFormat.BGRA5551 or
            VtfImageFormat.BGRX5551 or VtfImageFormat.IA88 or
            VtfImageFormat.UV88 => width * height * 2,
            VtfImageFormat.I8 or VtfImageFormat.A8 or VtfImageFormat.P8 => width * height,
            VtfImageFormat.RGBA16161616F or VtfImageFormat.RGBA16161616 => width * height * 8,
            _ => 0,
        };
    }

    /// <summary>
    /// Decode compressed/raw image data to RGBA8888 byte array.
    /// </summary>
    private static byte[] DecodeImage(byte[] data, int width, int height, VtfImageFormat format)
    {
        byte[] rgba = new byte[width * height * 4];

        switch (format)
        {
            case VtfImageFormat.RGBA8888:
                Buffer.BlockCopy(data, 0, rgba, 0, Math.Min(data.Length, rgba.Length));
                break;

            case VtfImageFormat.BGRA8888:
            case VtfImageFormat.BGRX8888:
                for (int i = 0; i < width * height && i * 4 + 3 < data.Length; i++)
                {
                    rgba[i * 4 + 0] = data[i * 4 + 2]; // R
                    rgba[i * 4 + 1] = data[i * 4 + 1]; // G
                    rgba[i * 4 + 2] = data[i * 4 + 0]; // B
                    rgba[i * 4 + 3] = format == VtfImageFormat.BGRX8888 ? (byte)255 : data[i * 4 + 3];
                }
                break;

            case VtfImageFormat.ABGR8888:
                for (int i = 0; i < width * height && i * 4 + 3 < data.Length; i++)
                {
                    rgba[i * 4 + 0] = data[i * 4 + 3]; // R
                    rgba[i * 4 + 1] = data[i * 4 + 2]; // G
                    rgba[i * 4 + 2] = data[i * 4 + 1]; // B
                    rgba[i * 4 + 3] = data[i * 4 + 0]; // A
                }
                break;

            case VtfImageFormat.ARGB8888:
                for (int i = 0; i < width * height && i * 4 + 3 < data.Length; i++)
                {
                    rgba[i * 4 + 0] = data[i * 4 + 1]; // R
                    rgba[i * 4 + 1] = data[i * 4 + 2]; // G
                    rgba[i * 4 + 2] = data[i * 4 + 3]; // B
                    rgba[i * 4 + 3] = data[i * 4 + 0]; // A
                }
                break;

            case VtfImageFormat.RGB888:
            case VtfImageFormat.RGB888_BlueScreen:
                for (int i = 0; i < width * height && i * 3 + 2 < data.Length; i++)
                {
                    rgba[i * 4 + 0] = data[i * 3 + 0];
                    rgba[i * 4 + 1] = data[i * 3 + 1];
                    rgba[i * 4 + 2] = data[i * 3 + 2];
                    rgba[i * 4 + 3] = 255;
                }
                break;

            case VtfImageFormat.BGR888:
            case VtfImageFormat.BGR888_BlueScreen:
                for (int i = 0; i < width * height && i * 3 + 2 < data.Length; i++)
                {
                    rgba[i * 4 + 0] = data[i * 3 + 2];
                    rgba[i * 4 + 1] = data[i * 3 + 1];
                    rgba[i * 4 + 2] = data[i * 3 + 0];
                    rgba[i * 4 + 3] = 255;
                }
                break;

            case VtfImageFormat.I8:
                for (int i = 0; i < width * height && i < data.Length; i++)
                {
                    rgba[i * 4 + 0] = data[i];
                    rgba[i * 4 + 1] = data[i];
                    rgba[i * 4 + 2] = data[i];
                    rgba[i * 4 + 3] = 255;
                }
                break;

            case VtfImageFormat.A8:
                for (int i = 0; i < width * height && i < data.Length; i++)
                {
                    rgba[i * 4 + 0] = 255;
                    rgba[i * 4 + 1] = 255;
                    rgba[i * 4 + 2] = 255;
                    rgba[i * 4 + 3] = data[i];
                }
                break;

            case VtfImageFormat.DXT1:
            case VtfImageFormat.DXT1_OneBitAlpha:
                DecodeDxt1(data, width, height, rgba);
                break;

            case VtfImageFormat.DXT3:
                DecodeDxt3(data, width, height, rgba);
                break;

            case VtfImageFormat.DXT5:
                DecodeDxt5(data, width, height, rgba);
                break;

            case VtfImageFormat.RGBA16161616F:
                DecodeRgba16f(data, width, height, rgba);
                break;
            case VtfImageFormat.RGBA16161616:
                DecodeRgba16(data, width, height, rgba);
                break;

            default:
                // Unsupported format — return transparent
                break;
        }

        return rgba;
    }

    #region DXT Decompression

    private static void DecodeDxt1(byte[] data, int width, int height, byte[] output)
    {
        int blockCountX = Math.Max(1, (width + 3) / 4);
        int blockCountY = Math.Max(1, (height + 3) / 4);
        int offset = 0;

        for (int by = 0; by < blockCountY; by++)
        {
            for (int bx = 0; bx < blockCountX; bx++)
            {
                if (offset + 8 > data.Length) return;

                ushort c0 = BitConverter.ToUInt16(data, offset);
                ushort c1 = BitConverter.ToUInt16(data, offset + 2);
                uint indices = BitConverter.ToUInt32(data, offset + 4);
                offset += 8;

                DecodeColor565(c0, out byte r0, out byte g0, out byte b0);
                DecodeColor565(c1, out byte r1, out byte g1, out byte b1);

                byte[][] colors = new byte[4][];
                colors[0] = new byte[] { r0, g0, b0, 255 };
                colors[1] = new byte[] { r1, g1, b1, 255 };

                if (c0 > c1)
                {
                    colors[2] = new byte[] { (byte)((2 * r0 + r1) / 3), (byte)((2 * g0 + g1) / 3), (byte)((2 * b0 + b1) / 3), 255 };
                    colors[3] = new byte[] { (byte)((r0 + 2 * r1) / 3), (byte)((g0 + 2 * g1) / 3), (byte)((b0 + 2 * b1) / 3), 255 };
                }
                else
                {
                    colors[2] = new byte[] { (byte)((r0 + r1) / 2), (byte)((g0 + g1) / 2), (byte)((b0 + b1) / 2), 255 };
                    colors[3] = new byte[] { 0, 0, 0, 0 };
                }

                for (int py = 0; py < 4; py++)
                {
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px;
                        int y = by * 4 + py;
                        if (x >= width || y >= height) continue;

                        int idx = (int)((indices >> (2 * (py * 4 + px))) & 0x3);
                        int pos = (y * width + x) * 4;
                        output[pos + 0] = colors[idx][0];
                        output[pos + 1] = colors[idx][1];
                        output[pos + 2] = colors[idx][2];
                        output[pos + 3] = colors[idx][3];
                    }
                }
            }
        }
    }

    private static void DecodeDxt3(byte[] data, int width, int height, byte[] output)
    {
        int blockCountX = Math.Max(1, (width + 3) / 4);
        int blockCountY = Math.Max(1, (height + 3) / 4);
        int offset = 0;

        for (int by = 0; by < blockCountY; by++)
        {
            for (int bx = 0; bx < blockCountX; bx++)
            {
                if (offset + 16 > data.Length) return;

                // Read 8 bytes of alpha
                byte[] alphaBlock = new byte[8];
                Array.Copy(data, offset, alphaBlock, 0, 8);
                offset += 8;

                // Color block (same as DXT1)
                ushort c0 = BitConverter.ToUInt16(data, offset);
                ushort c1 = BitConverter.ToUInt16(data, offset + 2);
                uint indices = BitConverter.ToUInt32(data, offset + 4);
                offset += 8;

                DecodeColor565(c0, out byte r0, out byte g0, out byte b0);
                DecodeColor565(c1, out byte r1, out byte g1, out byte b1);

                byte[][] colors = new byte[4][];
                colors[0] = new byte[] { r0, g0, b0 };
                colors[1] = new byte[] { r1, g1, b1 };
                colors[2] = new byte[] { (byte)((2 * r0 + r1) / 3), (byte)((2 * g0 + g1) / 3), (byte)((2 * b0 + b1) / 3) };
                colors[3] = new byte[] { (byte)((r0 + 2 * r1) / 3), (byte)((g0 + 2 * g1) / 3), (byte)((b0 + 2 * b1) / 3) };

                for (int py = 0; py < 4; py++)
                {
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px;
                        int y = by * 4 + py;
                        if (x >= width || y >= height) continue;

                        int idx = (int)((indices >> (2 * (py * 4 + px))) & 0x3);
                        int alphaIdx = py * 4 + px;
                        int alphaByte = alphaIdx / 2;
                        byte alpha;
                        if ((alphaIdx & 1) == 0)
                            alpha = (byte)((alphaBlock[alphaByte] & 0x0F) * 17);
                        else
                            alpha = (byte)(((alphaBlock[alphaByte] >> 4) & 0x0F) * 17);

                        int pos = (y * width + x) * 4;
                        output[pos + 0] = colors[idx][0];
                        output[pos + 1] = colors[idx][1];
                        output[pos + 2] = colors[idx][2];
                        output[pos + 3] = alpha;
                    }
                }
            }
        }
    }

    private static void DecodeDxt5(byte[] data, int width, int height, byte[] output)
    {
        int blockCountX = Math.Max(1, (width + 3) / 4);
        int blockCountY = Math.Max(1, (height + 3) / 4);
        int offset = 0;

        for (int by = 0; by < blockCountY; by++)
        {
            for (int bx = 0; bx < blockCountX; bx++)
            {
                if (offset + 16 > data.Length) return;

                // Alpha block
                byte a0 = data[offset];
                byte a1 = data[offset + 1];
                ulong alphaBits = 0;
                for (int i = 0; i < 6; i++)
                    alphaBits |= (ulong)data[offset + 2 + i] << (8 * i);
                offset += 8;

                byte[] alphaTable = new byte[8];
                alphaTable[0] = a0;
                alphaTable[1] = a1;
                if (a0 > a1)
                {
                    for (int i = 0; i < 6; i++)
                        alphaTable[2 + i] = (byte)((a0 * (6 - i) + a1 * (1 + i)) / 7);
                }
                else
                {
                    for (int i = 0; i < 4; i++)
                        alphaTable[2 + i] = (byte)((a0 * (4 - i) + a1 * (1 + i)) / 5);
                    alphaTable[6] = 0;
                    alphaTable[7] = 255;
                }

                // Color block
                ushort c0 = BitConverter.ToUInt16(data, offset);
                ushort c1 = BitConverter.ToUInt16(data, offset + 2);
                uint indices = BitConverter.ToUInt32(data, offset + 4);
                offset += 8;

                DecodeColor565(c0, out byte r0, out byte g0, out byte b0);
                DecodeColor565(c1, out byte r1, out byte g1, out byte b1);

                byte[][] colors = new byte[4][];
                colors[0] = new byte[] { r0, g0, b0 };
                colors[1] = new byte[] { r1, g1, b1 };
                colors[2] = new byte[] { (byte)((2 * r0 + r1) / 3), (byte)((2 * g0 + g1) / 3), (byte)((2 * b0 + b1) / 3) };
                colors[3] = new byte[] { (byte)((r0 + 2 * r1) / 3), (byte)((g0 + 2 * g1) / 3), (byte)((b0 + 2 * b1) / 3) };

                for (int py = 0; py < 4; py++)
                {
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px;
                        int y = by * 4 + py;
                        if (x >= width || y >= height) continue;

                        int colorIdx = (int)((indices >> (2 * (py * 4 + px))) & 0x3);
                        int alphaPixelIdx = py * 4 + px;
                        int alphaCode = (int)((alphaBits >> (3 * alphaPixelIdx)) & 0x7);

                        int pos = (y * width + x) * 4;
                        output[pos + 0] = colors[colorIdx][0];
                        output[pos + 1] = colors[colorIdx][1];
                        output[pos + 2] = colors[colorIdx][2];
                        output[pos + 3] = alphaTable[alphaCode];
                    }
                }
            }
        }
    }

    private static void DecodeColor565(ushort color, out byte r, out byte g, out byte b)
    {
        r = (byte)(((color >> 11) & 0x1F) * 255 / 31);
        g = (byte)(((color >> 5) & 0x3F) * 255 / 63);
        b = (byte)((color & 0x1F) * 255 / 31);
    }

    private static void DecodeRgba16f(byte[] data, int width, int height, byte[] output)
    {
        int pixelCount = width * height;
        if (data.Length < pixelCount * 8) return;

        for (int i = 0; i < pixelCount; i++)
        {
            int src = i * 8;
            int dst = i * 4;

            // Read 16-bit float (Half)
            float r = (float)BitConverter.ToHalf(data, src + 0);
            float g = (float)BitConverter.ToHalf(data, src + 2);
            float b = (float)BitConverter.ToHalf(data, src + 4);
            float a = (float)BitConverter.ToHalf(data, src + 6);

            // Clamp and convert to 8-bit
            output[dst + 0] = (byte)Math.Max(0, Math.Min(255, r * 255f));
            output[dst + 1] = (byte)Math.Max(0, Math.Min(255, g * 255f));
            output[dst + 2] = (byte)Math.Max(0, Math.Min(255, b * 255f));
            output[dst + 3] = (byte)Math.Max(0, Math.Min(255, a * 255f));
        }
    }

    private static void DecodeRgba16(byte[] data, int width, int height, byte[] output)
    {
        int pixelCount = width * height;
        if (data.Length < pixelCount * 8) return;

        for (int i = 0; i < pixelCount; i++)
        {
            int src = i * 8;
            int dst = i * 4;

            // Read 16-bit unsigned integer (normalized 0-65535 map to 0-1)
            ushort r = BitConverter.ToUInt16(data, src + 0);
            ushort g = BitConverter.ToUInt16(data, src + 2);
            ushort b = BitConverter.ToUInt16(data, src + 4);
            ushort a = BitConverter.ToUInt16(data, src + 6);

            output[dst + 0] = (byte)(r >> 8); // High byte matches most significant 8 bits
            output[dst + 1] = (byte)(g >> 8);
            output[dst + 2] = (byte)(b >> 8);
            output[dst + 3] = (byte)(a >> 8);
        }
    }

    #endregion
}
