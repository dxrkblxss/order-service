namespace OrderService.Models;

public class OrderHistory
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }

    public OrderStatus Status { get; set; }
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;

    public string? ChangedBy { get; set; }

    public string? Comment { get; set; }
}