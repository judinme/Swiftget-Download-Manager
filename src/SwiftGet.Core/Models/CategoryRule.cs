namespace SwiftGet.Core.Models;

public class CategoryRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string TargetDirectory { get; set; } = string.Empty;
    public string FileExtensions { get; set; } = string.Empty; // e.g. "zip,rar,7z,tar,gz"
    public string IconKey { get; set; } = "Document";
    public bool IsDefault { get; set; }

    public bool MatchesExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(FileExtensions)) return false;
        var cleanExt = extension.TrimStart('.').ToLowerInvariant();
        var parts = FileExtensions.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => p.Equals(cleanExt, StringComparison.OrdinalIgnoreCase));
    }
}
