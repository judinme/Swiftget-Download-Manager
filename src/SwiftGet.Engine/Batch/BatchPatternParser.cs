using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SwiftGet.Engine.Batch;

/// <summary>
/// Parser and generator for batch URL patterns (e.g. numeric ranges [001-100] and character ranges [a-z]).
/// </summary>
public static partial class BatchPatternParser
{
    public const int MaxGeneratedUrls = 5000;

    [GeneratedRegex(@"\[([0-9a-zA-Z]+)-([0-9a-zA-Z]+)(?::(-?\d+))?\]")]
    private static partial Regex RangePatternRegex();

    /// <summary>
    /// Checks if a pattern string contains valid range syntax and is within size limits.
    /// </summary>
    public static bool IsValidPattern(string patternTemplate, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(patternTemplate))
        {
            errorMessage = "Pattern cannot be empty.";
            return false;
        }

        var matches = RangePatternRegex().Matches(patternTemplate);
        if (matches.Count == 0)
        {
            errorMessage = "No valid range pattern found (expected format e.g. [1-10] or [001-100] or [a-z]).";
            return false;
        }

        try
        {
            var testCount = 1;
            foreach (Match match in matches)
            {
                var startStr = match.Groups[1].Value;
                var endStr = match.Groups[2].Value;
                var stepStr = match.Groups[3].Success ? match.Groups[3].Value : null;

                var rangeCount = CalculateRangeCount(startStr, endStr, stepStr);
                if (rangeCount <= 0)
                {
                    errorMessage = $"Invalid range [{startStr}-{endStr}].";
                    return false;
                }

                testCount *= rangeCount;
                if (testCount > MaxGeneratedUrls)
                {
                    errorMessage = $"Pattern would generate {testCount} URLs, exceeding limit of {MaxGeneratedUrls}.";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Generates the list of URLs by expanding all bracketed range tokens.
    /// </summary>
    public static IReadOnlyList<string> GenerateUrls(string patternTemplate)
    {
        if (string.IsNullOrWhiteSpace(patternTemplate))
        {
            return [];
        }

        var matches = RangePatternRegex().Matches(patternTemplate);
        if (matches.Count == 0)
        {
            return [patternTemplate.Trim()];
        }

        var currentList = new List<string> { patternTemplate };

        // Expand each range token sequentially
        foreach (Match match in matches)
        {
            var token = match.Value;
            var startStr = match.Groups[1].Value;
            var endStr = match.Groups[2].Value;
            var stepStr = match.Groups[3].Success ? match.Groups[3].Value : null;

            var estimatedCount = CalculateRangeCount(startStr, endStr, stepStr);
            if (estimatedCount > MaxGeneratedUrls || (long)currentList.Count * estimatedCount > MaxGeneratedUrls)
            {
                throw new InvalidOperationException($"Batch generation exceeded max limit of {MaxGeneratedUrls} URLs.");
            }

            var replacements = ExpandRange(startStr, endStr, stepStr);
            var nextList = new List<string>(Math.Min(MaxGeneratedUrls + 1, currentList.Count * replacements.Count));

            foreach (var template in currentList)
            {
                // Replace only the first occurrence of this token
                var tokenIndex = template.IndexOf(token, StringComparison.Ordinal);
                if (tokenIndex < 0) continue;

                var prefix = template[..tokenIndex];
                var suffix = template[(tokenIndex + token.Length)..];

                foreach (var rep in replacements)
                {
                    nextList.Add(prefix + rep + suffix);
                    if (nextList.Count > MaxGeneratedUrls)
                    {
                        throw new InvalidOperationException($"Batch generation exceeded max limit of {MaxGeneratedUrls} URLs.");
                    }
                }
            }

            currentList = nextList;
        }

        return currentList;
    }

    private static int CalculateRangeCount(string startStr, string endStr, string? stepStr)
    {
        // 1. Numeric Range: [001-100] or [1-10:2]
        if (long.TryParse(startStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var startNum) &&
            long.TryParse(endStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var endNum))
        {
            var step = 1;
            if (!string.IsNullOrWhiteSpace(stepStr) && int.TryParse(stepStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var customStep))
            {
                step = customStep != 0 ? Math.Abs(customStep) : 1;
            }

            var diff = Math.Abs(endNum - startNum);
            var count = (diff / step) + 1;
            return count > int.MaxValue ? int.MaxValue : (int)count;
        }

        // 2. Character Range: [a-z] or [A-Z]
        if (startStr.Length == 1 && endStr.Length == 1 && char.IsLetter(startStr[0]) && char.IsLetter(endStr[0]))
        {
            var startChar = startStr[0];
            var endChar = endStr[0];
            var step = 1;
            if (!string.IsNullOrWhiteSpace(stepStr) && int.TryParse(stepStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var customStep))
            {
                step = Math.Max(1, Math.Abs(customStep));
            }

            var diff = Math.Abs(endChar - startChar);
            var count = (diff / step) + 1;
            return count;
        }

        throw new ArgumentException($"Unsupported range bounds: [{startStr}-{endStr}]");
    }

    private static List<string> ExpandRange(string startStr, string endStr, string? stepStr)
    {
        var result = new List<string>();

        // 1. Numeric Range: [001-100] or [1-10:2]
        if (long.TryParse(startStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var startNum) &&
            long.TryParse(endStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var endNum))
        {
            var step = 1;
            if (!string.IsNullOrWhiteSpace(stepStr) && int.TryParse(stepStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var customStep))
            {
                step = customStep != 0 ? customStep : 1;
            }

            var padding = startStr.Length;
            var hasLeadingZeros = startStr.StartsWith('0') && startStr.Length > 1;

            if (startNum <= endNum)
            {
                if (step < 0) step = Math.Abs(step);
                for (var i = startNum; i <= endNum; i += step)
                {
                    result.Add(hasLeadingZeros ? i.ToString(CultureInfo.InvariantCulture).PadLeft(padding, '0') : i.ToString(CultureInfo.InvariantCulture));
                }
            }
            else
            {
                if (step > 0) step = -step;
                for (var i = startNum; i >= endNum; i += step)
                {
                    result.Add(hasLeadingZeros ? i.ToString(CultureInfo.InvariantCulture).PadLeft(padding, '0') : i.ToString(CultureInfo.InvariantCulture));
                }
            }

            return result;
        }

        // 2. Character Range: [a-z] or [A-Z]
        if (startStr.Length == 1 && endStr.Length == 1 && char.IsLetter(startStr[0]) && char.IsLetter(endStr[0]))
        {
            var startChar = startStr[0];
            var endChar = endStr[0];
            var step = 1;
            if (!string.IsNullOrWhiteSpace(stepStr) && int.TryParse(stepStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var customStep))
            {
                step = Math.Max(1, Math.Abs(customStep));
            }

            if (startChar <= endChar)
            {
                for (var c = startChar; c <= endChar; c = (char)(c + step))
                {
                    result.Add(c.ToString());
                }
            }
            else
            {
                for (var c = startChar; c >= endChar; c = (char)(c - step))
                {
                    result.Add(c.ToString());
                }
            }

            return result;
        }

        throw new ArgumentException($"Unsupported range bounds: [{startStr}-{endStr}]");
    }
}
