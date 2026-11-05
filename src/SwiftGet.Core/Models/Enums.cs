namespace SwiftGet.Core.Models;

public enum JobKind
{
    Http,
    Media,
    Torrent
}

public enum DownloadStatus
{
    Queued,
    Probing,
    Allocating,
    FetchingMetadata,
    Checking,
    Downloading,
    Seeding,
    Pausing,
    Paused,
    Verifying,
    Completed,
    Failed,
    RefreshingLink,
    Cancelled
}

public enum TorrentFilePriority
{
    DoNotDownload = 0,
    Lowest = 1,
    Low = 2,
    Normal = 3,
    High = 4,
    Highest = 5
}

public enum TorrentEncryptionMode
{
    PreferEncryption = 0,
    RequireEncryption = 1,
    AllowPlaintext = 2
}

public enum SegmentStatus
{
    Pending,
    Downloading,
    Completed,
    Failed
}

public enum ChecksumType
{
    None,
    Md5,
    Sha1,
    Sha256
}

public enum AfterCompletionAction
{
    None,
    ExitApp,
    Sleep,
    Hibernate,
    Shutdown,
    ExecuteCommand
}

public enum PieceState : byte
{
    Missing = 0,
    Downloading = 1,
    Downloaded = 2,
    Verifying = 3
}
