using Microsoft.EntityFrameworkCore;
using OrderService.Models;

namespace OrderService.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderHistory> OrderHistories => Set<OrderHistory>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSnakeCaseNamingConvention();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>().Property(o => o.TotalAmount).HasPrecision(18, 2);
        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.Property(oi => oi.Quantity).HasPrecision(18, 3);
            entity.Property(oi => oi.UnitPriceAtOrderTime).HasPrecision(18, 4);
            entity.Property(oi => oi.TotalLinePrice).HasPrecision(18, 4);
        });

        modelBuilder.Entity<Order>()
            .HasMany(o => o.Items)
            .WithOne()
            .HasForeignKey(i => i.OrderId);

        modelBuilder.Entity<OrderHistory>()
            .HasOne<Order>()
            .WithMany()
            .HasForeignKey(h => h.OrderId);
    }
}
