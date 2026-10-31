using System;

namespace SwiftGet.Core.IO;

public enum FileNameSource
{
    UserEdited,
    BrowserCaptured,
    ContentDisposition,
    FinalUrlPath,
    QueryString,
    PageTitle,
    Fallback
}

public sealed class FileNameResolutionContext
{
    public string? OriginalUrl { get; set; }
    public string? FinalUrl { get; set; }
    public string? ContentDisposition { get; set; }
    public string? ContentType { get; set; }
    public string? BrowserCapturedName { get; set; }
    public string? UserEditedName { get; set; }
    public string? PageTitle { get; set; }
    public ReadOnlyMemory<byte>? InitialBytes { get; set; }
    public bool UserExplicitlyNamed { get; set; }
}

public sealed class ResolvedFileName
{
    public string FileName { get; set; } = string.Empty;
    public string BaseName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public FileNameSource Source { get; set; }
    public bool ExtensionRecovered { get; set; }
    public bool IsDangerous { get; set; }
}

public interface IFileNameResolver
{
    ResolvedFileName Resolve(FileNameResolutionContext context);
    string SanitizeFileName(string rawName, string fallback = "download.bin");
    string ResolveInitialCandidate(string url, string? browserSuggestedName = null);
    string RecoverExtension(string fileName, string? contentType, ReadOnlySpan<byte> initialBytes = default);
    string ResolveUniquePath(string directory, string desiredFileName, bool overwrite = false);
    bool IsGenericOrScriptExtension(string ext);
    bool IsDangerousExtension(string ext);
    string GetCompoundExtension(string fileName);
    string GetSafeBaseName(string fileName);
}
