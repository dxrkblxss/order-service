using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.HttpOverrides;
using OrderService.Data;
using OrderService.Models;
using OrderService.Middleware;
using OrderService.Extensions;
using OrderService.DTOs;

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

        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Running in {Environment}", builder.Environment.EnvironmentName);

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
                logger = services.GetRequiredService<ILogger<Program>>();
                logger.LogError(ex, "An error occurred while migrating the database.");
            }
        }

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.UseCors("AllowAll");

        app.MapGet("/health", (HttpContext ctx) => Results.Ok(new { correlation_id = ctx.GetCorrelationId() }));

        app.MapGet("/", async (AppDbContext db, IDistributedCache cache, HttpContext ctx) =>
        {
            var correlationId = ctx.GetCorrelationId();

            var cached = await cache.GetStringAsync(AllOrdersKey);
            if (!string.IsNullOrEmpty(cached))
            {
                var cachedObj = JsonSerializer.Deserialize<object>(cached);
                return Results.Ok(new { data = cachedObj, correlation_id = correlationId });
            }

            var orders = await db.Orders
                .Include(o => o.Items)
                .ToListAsync();
            var jsonData = JsonSerializer.Serialize(orders);
            await cache.SetStringAsync(AllOrdersKey, jsonData, cacheOptions);

            return Results.Ok(new { data = orders, correlation_id = correlationId });
        });

        app.MapGet("/{id}", async (Guid id, AppDbContext db, IDistributedCache cache, HttpContext ctx) =>
        {
            var correlationId = ctx.GetCorrelationId();

            var cachedOrder = await cache.GetStringAsync(OrderKey(id));

            if (!string.IsNullOrEmpty(cachedOrder))
            {
                var cached = JsonSerializer.Deserialize<object>(cachedOrder);
                return Results.Ok(new { data = cached, correlation_id = correlationId });
            }

            var order = await db.Orders
                .Include(o => o.Items)
                .FirstOrDefaultAsync(d => d.Id == id);
            if (order is null)
                return Results.NotFound();

            var jsonData = JsonSerializer.Serialize(order);

            await cache.SetStringAsync(OrderKey(id), jsonData, cacheOptions);

            return Results.Ok(new { data = order, correlation_id = correlationId });
        });

        app.MapPost("/", async (CreateOrderRequest request, AppDbContext db, IHttpClientFactory clientFactory, IDistributedCache cache, HttpContext ctx) =>
        {
            var correlationId = ctx.GetCorrelationId();
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

                var wrapper = await response.Content.ReadFromJsonAsync<ServiceResponse<DishDto>>(new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                var dish = wrapper?.Data;

                if (dish is null || dish.PriceOptions is null)
                    return Results.BadRequest($"Данные о блюде {itemRequest.DishId} повреждены или отсутствуют.");

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

            return Results.Created($"/orders/{order.Id}", new { data = order, correlation_id = correlationId });
        });

        app.MapDelete("/{id}", async (Guid id, AppDbContext db, IDistributedCache cache, HttpContext ctx) =>
        {
            var affected = await db.Orders
                .Where(x => x.Id == id)
                .ExecuteDeleteAsync();

            if (affected > 0)
            {
                await cache.RemoveAsync(OrderKey(id));
                await cache.RemoveAsync(AllOrdersKey);
            }

            if (affected == 0) return Results.NotFound();

            return Results.NoContent();
        });

        app.MapPost("/{id}/pay", async (Guid id, AppDbContext db, IDistributedCache cache, HttpContext ctx) =>
        {
            var order = await db.Orders.FindAsync(id);
            if (order is null) return Results.NotFound();

            try
            {
                order.MarkAsPaid();
                await db.SaveChangesAsync();

                await cache.RemoveAsync(OrderKey(id));
                await cache.RemoveAsync(AllOrdersKey);

                return Results.Ok(new { Status = order.Status.ToString(), correlation_id = ctx.GetCorrelationId() });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.Run();
    }
}
