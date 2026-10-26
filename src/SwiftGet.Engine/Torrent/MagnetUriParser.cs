using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SwiftGet.Engine.Torrent;

public sealed record ParsedMagnetUri
{
    public required string OriginalUri { get; init; }
    public required string InfoHash { get; init; }
    public string? DisplayName { get; init; }
    public IReadOnlyList<string> Trackers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> WebSeeds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExactSources { get; init; } = Array.Empty<string>();
    public IReadOnlyList<int> SelectOnlyIndices { get; init; } = Array.Empty<int>();
    public bool IsV2 { get; init; }
}

public static class MagnetUriParser
{
    public const int MaxUriLength = 8192;
    private const string MagnetScheme = "magnet:?";

    private static readonly Regex Hex40Regex = new("^[0-9a-fA-F]{40}$", RegexOptions.Compiled);
    private static readonly Regex Base32Regex = new("^[2-7a-zA-Z]{32}$", RegexOptions.Compiled);
    private static readonly Regex Hex64Regex = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    public static ParsedMagnetUri Parse(string input)
    {
        if (!TryParse(input, out var result, out var errorMessage))
        {
            throw new FormatException(errorMessage);
        }
        return result!;
    }

    public static bool TryParse(string? input, out ParsedMagnetUri? result, out string? errorMessage)
    {
        result = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            errorMessage = "Magnet URI cannot be null, empty, or whitespace.";
            return false;
        }

        var trimmed = input.Trim();
        if (trimmed.Length > MaxUriLength)
        {
            errorMessage = $"Magnet URI exceeds maximum allowed length of {MaxUriLength} characters.";
            return false;
        }

        if (!trimmed.StartsWith(MagnetScheme, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = $"Magnet URI must start with '{MagnetScheme}'.";
            return false;
        }

        var queryString = trimmed[MagnetScheme.Length..];
        if (string.IsNullOrWhiteSpace(queryString))
        {
            errorMessage = "Magnet URI contains no query parameters.";
            return false;
        }

        string? infoHash = null;
        string? displayName = null;
        bool isV2 = false;
        var trackers = new List<string>();
        var webSeeds = new List<string>();
        var exactSources = new List<string>();
        var selectOnly = new List<int>();

        var pairs = queryString.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            int equalsIndex = pair.IndexOf('=');
            if (equalsIndex <= 0) continue;

            var key = pair[..equalsIndex].Trim();
            var rawValue = pair[(equalsIndex + 1)..].Trim();
            var decodedValue = Uri.UnescapeDataString(rawValue.Replace('+', ' '));

            if (key.Equals("xt", StringComparison.OrdinalIgnoreCase))
            {
                // Exact Topic: urn:btih:<hash> or urn:btmh:<multihash>
                if (decodedValue.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
                {
                    var hashPart = decodedValue["urn:btih:".Length..].Trim();
                    if (Hex40Regex.IsMatch(hashPart))
                    {
                        infoHash = hashPart.ToLowerInvariant();
                        isV2 = false;
                    }
                    else if (Base32Regex.IsMatch(hashPart))
                    {
                        if (TryDecodeBase32ToHex(hashPart, out var convertedHex))
                        {
                            infoHash = convertedHex;
                            isV2 = false;
                        }
                    }
                }
                else if (decodedValue.StartsWith("urn:btmh:", StringComparison.OrdinalIgnoreCase))
                {
                    // BEP 52 multihash: 1220 followed by 64 hex characters (SHA-256)
                    var hashPart = decodedValue["urn:btmh:".Length..].Trim();
                    if (hashPart.StartsWith("1220", StringComparison.OrdinalIgnoreCase) && hashPart.Length == 68)
                    {
                        var sha256Hex = hashPart[4..];
                        if (Hex64Regex.IsMatch(sha256Hex))
                        {
                            infoHash = sha256Hex.ToLowerInvariant();
                            isV2 = true;
                        }
                    }
                    else if (Hex64Regex.IsMatch(hashPart))
                    {
                        infoHash = hashPart.ToLowerInvariant();
                        isV2 = true;
                    }
                }
            }
            else if (key.Equals("dn", StringComparison.OrdinalIgnoreCase))
            {
                displayName = SanitizeDisplayName(decodedValue);
            }
            else if (key.Equals("tr", StringComparison.OrdinalIgnoreCase))
            {
                if (IsValidTrackerUri(decodedValue))
                {
                    trackers.Add(decodedValue);
                }
            }
            else if (key.Equals("ws", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(decodedValue, UriKind.Absolute, out var wsUri) &&
                    (wsUri.Scheme == Uri.UriSchemeHttp || wsUri.Scheme == Uri.UriSchemeHttps))
                {
                    webSeeds.Add(decodedValue);
                }
            }
            else if (key.Equals("xs", StringComparison.OrdinalIgnoreCase))
            {
                exactSources.Add(decodedValue);
            }
            else if (key.Equals("so", StringComparison.OrdinalIgnoreCase))
            {
                // BEP 53: Select-Only indices or ranges: 0,2,4 or 0-3
                if (!TryParseSelectOnly(decodedValue, selectOnly, out var soError))
                {
                    errorMessage = $"Invalid 'so' (select-only) parameter: {soError}";
                    return false;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(infoHash))
        {
            errorMessage = "Magnet URI missing a valid 'xt' parameter with a supported BitTorrent info hash (urn:btih: or urn:btmh:).";
            return false;
        }

        result = new ParsedMagnetUri
        {
            OriginalUri = trimmed,
            InfoHash = infoHash,
            DisplayName = displayName,
            Trackers = trackers.AsReadOnly(),
            WebSeeds = webSeeds.AsReadOnly(),
            ExactSources = exactSources.AsReadOnly(),
            SelectOnlyIndices = selectOnly.AsReadOnly(),
            IsV2 = isV2
        };

        return true;
    }

    private static bool TryParseSelectOnly(string value, List<int> indices, out string? error)
    {
        error = null;
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var token = part.Trim();
            if (token.Contains('-'))
            {
                var range = token.Split('-', StringSplitOptions.RemoveEmptyEntries);
                if (range.Length != 2 ||
                    !int.TryParse(range[0], out var start) ||
                    !int.TryParse(range[1], out var end) ||
                    start < 0 || end < start || end - start > 10000)
                {
                    error = $"Invalid range syntax '{token}'.";
                    return false;
                }

                for (int i = start; i <= end; i++)
                {
                    if (!indices.Contains(i)) indices.Add(i);
                }
            }
            else
            {
                if (!int.TryParse(token, out var singleIdx) || singleIdx < 0)
                {
                    error = $"Invalid file index '{token}'. Must be a non-negative integer.";
                    return false;
                }
                if (!indices.Contains(singleIdx)) indices.Add(singleIdx);
            }
        }
        return true;
    }

    private static bool IsValidTrackerUri(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var scheme = uri.Scheme.ToLowerInvariant();
            return scheme is "http" or "https" or "udp" or "wss";
        }
        return false;
    }

    private static string? SanitizeDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        // Strip path traversal attempts and drive roots
        var cleaned = name.Replace('/', '_').Replace('\\', '_');
        cleaned = cleaned.Replace("..", "__");

        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(cleaned.Length);
        foreach (char c in cleaned)
        {
            if (Array.IndexOf(invalidChars, c) >= 0 || c < 32)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }

        var result = sb.ToString().Trim('.', ' ', '_');
        if (result.Length > 255)
        {
            result = result[..255];
        }

        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static bool TryDecodeBase32ToHex(string base32, out string hex)
    {
        hex = string.Empty;
        const string b32Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var upper = base32.ToUpperInvariant();

        var bits = 0;
        var value = 0;
        var bytes = new List<byte>(20);

        foreach (char c in upper)
        {
            int index = b32Chars.IndexOf(c);
            if (index < 0) return false;

            value = (value << 5) | index;
            bits += 5;

            if (bits >= 8)
            {
                bytes.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        if (bytes.Count != 20) return false;

        hex = Convert.ToHexString(bytes.ToArray()).ToLowerInvariant();
        return true;
    }
}
