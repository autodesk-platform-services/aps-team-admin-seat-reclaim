using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

/// <summary>Turns APS and report errors into plain-text responses the browser can show.</summary>
public class ApsExceptionFilter : IExceptionFilter
{
    private readonly ILogger<ApsExceptionFilter> _logger;

    public ApsExceptionFilter(ILogger<ApsExceptionFilter> logger)
    {
        _logger = logger;
    }

    public void OnException(ExceptionContext context)
    {
        var (status, message) = context.Exception switch
        {
            ApsApiException aps => (aps.StatusCode, aps.Message),
            InvalidDataException data => (400, data.Message),
            System.Text.Json.JsonException json => (400, $"Invalid JSON: {json.Message}"),
            _ => (0, null as string)
        };
        if (message == null)
        {
            return; // unexpected: let the default handling apply
        }
        _logger.LogWarning(context.Exception, "Request failed with {Status}", status);
        context.Result = new ContentResult { StatusCode = status, Content = message, ContentType = "text/plain; charset=utf-8" };
        context.ExceptionHandled = true;
    }
}
