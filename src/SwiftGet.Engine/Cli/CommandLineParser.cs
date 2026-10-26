namespace SwiftGet.Engine.Cli;

public static class CommandLineParser
{
    public static CliCommand Parse(string[]? args)
    {
        var command = new CliCommand();
        if (args == null || args.Length == 0)
        {
            return command;
        }

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i].Trim();
            if (string.IsNullOrWhiteSpace(arg)) continue;

            // Help flags
            if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/?", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/help", StringComparison.OrdinalIgnoreCase))
            {
                command.ShowHelp = true;
                continue;
            }

            // IDM /h switch: Disconnect dial-up/VPN upon download completion
            if (arg.Equals("/h", StringComparison.OrdinalIgnoreCase))
            {
                command.HangUpAfterCompletion = true;
                continue;
            }

            // Start flags: /s starts download/active queue immediately
            if (arg.Equals("--start", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-s", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/s", StringComparison.OrdinalIgnoreCase))
            {
                command.StartImmediately = true;
                continue;
            }

            // Quiet / Silent flags: /n suppresses confirmation and progress dialogs
            if (arg.Equals("--quiet", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-n", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/n", StringComparison.OrdinalIgnoreCase))
            {
                command.Quiet = true;
                continue;
            }

            // Test mode flags: --test-mode runs in isolated environment
            if (arg.Equals("--test-mode", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/test-mode", StringComparison.OrdinalIgnoreCase))
            {
                command.TestMode = true;
                continue;
            }

            // Custom data directory for tests
            if (arg.Equals("--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-') && !args[i + 1].StartsWith('/'))
                {
                    command.CustomDataDirectory = args[++i].Trim();
                }
                continue;
            }

            // Custom loopback port for tests
            if (arg.Equals("--port", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out var port))
                {
                    command.CustomLoopbackPort = port;
                    i++;
                }
                continue;
            }

            // IDM /a flag: enqueue URL without starting immediately / quit after enqueue
            if (arg.Equals("--quit", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/a", StringComparison.OrdinalIgnoreCase))
            {
                command.QuitAfterAdd = true;
                continue;
            }

            // URL flags: /d URL initiates downloading of the specified file URL
            if (arg.Equals("--url", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-u", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/d", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-') && !args[i + 1].StartsWith('/'))
                {
                    command.Url = args[++i].Trim();
                }
                continue;
            }

            // Path flags: /p local_path defines destination directory
            if (arg.Equals("--path", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-p", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/p", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-') && !args[i + 1].StartsWith('/'))
                {
                    command.DestinationPath = args[++i].Trim();
                }
                continue;
            }

            // Filename flags: /f local_file_name overrides target filename
            if (arg.Equals("--filename", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-f", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/f", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-') && !args[i + 1].StartsWith('/'))
                {
                    command.FileName = args[++i].Trim();
                }
                continue;
            }

            // Queue / Quit-after-completion flags:
            // In IDM, /q without argument means exit after all downloads complete;
            // with argument, it specifies the target queue.
            if (arg.Equals("--queue", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-q", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/q", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-') && !args[i + 1].StartsWith('/'))
                {
                    command.QueueName = args[++i].Trim();
                }
                else
                {
                    command.QuitAfterCompletion = true;
                    command.QueueName = "default";
                }
                continue;
            }

            // Torrent flags
            if (arg.Equals("--torrent", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--add-torrent", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-t", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-') && !args[i + 1].StartsWith('/'))
                {
                    command.Url = args[++i].Trim().Trim('"');
                }
                continue;
            }

            // Bare URL or Torrent argument
            if (string.IsNullOrWhiteSpace(command.Url))
            {
                var unquoted = arg.Trim('"');
                if (unquoted.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    unquoted.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    unquoted.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ||
                    unquoted.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) ||
                    unquoted.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                {
                    command.Url = unquoted;
                }
            }
        }

        return command;
    }

    public static string GetHelpText()
    {
        return @"SwiftGet Download Manager CLI

Usage:
  SwiftGet.exe [URL or Magnet or TorrentFile] [options]
  SwiftGet.exe /d <URL> [options]
  SwiftGet.exe --torrent <Magnet or TorrentFile> [options]

Options:
  --url, -u, /d <URL>             Download URL
  --torrent, -t <MAGNET/.TORRENT> Torrent magnet URI or .torrent file path
  --path, -p, /p <PATH>           Destination folder path
  --filename, -f, /f <NAME>       Destination file name
  --queue, -q, /q [NAME]          Add to queue (default: Main Queue)
  --start, -s, /s                 Start download or queue immediately
  --quiet, -n, /n                 Silent execution without interactive dialogs
  --quit, /a                      Quit application after adding download
  --help, -h, /?                  Display this help text

Examples:
  SwiftGet.exe https://example.com/file.zip
  SwiftGet.exe ""magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=Ubuntu""
  SwiftGet.exe ""C:\Downloads\linux.torrent"" -p ""D:\ISO"" --start
  SwiftGet.exe /d https://example.com/file.zip /p C:\Downloads /s
  SwiftGet.exe --url https://example.com/file.zip --queue ""Night Queue"" --quiet
";
    }
}
