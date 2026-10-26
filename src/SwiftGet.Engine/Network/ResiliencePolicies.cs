using System.Net;
using Polly;
using Polly.Retry;

namespace SwiftGet.Engine.Network;

public static class ResiliencePolicies
{
    public static ResiliencePipeline<HttpResponseMessage> CreateHttpRetryPipeline(int maxRetries = 5, TimeSpan? initialDelay = null)
    {
        var delay = initialDelay ?? TimeSpan.FromMilliseconds(500);

        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = maxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = delay,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<Exception>(ex =>
                    {
                        var category = ErrorClassifier.Classify(ex, out _);
                        return category is DownloadErrorCategory.Transient or DownloadErrorCategory.RateLimited;
                    })
                    .HandleResult(response =>
                    {
                        var category = ErrorClassifier.ClassifyResponse(response, out _);
                        return category is DownloadErrorCategory.Transient or DownloadErrorCategory.RateLimited;
                    }),
                DelayGenerator = static args =>
                {
                    if (args.Outcome.Result != null)
                    {
                        ErrorClassifier.ClassifyResponse(args.Outcome.Result, out var retryAfter);
                        if (retryAfter.HasValue)
                        {
                            return new ValueTask<TimeSpan?>(retryAfter.Value);
                        }
                    }
                    return new ValueTask<TimeSpan?>((TimeSpan?)null);
                }
            })
            .Build();
    }
}
