using Microsoft.Extensions.Logging;
using Stratara.Shared.Diagnostics.Extensions;

namespace Stratara.Shared.Tests.Diagnostics;

public class LoggerScopeExtensionsTests
{
    private readonly Mock<ILogger> _loggerMock = new();

    public LoggerScopeExtensionsTests()
    {
        _loggerMock
            .Setup(l => l.BeginScope(It.IsAny<It.IsAnyType>()))
            .Returns(new Mock<IDisposable>().Object);
    }

    [Fact]
    public void BeginCreateAggregateScope_CallsBeginScopeAndReturnsDisposable()
    {
        var result = _loggerMock.Object.BeginCreateAggregateScope(Guid.NewGuid());

        Assert.NotNull(result);
        _loggerMock.Verify(l => l.BeginScope(It.IsAny<It.IsAnyType>()), Times.Once);
    }

    [Fact]
    public void BeginUpdateAggregateScope_CallsBeginScopeAndReturnsDisposable()
    {
        var result = _loggerMock.Object.BeginUpdateAggregateScope(Guid.NewGuid());

        Assert.NotNull(result);
        _loggerMock.Verify(l => l.BeginScope(It.IsAny<It.IsAnyType>()), Times.Once);
    }
}
