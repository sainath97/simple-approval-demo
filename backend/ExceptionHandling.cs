using Microsoft.AspNetCore.Mvc;

namespace ApprovalDemo.Api;

public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        try { await next(context); }
        catch (ApiValidationException ex)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new ProblemDetails { Title = "Validation failed", Detail = ex.Message, Status = 400 });
        }
        catch (ApiConflictException ex)
        {
            context.Response.StatusCode = 409;
            await context.Response.WriteAsJsonAsync(new ProblemDetails { Title = "Conflict", Detail = ex.Message, Status = 409 });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled request error");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new ProblemDetails { Title = "Unexpected server error", Status = 500 });
        }
    }
}
