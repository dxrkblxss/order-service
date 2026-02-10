using Microsoft.AspNetCore.Mvc;
using OrderService.Middleware;

namespace OrderService.Extensions;

public static class HttpContextExtensions
{
    private const string HeaderName = CorrelationIdMiddleware.HeaderNameConst;
    private const string ItemsKey = CorrelationIdMiddleware.ItemsKeyConst;

    public static string GetCorrelationId(this HttpContext ctx)
    {
        if (ctx == null) return string.Empty;
        if (ctx.Items.TryGetValue(ItemsKey, out var v) && v is string s && !string.IsNullOrEmpty(s))
            return s;

        if (ctx.Request.Headers.TryGetValue(HeaderName, out var header) && !string.IsNullOrEmpty(header))
            return header.ToString();

        return ctx.TraceIdentifier ?? string.Empty;
    }

    public static IResult Problem(this HttpContext ctx, int statusCode, string message, string? title = null)
    {
        var correlationId = ctx.GetCorrelationId();

        var problem = new ProblemDetails
        {
            Title = title ?? "Internal Server Error",
            Detail = message,
            Status = statusCode
        };

        problem.Extensions["correlation_id"] = correlationId;

        if (!string.IsNullOrEmpty(correlationId))
            ctx.Response.Headers[HeaderName] = correlationId;

        return Results.Problem(problem);
    }
}
