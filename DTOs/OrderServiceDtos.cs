namespace OrderService.DTOs;

public record CreateOrderRequest(string Address, List<OrderItemRequest> Items);
public record OrderItemRequest(Guid DishId, Guid PriceOptionId, decimal Quantity);
public record DishPriceOptionDto(Guid Id, string UnitOfMeasure, decimal UnitAmount, decimal Price, string? Label);
public record DishDto(Guid Id, string Name, string Description, List<DishPriceOptionDto> PriceOptions, string? ImageUrl);
public record ServiceResponse<T>(T Data, string Correlation_Id);
