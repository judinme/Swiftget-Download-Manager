namespace SwiftGet.Engine.Network;

public interface ILinkRefreshHook
{
    Task<string?> RefreshUrlAsync(string downloadId, string currentUrl, CancellationToken ct = default);
}
