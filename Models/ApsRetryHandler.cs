using System.Net;

/// <summary>
/// Retries APS calls that were throttled (429, honoring Retry-After) or hit a transient server error (5xx).
/// Team Admin allows 60 requests per minute per operation per client ID; Insights limits are per user.
/// </summary>
public class ApsRetryHandler : DelegatingHandler
{
    private const int MaxThrottleAttempts = 5;
    private const int MaxServerErrorAttempts = 3;
    private static readonly TimeSpan RetryAfterFallback = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryAfterMax = TimeSpan.FromSeconds(120);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Buffer the body so the request can be re-sent.
        if (request.Content != null)
        {
            await request.Content.LoadIntoBufferAsync(cancellationToken);
        }

        int throttled = 0, serverErrors = 0;
        while (true)
        {
            var response = await base.SendAsync(request, cancellationToken);
            TimeSpan delay;
            if (response.StatusCode == HttpStatusCode.TooManyRequests && ++throttled < MaxThrottleAttempts)
            {
                delay = GetRetryAfter(response) ?? RetryAfterFallback;
            }
            else if ((int)response.StatusCode >= 500 && ++serverErrors < MaxServerErrorAttempts)
            {
                delay = TimeSpan.FromSeconds(Math.Pow(2, serverErrors));
            }
            else
            {
                return response;
            }
            response.Dispose();
            await Task.Delay(delay < RetryAfterMax ? delay : RetryAfterMax, cancellationToken);
        }
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }
        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }
}
