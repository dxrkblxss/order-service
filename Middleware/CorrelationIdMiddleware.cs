using System.Diagnostics;

namespace OrderService.Middleware;

public class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderNameConst = "X-Correlation-ID";
    public const string ItemsKeyConst = "CorrelationId";

    public static readonly string[] IncomingHeaderNames = [HeaderNameConst, "X-Request-ID", "Request-Id"];

    private readonly RequestDelegate _next = next;
    private readonly ILogger<CorrelationIdMiddleware> _logger = logger;
    private const string HeaderName = HeaderNameConst;
    private const string ItemsKey = ItemsKeyConst;

    public async Task InvokeAsync(HttpContext context)
    {
        string? correlationId = null;
        foreach (var hn in IncomingHeaderNames)
        {
            if (context.Request.Headers.TryGetValue(hn, out var provided) && !string.IsNullOrWhiteSpace(provided))
            {
                correlationId = provided.ToString();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = Guid.NewGuid().ToString();

        context.Items[ItemsKey] = correlationId;
        if (!context.Response.Headers.ContainsKey(HeaderName))
            context.Response.Headers[HeaderName] = correlationId;

        context.TraceIdentifier = correlationId;
        if (Activity.Current != null)
        {
            try
            {
                Activity.Current.SetTag("correlation_id", correlationId);
            }
            catch
            {
                // ignore any activity exceptions
            }
        }

        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            _logger.LogDebug("Assigned correlation id {CorrelationId} to request {Method} {Path}", correlationId, context.Request.Method, context.Request.Path);
            await _next(context);
        }
    }
}
