namespace OrderService.Models;

public class OrderItem
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public Guid DishId { get; set; }

    public Guid? PriceOptionId { get; set; }

    public string DishName { get; set; } = string.Empty;

    public decimal Quantity { get; set; }

    public decimal UnitPriceAtOrderTime { get; set; }

    public string UnitLabel { get; set; } = string.Empty;

    public decimal TotalLinePrice { get; set; }
}