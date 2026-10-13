using Kyuplan.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Kyuplan.Api.Accounts;

/// <summary>
/// The user accounts: a database of its own (SQLite, or PostgreSQL through <see cref="AccountsPostgresDbContext"/>) (<c>Accounts:ConnectionString</c>), apart from
/// <see cref="Persistence.PlannerDbContext"/>, so that logins and password hashes are kept, backed up and protected separately.
/// </summary>
public class AccountsDbContext : DbContext
{
    public AccountsDbContext(DbContextOptions<AccountsDbContext> options) : base(options)
    {
    }

    /// <summary>For <see cref="AccountsPostgresDbContext"/>.</summary>
    protected AccountsDbContext(DbContextOptions options) : base(options)
    {
    }

    public const int UserNameLength = Contracts.AccountRules.MaxUserNameLength;

    public DbSet<UserAccount> Users => Set<UserAccount>();

    public DbSet<UserAlias> UserAliases => Set<UserAlias>();

    public DbSet<Role> Roles => Set<Role>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserAccount>(user =>
        {
            // The unique index and every lookup by name use this collation, so "Anna" and "anna" are one user.
            user.Property(u => u.UserName).HasMaxLength(UserNameLength).UseCollation(PostgresModel.NoCase);
            user.HasIndex(u => u.UserName).IsUnique();
            user.Property(u => u.DisplayName).HasMaxLength(Contracts.AccountRules.MaxDisplayNameLength);
            user.Property(u => u.WebTheme).HasMaxLength(Contracts.ThemeRules.MaxLength);
            user.Property(u => u.WindowsTheme).HasMaxLength(Contracts.ThemeRules.MaxLength);
            user.Property(u => u.Language).HasMaxLength(Contracts.LanguageRules.MaxLength);
            user.HasMany(u => u.Roles).WithMany(r => r.Users).UsingEntity("UserRoles");
        });

        modelBuilder.Entity<UserAlias>(alias =>
        {
            alias.Property(a => a.Alias).HasMaxLength(UserNameLength).UseCollation(PostgresModel.NoCase);
            alias.HasIndex(a => a.Alias).IsUnique();
            alias.HasOne<UserAccount>().WithMany(u => u.Aliases).HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Role>(role =>
        {
            role.Property(r => r.Name).HasMaxLength(Contracts.RoleRules.MaxNameLength).UseCollation(PostgresModel.NoCase);
            role.HasIndex(r => r.Name).IsUnique();
        });

        if (Database.IsNpgsql()) PostgresModel.Adapt(modelBuilder);
    }
}

/// <summary><see cref="AccountsDbContext"/> on PostgreSQL: the same model, with migrations of its own (<c>Accounts/PostgresMigrations</c>).</summary>
public sealed class AccountsPostgresDbContext(DbContextOptions<AccountsPostgresDbContext> options) : AccountsDbContext(options);
