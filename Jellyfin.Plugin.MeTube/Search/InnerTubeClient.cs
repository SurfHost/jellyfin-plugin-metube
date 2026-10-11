using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MeTube.Models;

namespace Jellyfin.Plugin.MeTube.Search;

/// <summary>Settings for one InnerTube request, taken from the plugin configuration.</summary>
public sealed record InnerTubeSettings(string ClientVersion, string Language, string Region);

/// <summary>YouTube answered with a non-success HTTP status.</summary>
public sealed class YouTubeHttpException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="YouTubeHttpException"/> class.</summary>
    public YouTubeHttpException(int statusCode)
        : base("YouTube returned HTTP " + statusCode + ".")
    {
        StatusCode = statusCode;
    }

    /// <summary>Gets the HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>Gets a value indicating whether YouTube is rate-limiting or blocking this server.</summary>
    public bool IsThrottled => StatusCode is 429 or 403;
}

/// <summary>
/// Calls YouTube's internal web API, the same endpoints youtube.com, yt-dlp and
/// NewPipe use. No API key and no cookies are sent apart from a "reject all"
/// consent cookie, which keeps EU servers from being redirected to the consent page.
/// </summary>
public sealed class InnerTubeClient
{
    /// <summary>The named HttpClient for YouTube requests.</summary>
    public const string HttpClientName = "MeTube.YouTube";

    private const string BaseUrl = "https://www.youtube.com/youtubei/v1/";
    private const string VideosOnlyFilter = "EgIQAfABAQ==";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Initializes a new instance of the <see cref="InnerTubeClient"/> class.</summary>
    public InnerTubeClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Runs a video search, or loads the next page when a continuation token is given.</summary>
    public async Task<SearchPage> SearchAsync(string? query, string? continuation, InnerTubeSettings settings, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object> { ["context"] = Context(settings) };
        if (!string.IsNullOrEmpty(continuation))
        {
            body["continuation"] = continuation;
        }
        else
        {
            body["query"] = query ?? string.Empty;
            body["params"] = VideosOnlyFilter;
        }

        using var document = await PostAsync("search", body, settings, cancellationToken).ConfigureAwait(false);
        return InnerTubeParser.ParseSearch(document.RootElement);
    }

    /// <summary>Looks up one video's length and live status.</summary>
    public async Task<VideoInfo> GetVideoInfoAsync(string videoId, InnerTubeSettings settings, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>
        {
            ["context"] = Context(settings),
            ["videoId"] = videoId
        };

        using var document = await PostAsync("player", body, settings, cancellationToken).ConfigureAwait(false);
        return InnerTubeParser.ParsePlayer(document.RootElement, videoId);
    }

    private static Dictionary<string, object> Context(InnerTubeSettings settings)
    {
        var client = new Dictionary<string, object>
        {
            ["clientName"] = "WEB",
            ["clientVersion"] = settings.ClientVersion,
            ["hl"] = string.IsNullOrWhiteSpace(settings.Language) ? "en" : settings.Language
        };

        if (!string.IsNullOrWhiteSpace(settings.Region))
        {
            client["gl"] = settings.Region.ToUpperInvariant();
        }

        return new Dictionary<string, object> { ["client"] = client };
    }

    private async Task<JsonDocument> PostAsync(string endpoint, Dictionary<string, object> body, InnerTubeSettings settings, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + endpoint + "?prettyPrint=false")
        {
            Content = JsonContent.Create(body)
        };

        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Cookie", "SOCS=CAE=");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", settings.ClientVersion);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new YouTubeHttpException((int)response.StatusCode);
        }

        var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        }
    }
}
