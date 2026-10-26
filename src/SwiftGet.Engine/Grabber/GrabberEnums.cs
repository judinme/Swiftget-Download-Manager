namespace SwiftGet.Engine.Grabber;

/// <summary>
/// Domain boundary options for the site grabber crawler.
/// </summary>
public enum DomainScope
{
    SameDomain,
    Subdomains,
    AnyDomain
}

/// <summary>
/// File filter presets for media and document discovery.
/// </summary>
public enum FileFilterPreset
{
    All,
    Pictures,
    Audio,
    Video,
    Documents,
    Compressed,
    Custom
}
