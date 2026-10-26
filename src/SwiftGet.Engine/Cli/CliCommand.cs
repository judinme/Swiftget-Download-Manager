namespace SwiftGet.Engine.Cli;

public class CliCommand
{
    public string? Url { get; set; }
    public string? DestinationPath { get; set; }
    public string? FileName { get; set; }
    public string? QueueName { get; set; }
    public bool StartImmediately { get; set; }
    public bool Quiet { get; set; }
    public bool QuitAfterAdd { get; set; }
    public bool QuitAfterCompletion { get; set; }
    public bool HangUpAfterCompletion { get; set; }
    public bool ShowHelp { get; set; }
    public bool TestMode { get; set; }
    public string? CustomDataDirectory { get; set; }
    public int? CustomLoopbackPort { get; set; }
    public bool IsTorrent => IsTorrentInput(Url);
    public bool HasActionableArguments => !string.IsNullOrWhiteSpace(Url) || ShowHelp;

    public static bool IsTorrentInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        var trimmed = input.Trim().Trim('"');
        return trimmed.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
    }
}
