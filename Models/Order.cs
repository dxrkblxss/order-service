namespace OrderService.Models;

public enum OrderStatus
{
    New,
    Paid,
    Cooking,
    Delivered
}

public enum PaymentStatus
{
    NotPaid,
    Paid
}

public class Order
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public OrderStatus Status { get; private set; } = OrderStatus.New;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public decimal TotalAmount { get; set; }

    public string Address { get; set; } = string.Empty;

    public List<OrderItem> Items { get; set; } = [];

    public void MarkAsPaid()
    {
        if (Status == OrderStatus.Paid) return;
        Status = OrderStatus.Paid;
    }
}
