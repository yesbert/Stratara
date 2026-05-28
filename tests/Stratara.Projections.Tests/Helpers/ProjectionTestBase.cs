using Moq;
using Stratara.Projections.Multitenancy.Repositories;
using Stratara.Abstractions.Persistence;

namespace Stratara.Projections.Tests.Helpers;

public abstract class ProjectionTestBase
{
    protected Mock<IProjectionsUnitOfWork> UnitOfWorkMock { get; }
    protected Mock<ITransaction> TransactionMock { get; }
    protected Mock<ITenantRepository> TenantRepositoryMock { get; }

    protected ProjectionTestBase()
    {
        TransactionMock = new Mock<ITransaction>();
        TransactionMock.Setup(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        TenantRepositoryMock = new Mock<ITenantRepository>();

        UnitOfWorkMock = new Mock<IProjectionsUnitOfWork>();
        UnitOfWorkMock.Setup(u => u.StartAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TransactionMock.Object);
        UnitOfWorkMock.Setup(u => u.CreateTenantRepository(It.IsAny<ITransaction>()))
            .Returns(TenantRepositoryMock.Object);
    }
}
