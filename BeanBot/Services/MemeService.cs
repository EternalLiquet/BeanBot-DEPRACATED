using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;

namespace BeanBot.Services;

public interface IMemeService
{
    Task<MemeResult?> GetMemeAsync(string? subreddit, CancellationToken cancellationToken = default);
}

public sealed record MemeResult(string Title, string SubReddit, string ImageUrl);

public sealed class MemeService(HttpClient httpClient, ILogger<MemeService> logger) : IMemeService
{
    public async Task<MemeResult?> GetMemeAsync(string? subreddit, CancellationToken cancellationToken = default)
    {
        try
        {
            var requestPath = BuildRequestPath(subreddit);
            using var response = await httpClient.GetAsync(requestPath, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogDebug("Meme API returned 404 for subreddit {Subreddit}", subreddit);
                return null;
            }

            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<MemeApiResponse>(cancellationToken: cancellationToken);
            if (payload is null ||
                string.IsNullOrWhiteSpace(payload.Title) ||
                string.IsNullOrWhiteSpace(payload.Subreddit) ||
                !Uri.TryCreate(payload.Url, UriKind.Absolute, out var imageUrl))
            {
                logger.LogWarning("Meme API returned an unexpected payload for subreddit {Subreddit}", subreddit);
                return null;
            }

            return new MemeResult(payload.Title, payload.Subreddit, imageUrl.AbsoluteUri);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Meme API request failed for subreddit {Subreddit}", subreddit);
            return null;
        }
    }

    private static string BuildRequestPath(string? subreddit)
    {
        if (string.IsNullOrWhiteSpace(subreddit))
        {
            return "gimme";
        }

        var normalizedSubreddit = subreddit.Trim().TrimStart('/');
        if (normalizedSubreddit.StartsWith("r/", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSubreddit = normalizedSubreddit[2..];
        }

        return $"gimme/{Uri.EscapeDataString(normalizedSubreddit)}";
    }

    private sealed class MemeApiResponse
    {
        public string? Title { get; init; }

        public string? Subreddit { get; init; }

        public string? Url { get; init; }
    }
}
