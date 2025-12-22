using LapTrinhMang.Models;
using Microsoft.EntityFrameworkCore;

namespace LapTrinhMang.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<TableEntity> Tables => Set<TableEntity>();
    public DbSet<ReservationEntity> Reservations => Set<ReservationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // User: phone unique
        modelBuilder.Entity<AppUser>()
            .HasIndex(x => x.Phone)
            .IsUnique();

        // Table: number unique
        modelBuilder.Entity<TableEntity>()
            .HasIndex(t => t.Number)
            .IsUnique();

        // Reservation -> Table
        modelBuilder.Entity<ReservationEntity>()
            .HasOne(r => r.Table)
            .WithMany()
            .HasForeignKey(r => r.TableId)
            .OnDelete(DeleteBehavior.Restrict);

        // Reservation -> User (optional)
        modelBuilder.Entity<ReservationEntity>()
            .HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Seed 20 bàn
        var tables = Enumerable.Range(1, 20).Select(i => new TableEntity
        {
            Id = i,
            Number = i,
            Capacity = (i <= 10 ? 4 : 6)
        });
        modelBuilder.Entity<TableEntity>().HasData(tables);
    }
}
