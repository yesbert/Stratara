using Microsoft.EntityFrameworkCore;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.Tests;

public class TestWriteDbContext(DbContextOptions<TestWriteDbContext> options)
    : WriteDbContext<TestWriteDbContext>(options)
{
}