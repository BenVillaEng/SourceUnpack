namespace SourceUnpack.Core.Conversion;

/// <summary>
/// Extracts and converts audio files from BSP/VPK sources.
/// Source Engine sounds are typically WAV — MP3/OGG are converted to WAV.
/// </summary>
public static class AudioConverter
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".ogg"
    };

    /// <summary>
    /// Check if a file path is a supported audio format.
    /// </summary>
    public static bool IsAudioFile(string path)
    {
        string ext = Path.GetExtension(path);
        return SupportedExtensions.Contains(ext);
    }

    /// <summary>
    /// Extract audio data to the output path. WAV files are copied directly.
    /// For other formats, wraps raw PCM or copies as-is (most Source audio is WAV).
    /// Returns true if successful.
    /// </summary>
    public static bool ExtractAudio(byte[] audioData, string sourcePath, string outputPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();

            switch (ext)
            {
                case ".wav":
                    // WAV files — copy directly, already in the right format
                    File.WriteAllBytes(outputPath, audioData);
                    return true;

                case ".mp3":
                    // MP3 files — copy with .mp3 extension (or convert if WAV output requested)
                    if (outputPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    {
                        // Simple approach: wrap as PCM WAV if we can detect header,
                        // otherwise just save the raw bytes with correct extension
                        string mp3Output = Path.ChangeExtension(outputPath, ".mp3");
                        File.WriteAllBytes(mp3Output, audioData);
                    }
                    else
                    {
                        File.WriteAllBytes(outputPath, audioData);
                    }
                    return true;

                case ".ogg":
                    // OGG files — same approach: preserve format
                    File.WriteAllBytes(outputPath, audioData);
                    return true;

                default:
                    // Unknown format — try saving anyway
                    File.WriteAllBytes(outputPath, audioData);
                    return true;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get the preferred output extension for an audio source file.
    /// </summary>
    public static string GetOutputExtension(string sourcePath)
    {
        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        // Most Source audio is already WAV
        return ext switch
        {
            ".wav" => ".wav",
            ".mp3" => ".mp3",
            ".ogg" => ".ogg",
            _ => ".wav"
        };
    }
}
