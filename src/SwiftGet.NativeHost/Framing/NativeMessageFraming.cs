using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SwiftGet.NativeHost.Framing;

public static class NativeMessageFraming
{
    public const int MaxMessageSizeBytes = 1024 * 1024; // 1 MB WebExtension limit

    public static async Task<string?> ReadMessageAsync(Stream input, CancellationToken ct = default)
    {
        var lengthBuffer = new byte[4];
        var bytesRead = await ReadExactAsync(input, lengthBuffer, 0, 4, ct).ConfigureAwait(false);
        if (bytesRead == 0)
        {
            return null; // Stream closed
        }

        if (bytesRead < 4)
        {
            throw new EndOfStreamException("Incomplete length header received from native messaging stream.");
        }

        var messageLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (messageLength < 0 || messageLength > MaxMessageSizeBytes)
        {
            throw new InvalidDataException($"Native message length {messageLength} exceeds max allowed {MaxMessageSizeBytes} bytes.");
        }

        var messageBuffer = new byte[messageLength];
        var payloadBytesRead = await ReadExactAsync(input, messageBuffer, 0, messageLength, ct).ConfigureAwait(false);
        if (payloadBytesRead < messageLength)
        {
            throw new EndOfStreamException($"Expected {messageLength} bytes for native message body, but received {payloadBytesRead}.");
        }

        return Encoding.UTF8.GetString(messageBuffer);
    }

    public static async Task WriteMessageAsync(Stream output, object payload, CancellationToken ct = default)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        if (jsonBytes.Length > MaxMessageSizeBytes)
        {
            throw new InvalidDataException($"Outgoing message size {jsonBytes.Length} exceeds {MaxMessageSizeBytes} bytes.");
        }

        var lengthBuffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, jsonBytes.Length);

        await output.WriteAsync(lengthBuffer, ct).ConfigureAwait(false);
        await output.WriteAsync(jsonBytes, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }

        return totalRead;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };
}
