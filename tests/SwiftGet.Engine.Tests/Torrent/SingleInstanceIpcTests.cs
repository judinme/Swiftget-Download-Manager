using System;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.Engine.Torrent;
using Xunit;

namespace SwiftGet.Engine.Tests.Torrent;

public class SingleInstanceIpcTests
{
    [Fact]
    public async Task TryForward_WhenNoServerRunning_ReturnsFalse()
    {
        var customPipe = $"SwiftGet_Test_Pipe_{Guid.NewGuid():N}";
        await using var ipc = new SingleInstanceIpc(customPipe);

        var msg = new SingleInstanceMessage
        {
            Action = "AddTorrent",
            Argument = "magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709"
        };

        var forwarded = await ipc.TryForwardToRunningInstanceAsync(msg, timeoutMs: 100);

        forwarded.Should().BeFalse("Forwarding should immediately return false when no instance is listening.");
    }

    [Fact]
    public async Task TryForward_WhenServerRunning_TransfersMessageAndReceivesAck()
    {
        var customPipe = $"SwiftGet_Test_Pipe_{Guid.NewGuid():N}";
        await using var serverIpc = new SingleInstanceIpc(customPipe);

        SingleInstanceMessage? receivedMsg = null;
        var tcs = new TaskCompletionSource<SingleInstanceMessage>();

        serverIpc.MessageReceived += (s, e) =>
        {
            receivedMsg = e;
            tcs.TrySetResult(e);
        };

        await serverIpc.StartServerAsync();

        // Second instance
        await using var clientIpc = new SingleInstanceIpc(customPipe);
        var msg = new SingleInstanceMessage
        {
            Action = "AddTorrent",
            Argument = "magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=TestPayload",
            StartImmediately = true
        };

        var forwarded = await clientIpc.TryForwardToRunningInstanceAsync(msg, timeoutMs: 3000);

        forwarded.Should().BeTrue("Server should accept connection and send ACK.");

        var received = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        received.Should().Be(tcs.Task, "Message should be delivered to server event handler.");

        receivedMsg.Should().NotBeNull();
        receivedMsg!.Action.Should().Be("AddTorrent");
        receivedMsg.Argument.Should().Be(msg.Argument);
        receivedMsg.StartImmediately.Should().BeTrue();
    }
}
