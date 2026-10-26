using FluentAssertions;
using Xunit;

namespace SwiftGet.Engine.Tests;

public class EngineSmokeTests
{
    [Fact]
    public void EngineMarker_ShouldReturnValidVersion()
    {
        EngineMarker.EngineVersion.Should().NotBeNullOrWhiteSpace();
        Core.Constants.ApplicationName.Should().Contain("SwiftGet");
    }
}
