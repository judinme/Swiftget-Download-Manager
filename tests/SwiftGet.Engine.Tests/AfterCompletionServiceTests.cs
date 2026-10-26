using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Automation;
using Xunit;

namespace SwiftGet.Engine.Tests;

public class AfterCompletionServiceTests
{
    [Fact]
    public async Task ExecuteAction_None_ShouldReturnTrue()
    {
        var service = new AfterCompletionService();
        var result = await service.ExecuteActionAsync(AfterCompletionAction.None);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAction_ExitApp_ShouldRaiseExitAppRequestedEvent()
    {
        var service = new AfterCompletionService();
        var eventRaised = false;
        service.ExitAppRequested += (s, e) => eventRaised = true;

        var result = await service.ExecuteActionAsync(AfterCompletionAction.ExitApp);
        result.Should().BeTrue();
        eventRaised.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAction_ExecuteCommand_ShouldRunCommandSuccessfully()
    {
        var service = new AfterCompletionService();
        var cmd = "cmd.exe /c exit 0";

        var result = await service.ExecuteActionAsync(AfterCompletionAction.ExecuteCommand, cmd);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAction_ExecuteCommand_WithQuotes_ShouldParseCorrectly()
    {
        var service = new AfterCompletionService();
        var cmd = "\"cmd.exe\" /c exit 0";

        var result = await service.ExecuteActionAsync(AfterCompletionAction.ExecuteCommand, cmd);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAction_ExecuteCommand_EmptyCommand_ShouldReturnFalse()
    {
        var service = new AfterCompletionService();
        var result = await service.ExecuteActionAsync(AfterCompletionAction.ExecuteCommand, "");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAction_Shutdown_WhenDeclinedByConfirmation_ReturnsFalse()
    {
        var service = new AfterCompletionService();
        service.ConfirmActionAsync = (action, countdown, ct) => Task.FromResult(false);

        var result = await service.ExecuteActionAsync(AfterCompletionAction.Shutdown);
        result.Should().BeFalse("action must be aborted when user confirmation returns false");
    }

    [Fact]
    public async Task ExecuteAction_ExecuteCommand_WithShellMetas_Blocked()
    {
        var service = new AfterCompletionService();
        var dangerousCommand = "calc.exe & notepad.exe";

        var result = await service.ExecuteActionAsync(AfterCompletionAction.ExecuteCommand, dangerousCommand);
        result.Should().BeFalse("commands with chained shell metas must be blocked");
    }
}
