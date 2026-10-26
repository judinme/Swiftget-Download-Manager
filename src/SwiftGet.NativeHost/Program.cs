using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using SwiftGet.Engine.Server;
using SwiftGet.NativeHost.Framing;
using SwiftGet.NativeHost.Routing;

namespace SwiftGet.NativeHost;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // 1. Origin validation if passed via command line by browser host
        if (args.Length > 0)
        {
            var callerOrigin = args[0];
            if (!NativeMessageRouter.ValidateOrigin(callerOrigin))
            {
                await Console.Error.WriteLineAsync($"SwiftGet.NativeHost: Unauthorized caller origin: '{callerOrigin}'");
                return 1;
            }
        }

        // 2. Initialize database context
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDir = Path.Combine(localAppData, "SwiftGet");
        Directory.CreateDirectory(appDir);
        var dbPath = Path.Combine(appDir, "swiftget.db");

        await using var db = new SqliteDatabase(dbPath);
        await db.InitializeAsync().ConfigureAwait(false);
        using var categoryManager = new CategoryManager(db);
        var tokenManager = new LoopbackTokenManager();

        var router = new NativeMessageRouter(db, categoryManager, tokenManager);

        // 3. Process stdin/stdout framing loop
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var incomingJson = await NativeMessageFraming.ReadMessageAsync(stdin, cts.Token).ConfigureAwait(false);
                if (incomingJson == null)
                {
                    // EOF reached, browser closed the pipe
                    break;
                }

                var responsePayload = await router.RouteMessageAsync(incomingJson, cts.Token).ConfigureAwait(false);
                await NativeMessageFraming.WriteMessageAsync(stdout, responsePayload, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Clean exit
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"SwiftGet.NativeHost fatal error: {ex.Message}");
            return 1;
        }

        return 0;
    }
}
