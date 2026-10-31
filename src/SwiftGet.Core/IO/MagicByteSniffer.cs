using System;
using System.Text;

namespace SwiftGet.Core.IO;

public static class MagicByteSniffer
{
    public static string? SniffExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2)
        {
            return null;
        }

        // 1. GZIP: 1F 8B
        if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            return ".gz";
        }

        // 2. BZIP2: 'B', 'Z', 'h'
        if (bytes.Length >= 3 && bytes[0] == 0x42 && bytes[1] == 0x5A && bytes[2] == 0x68)
        {
            return ".bz2";
        }

        // 3. XZ: FD 37 7A 58 5A 00
        if (bytes.Length >= 6 &&
            bytes[0] == 0xFD && bytes[1] == 0x37 && bytes[2] == 0x7A &&
            bytes[3] == 0x58 && bytes[4] == 0x5A && bytes[5] == 0x00)
        {
            return ".xz";
        }

        // 4. Zstandard: 28 B5 2F FD
        if (bytes.Length >= 4 &&
            bytes[0] == 0x28 && bytes[1] == 0xB5 && bytes[2] == 0x2F && bytes[3] == 0xFD)
        {
            return ".zst";
        }

        // 5. 7-Zip: 37 7A BC AF 27 1C
        if (bytes.Length >= 6 &&
            bytes[0] == 0x37 && bytes[1] == 0x7A && bytes[2] == 0xBC &&
            bytes[3] == 0xAF && bytes[4] == 0x27 && bytes[5] == 0x1C)
        {
            return ".7z";
        }

        // 6. RAR: "Rar!\x1A\x07\x00" (RAR4) or "Rar!\x1A\x07\x01\x00" (RAR5)
        if (bytes.Length >= 7 &&
            bytes[0] == 0x52 && bytes[1] == 0x61 && bytes[2] == 0x72 && bytes[3] == 0x21 &&
            bytes[4] == 0x1A && bytes[5] == 0x07)
        {
            return ".rar";
        }

        // 7. ZIP & Office / APK / JAR: "PK\x03\x04"
        if (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04)
        {
            return SniffZipBasedExtension(bytes);
        }

        // 8. PDF: "%PDF-"
        if (bytes.Length >= 5 &&
            bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46 && bytes[4] == 0x2D)
        {
            return ".pdf";
        }

        // 9. Windows Executable / DLL: "MZ"
        if (bytes[0] == 0x4D && bytes[1] == 0x5A)
        {
            return ".exe";
        }

        // 10. PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return ".png";
        }

        // 11. JPEG: FF D8 FF
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ".jpg";
        }

        // 12. GIF: "GIF87a" or "GIF89a"
        if (bytes.Length >= 6 &&
            bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38 &&
            (bytes[4] == 0x37 || bytes[4] == 0x39) && bytes[5] == 0x61)
        {
            return ".gif";
        }

        // 13. WEBP: "RIFF....WEBP"
        if (bytes.Length >= 12 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return ".webp";
        }

        // 14. MP4 / MOV: 'f','t','y','p' at offset 4
        if (bytes.Length >= 12 &&
            bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
        {
            // Check for QuickTime 'qt  '
            if (bytes[8] == 0x71 && bytes[9] == 0x74 && bytes[10] == 0x20 && bytes[11] == 0x20)
            {
                return ".mov";
            }
            return ".mp4";
        }

        // 15. MKV / WebM: EBML header 1A 45 DF A3
        if (bytes.Length >= 4 &&
            bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3)
        {
            // Sniff doctype in EBML header if available
            var ebmlScanLen = Math.Min(bytes.Length, 64);
            var ebmlHeader = Encoding.ASCII.GetString(bytes[..ebmlScanLen]);
            if (ebmlHeader.Contains("webm", StringComparison.OrdinalIgnoreCase))
            {
                return ".webm";
            }
            return ".mkv";
        }

        // 16. MP3: "ID3" or sync words FF FB, FF F3, FF F2
        if (bytes.Length >= 3 && bytes[0] == 0x49 && bytes[1] == 0x44 && bytes[2] == 0x33)
        {
            return ".mp3";
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && (bytes[1] == 0xFB || bytes[1] == 0xF3 || bytes[1] == 0xF2))
        {
            return ".mp3";
        }

        // 17. TAR: "ustar" at offset 257
        if (bytes.Length >= 262 &&
            bytes[257] == 0x75 && bytes[258] == 0x73 && bytes[259] == 0x74 && bytes[260] == 0x61 && bytes[261] == 0x72)
        {
            return ".tar";
        }

        // 18. ISO 9660: "CD001" at offset 0x8001 (32769)
        if (bytes.Length >= 0x8006 &&
            bytes[0x8001] == 0x43 && bytes[0x8002] == 0x44 && bytes[0x8003] == 0x30 &&
            bytes[0x8004] == 0x30 && bytes[0x8005] == 0x31)
        {
            return ".iso";
        }

        return null;
    }

    private static string SniffZipBasedExtension(ReadOnlySpan<byte> bytes)
    {
        // Convert initial bytes to ASCII string to search for archive directory structure entries
        var sampleLen = Math.Min(bytes.Length, 8192);
        var text = Encoding.ASCII.GetString(bytes[..sampleLen]);

        if (text.Contains("[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Contains("word/", StringComparison.OrdinalIgnoreCase))
            {
                return ".docx";
            }
            if (text.Contains("xl/", StringComparison.OrdinalIgnoreCase))
            {
                return ".xlsx";
            }
            if (text.Contains("ppt/", StringComparison.OrdinalIgnoreCase))
            {
                return ".pptx";
            }
        }

        if (text.Contains("AndroidManifest.xml", StringComparison.OrdinalIgnoreCase))
        {
            return ".apk";
        }

        if (text.Contains("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase))
        {
            return ".jar";
        }

        return ".zip";
    }
}
