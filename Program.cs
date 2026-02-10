using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.HttpOverrides;
using OrderService.Data;
using OrderService.Models;

namespace OrderService;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor |
                ForwardedHeaders.XForwardedHost |
                ForwardedHeaders.XForwardedProto;

            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging.AddSimpleConsole(options =>
        {
            options.IncludeScopes = true;
            options.SingleLine = false;
            options.TimestampFormat = "[HH:mm:ss]";
        });
        builder.Logging.AddDebug();

        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowAll", policy =>
                policy.AllowAnyOrigin()
                      .AllowAnyHeader()
                      .AllowAnyMethod());
        });

        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection not set");
        builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                npgsqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null);
            }));

        builder.Services.AddStackExchangeRedisCache(
            options => options.Configuration = builder.Configuration.GetConnectionString("Redis"));

        var cacheOptions = new DistributedCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(10))
                .SetSlidingExpiration(TimeSpan.FromMinutes(2));

        const string AllOrdersKey = "orders:all";
        static string OrderKey(Guid id) => $"orders:{id}";

        builder.Services.AddHttpClient("DishClient", client =>
        {
            client.BaseAddress = new Uri("http://dishservice:80");
        });

        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
        {
            options.SerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        });

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo { Title = "Orders Service API", Version = "v1" });
        });

        var app = builder.Build();

        app.UseForwardedHeaders();

        app.UseSwagger(c =>
        {
            c.PreSerializeFilters.Add((swaggerDoc, httpReq) =>
            {
                var fallbackPrefix = app.Configuration["Swagger:BasePath"]
                    ?? Environment.GetEnvironmentVariable("SWAGGER_BASEPATH")
                    ?? string.Empty;

                var prefix = httpReq.Headers["X-Forwarded-Prefix"].FirstOrDefault();
                if (string.IsNullOrEmpty(prefix))
                    prefix = fallbackPrefix;

                var scheme = httpReq.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? httpReq.Scheme;
                var host = httpReq.Headers["X-Forwarded-Host"].FirstOrDefault() ?? httpReq.Host.Value;

                var baseUrl = $"{scheme}://{host}{prefix}";
                swaggerDoc.Servers =
                [
                    new() { Url = baseUrl }
                ];
            });
        });

        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "My API V1");
            c.RoutePrefix = "swagger";
        });

        using (var scope = app.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            try
            {
                var context = services.GetRequiredService<AppDbContext>();
                context.Database.Migrate();
            }
            catch (Exception ex)
            {
                var logger = services.GetRequiredService<ILogger<Program>>();
                logger.LogError(ex, "An error occurred while migrating the database.");
            }
        }

        app.MapGet("/health", () => Results.Ok());

        app.MapGet("/", async (AppDbContext db, IDistributedCache cache) =>
        {
            var cached = await cache.GetStringAsync(AllOrdersKey);
            if (!string.IsNullOrEmpty(cached)) return Results.Text(cached, "application/json");

            var orders = await db.Orders.Include(o => o.Items).ToListAsync();
            var jsonData = JsonSerializer.Serialize(orders);
            await cache.SetStringAsync(AllOrdersKey, jsonData, cacheOptions);

            return Results.Ok(orders);
        });

        app.MapPost("/", async (CreateOrderRequest request, AppDbContext db, IHttpClientFactory clientFactory, IDistributedCache cache) =>
        {
            var client = clientFactory.CreateClient("DishClient");

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = request.UserId,
                Address = request.Address,
                CreatedAt = DateTime.UtcNow,
                Items = []
            };

            decimal totalOrderSum = 0;

            foreach (var itemRequest in request.Items)
            {
                var response = await client.GetAsync($"/{itemRequest.DishId}");
                if (!response.IsSuccessStatusCode) return Results.BadRequest($"Блюдо {itemRequest.DishId} не найдено.");

                var dish = await response.Content.ReadFromJsonAsync<DishDto>();
                if (dish is null) continue;

                if (itemRequest.Quantity <= 0)
                    return Results.BadRequest("Quantity must be greater than 0.");

                if (itemRequest.PriceOptionId == Guid.Empty)
                    return Results.BadRequest("PriceOptionId is required.");

                var option = dish.PriceOptions.FirstOrDefault(p => p.Id == itemRequest.PriceOptionId);
                if (option is null)
                    return Results.BadRequest($"Price option {itemRequest.PriceOptionId} not found for dish {itemRequest.DishId}.");

                var itemLinePrice = option.Price * itemRequest.Quantity;
                var unitLabel = string.IsNullOrWhiteSpace(option.Label)
                    ? $"{option.UnitAmount} {option.UnitOfMeasure}"
                    : option.Label;

                var orderItem = new OrderItem
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    DishId = dish.Id,
                    DishName = dish.Name ?? "Unknown",
                    PriceOptionId = option.Id,
                    UnitPriceAtOrderTime = option.Price,
                    UnitLabel = unitLabel,
                    Quantity = itemRequest.Quantity,
                    TotalLinePrice = itemLinePrice
                };

                order.Items.Add(orderItem);
                totalOrderSum += itemLinePrice;
            }

            order.TotalAmount = totalOrderSum;

            db.Orders.Add(order);
            await db.SaveChangesAsync();

            await cache.RemoveAsync("orders:all");

            return Results.Created($"/orders/{order.Id}", order);
        });

        app.MapDelete("/{id}", async (Guid id, AppDbContext db, IDistributedCache cache) =>
        {
            var affected = await db.Orders.Where(x => x.Id == id).ExecuteDeleteAsync();
            if (affected > 0)
            {
                await cache.RemoveAsync(OrderKey(id));
                await cache.RemoveAsync(AllOrdersKey);
            }
            return affected == 0 ? Results.NotFound() : Results.NoContent();
        });

        app.MapPatch("/{id}/pay", async (Guid id, AppDbContext db, IDistributedCache cache) =>
        {
            var order = await db.Orders.FindAsync(id);
            if (order is null) return Results.NotFound();

            try
            {
                order.MarkAsPaid();
                await db.SaveChangesAsync();

                await cache.RemoveAsync(OrderKey(id));
                await cache.RemoveAsync(AllOrdersKey);

                return Results.Ok(new { Status = order.Status.ToString() });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.Run();
    }
}

public record CreateOrderRequest(Guid UserId, string Address, List<OrderItemRequest> Items);
public record OrderItemRequest(Guid DishId, Guid PriceOptionId, decimal Quantity);

public record DishPriceOptionDto(Guid Id, string UnitOfMeasure, decimal UnitAmount, decimal Price, string? Label);
public record DishDto(Guid Id, string Name, string Description, List<DishPriceOptionDto> PriceOptions, string? ImageUrl);
