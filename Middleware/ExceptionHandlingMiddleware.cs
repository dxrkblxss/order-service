using Microsoft.AspNetCore.Mvc;

namespace OrderService.Middleware;

public class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    private readonly RequestDelegate _next = next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger = logger;
    private const string HeaderName = CorrelationIdMiddleware.HeaderNameConst;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            var correlationId = context.Items.TryGetValue(CorrelationIdMiddleware.ItemsKeyConst, out var v) && v is string s && !string.IsNullOrEmpty(s)
                ? s
                : (CorrelationIdMiddleware.IncomingHeaderNames.Select(hn => context.Request.Headers.TryGetValue(hn, out var hv) ? hv.ToString() : null).FirstOrDefault(h => !string.IsNullOrWhiteSpace(h))
                   ?? context.TraceIdentifier);

            using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
            {
                _logger.LogError(ex, "Unhandled exception. CorrelationId={CorrelationId}", correlationId);
            }

            var problem = new ProblemDetails
            {
                Title = "An unexpected error occurred.",
                Status = 500,
                Detail = "Internal server error"
            };
            problem.Extensions["correlation_id"] = correlationId;

            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            if (!context.Response.Headers.ContainsKey(HeaderName))
                context.Response.Headers[HeaderName] = correlationId;

            await context.Response.WriteAsJsonAsync(problem);
        }
    }
}
