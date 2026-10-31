using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using SwiftGet.Engine.Cli;
using SwiftGet.Engine.Coordinator;
using SwiftGet.Engine.Torrent;
using WinRT;

namespace SwiftGet.App;

public static class Program
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    public static void Main(string[] args)
    {
        var cli = CommandLineParser.Parse(args);

        if (cli.ShowHelp)
        {
            AttachConsole(-1);
            Console.WriteLine(CommandLineParser.GetHelpText());
            return;
        }

        // Single-instance forwarding: if another instance is already running, hand off and exit
        if (!cli.TestMode && string.IsNullOrWhiteSpace(cli.CustomDataDirectory) && (cli.HasActionableArguments || cli.IsTorrent))
        {
            var ipc = new SingleInstanceIpc();
            var message = new SingleInstanceMessage
            {
                Action = cli.IsTorrent ? "AddTorrent" : "AddUrl",
                Argument = cli.Url ?? string.Empty,
                SaveDirectory = cli.DestinationPath,
                StartImmediately = cli.StartImmediately,
                Silent = cli.Quiet,
                RawArgs = args
            };

            var forwarded = ipc.TryForwardToRunningInstanceAsync(message, timeoutMs: 1500).GetAwaiter().GetResult();
            if (forwarded)
            {
                // Request successfully forwarded to the active SwiftGet instance; exit immediately
                return;
            }
        }

        // Headless execution mode if requested (and not a BitTorrent transfer)
        if (!cli.IsTorrent && (cli.QuitAfterAdd || (cli.Quiet && !string.IsNullOrWhiteSpace(cli.Url))))
        {
            RunHeadlessAsync(cli).GetAwaiter().GetResult();
            return;
        }

        App.PendingLaunchCommand = cli;
        ComWrappersSupport.InitializeComWrappers();

        Application.Start(p =>
        {
            DispatcherQueueSynchronizationContext context = new(DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    private static async Task RunHeadlessAsync(CliCommand cli)
    {
        if (string.IsNullOrWhiteSpace(cli.Url))
        {
            return;
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appDir = Path.Combine(localAppData, "SwiftGet");
        Directory.CreateDirectory(appDir);

        await using var db = new SqliteDatabase(Path.Combine(appDir, "swiftget.db"));
        using var categoryManager = new CategoryManager(db);
        await categoryManager.InitializeAsync().ConfigureAwait(false);

        var resolver = new SwiftGet.Core.IO.FileNameResolver();
        var fileName = !string.IsNullOrWhiteSpace(cli.FileName)
            ? cli.FileName
            : resolver.ResolveInitialCandidate(cli.Url);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "download.bin";

        var category = categoryManager.ResolveCategory(fileName);
        var targetDir = cli.DestinationPath;
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            targetDir = category.TargetDirectory;
        }
        Directory.CreateDirectory(targetDir);

        var destinationPath = Path.Combine(targetDir, fileName);

        var item = new DownloadItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Url = cli.Url,
            FileName = fileName,
            DestinationPath = destinationPath,
            QueueId = cli.QueueName ?? "default",
            CategoryId = category.Id,
            Status = cli.StartImmediately ? DownloadStatus.Downloading : DownloadStatus.Queued,
            CreatedAtUtc = DateTime.UtcNow
        };

        await db.SaveDownloadAsync(item).ConfigureAwait(false);

        if (cli.StartImmediately)
        {
            using var httpClient = new HttpClient();
            var coordinator = new DownloadCoordinator(httpClient, db);
            await coordinator.StartAsync(item).ConfigureAwait(false);
        }
    }
}
