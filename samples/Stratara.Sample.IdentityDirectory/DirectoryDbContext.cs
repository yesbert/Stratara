using Microsoft.EntityFrameworkCore;
using Stratara.Identity.EntityFrameworkCore;

namespace Stratara.Sample.IdentityDirectory;

public sealed class DirectoryDbContext(DbContextOptions<DirectoryDbContext> options)
    : IdentityDirectoryDbContext<DirectoryDbContext>(options);
