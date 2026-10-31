using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SwiftGet.Engine.Torrent;

public sealed record SingleInstanceMessage
{
    public string? DownloadId { get; init; }
    public string Action { get; init; } = "AddTorrent";
    public string Argument { get; init; } = string.Empty;
    public string? FileName { get; init; }
    public string? SaveDirectory { get; init; }
    public bool StartImmediately { get; init; }
    public bool Silent { get; init; }
    public string? CorrelationId { get; init; }
    public string? JobKind { get; init; }
    public string? Cookies { get; init; }
    public string? Referrer { get; init; }
    public string? UserAgent { get; init; }
    public string[]? RawArgs { get; init; }
}

public interface ISingleInstanceIpc : IAsyncDisposable
{
    string PipeName { get; }
    event EventHandler<SingleInstanceMessage>? MessageReceived;
    Task StartServerAsync(CancellationToken ct = default);
    Task<bool> TryForwardToRunningInstanceAsync(SingleInstanceMessage message, int timeoutMs = 1500, CancellationToken ct = default);
}

public sealed class SingleInstanceIpc : ISingleInstanceIpc
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _pipeName;
    private CancellationTokenSource? _serverCts;
    private Task? _listenerTask;
    private bool _isDisposed;

    public string PipeName => _pipeName;
    public event EventHandler<SingleInstanceMessage>? MessageReceived;

    public SingleInstanceIpc(string? customPipeName = null)
    {
        if (!string.IsNullOrWhiteSpace(customPipeName))
        {
            _pipeName = customPipeName;
        }
        else
        {
            _pipeName = GenerateDefaultPipeName();
        }
    }

    public static string GenerateDefaultPipeName()
    {
        string identityToken = "DefaultUser";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var sid = WindowsIdentity.GetCurrent().User?.Value;
                if (!string.IsNullOrWhiteSpace(sid))
                {
                    identityToken = sid;
                }
            }
            else
            {
                identityToken = Environment.UserName;
            }
        }
        catch
        {
            identityToken = Environment.UserName;
        }

        // Sanitize for pipe name
        identityToken = identityToken.Replace('\\', '_').Replace('/', '_');
        return $"SwiftGet_IPC_{identityToken}";
    }

    public async Task<bool> TryForwardToRunningInstanceAsync(SingleInstanceMessage message, int timeoutMs = 1500, CancellationToken ct = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            await client.ConnectAsync(linkedCts.Token).ConfigureAwait(false);

            var json = JsonSerializer.Serialize(message, JsonOpts);
            var payload = Encoding.UTF8.GetBytes(json);
            var lengthPrefix = BitConverter.GetBytes(payload.Length);

            await client.WriteAsync(lengthPrefix, linkedCts.Token).ConfigureAwait(false);
            await client.WriteAsync(payload, linkedCts.Token).ConfigureAwait(false);
            await client.FlushAsync(linkedCts.Token).ConfigureAwait(false);

            // Read ACK
            var ackBuffer = new byte[4];
            int read = await client.ReadAsync(ackBuffer, linkedCts.Token).ConfigureAwait(false);
            return read == 4 && BitConverter.ToInt32(ackBuffer, 0) == 1;
        }
        catch
        {
            // Pipe does not exist or connection failed => primary instance not running
            return false;
        }
    }

    public Task StartServerAsync(CancellationToken ct = default)
    {
        if (_serverCts != null) return Task.CompletedTask;

        _serverCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listenerTask = RunServerLoopAsync(_serverCts.Token);
        return Task.CompletedTask;
    }

    private async Task RunServerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_isDisposed)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServerStream(_pipeName);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                // Read length prefix
                var lengthBuffer = new byte[4];
                int read = await ReadExactAsync(server, lengthBuffer, 4, ct).ConfigureAwait(false);
                if (read == 4)
                {
                    int length = BitConverter.ToInt32(lengthBuffer, 0);
                    if (length is > 0 and <= 65536) // Safety limit: 64 KB
                    {
                        var payload = new byte[length];
                        read = await ReadExactAsync(server, payload, length, ct).ConfigureAwait(false);
                        if (read == length)
                        {
                            var json = Encoding.UTF8.GetString(payload);
                            var msg = JsonSerializer.Deserialize<SingleInstanceMessage>(json, JsonOpts);

                            // Send ACK
                            await server.WriteAsync(BitConverter.GetBytes(1), ct).ConfigureAwait(false);
                            await server.FlushAsync(ct).ConfigureAwait(false);

                            if (msg != null)
                            {
                                MessageReceived?.Invoke(this, msg);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore transient pipe connection errors and continue loop
            }
            finally
            {
                if (server != null)
                {
                    try { server.Dispose(); } catch { }
                }
            }
        }
    }

    private static NamedPipeServerStream CreateServerStream(string pipeName)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var currentUser = WindowsIdentity.GetCurrent().User;
                if (currentUser != null)
                {
                    var pipeSecurity = new PipeSecurity();
                    pipeSecurity.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.ReadWrite, AccessControlType.Allow));

                    return NamedPipeServerStreamAcl.Create(
                        pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        inBufferSize: 4096,
                        outBufferSize: 4096,
                        pipeSecurity);
                }
            }
            catch
            {
                // Fallback to standard creation if ACL setup fails
            }
        }

        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), ct).ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_serverCts != null)
        {
            _serverCts.Cancel();
            _serverCts.Dispose();
            _serverCts = null;
        }

        if (_listenerTask != null)
        {
            try { await _listenerTask.ConfigureAwait(false); } catch { }
            _listenerTask = null;
        }
    }
}
