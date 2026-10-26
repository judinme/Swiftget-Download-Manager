namespace SwiftGet.Engine.Network;

public enum DownloadErrorCategory
{
    None,
    Transient,
    RateLimited,
    Authentication,
    ExpiredUrl,
    DiskFull,
    Permission,
    TlsFailure,
    RangeRejected,
    Unrecoverable
}
