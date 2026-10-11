using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MeTube.Models;
using Jellyfin.Plugin.MeTube.Search;

namespace Jellyfin.Plugin.MeTube.MeTube;

/// <summary>
/// Talks to MeTube over the LAN, server to server. MeTube has no authentication;
/// its cross-origin guard only stops browsers, and these requests carry no
/// Origin header, so they pass. That is also why MeTube must never be exposed
/// beyond the LAN: the plugin's own endpoints are what Jellyfin's login protects.
/// </summary>
public sealed class MeTubeClient
{
    /// <summary>The named HttpClient for MeTube requests.</summary>
    public const string HttpClientName = "MeTube.Api";

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Initializes a new instance of the <see cref="MeTubeClient"/> class.</summary>
    public MeTubeClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Queues a YouTube video. MeTube probes the video with yt-dlp before it
    /// answers, so this can take several seconds.
    /// </summary>
    public async Task<MeTubeAddResult> AddAsync(string baseUrl, string videoId, MeTubeAddOptions options, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>
        {
            ["url"] = VideoIdParser.WatchUrl(videoId),
            ["download_type"] = "video",
            ["codec"] = options.Codec,
            ["format"] = options.Format,
            ["quality"] = options.Quality,
            ["folder"] = options.Folder,
            ["ytdl_options_presets"] = options.Presets,
            ["auto_start"] = true
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            using var response = await Client().PostAsync(BuildUri(baseUrl, "add"), JsonContent.Create(body), timeout.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return MeTubeParsers.ParseAddResponse((int)response.StatusCode, text);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // MeTube keeps working on a request whose client went away, so a timeout
            // usually still ends with the video queued.
            throw new MeTubeException("MeTube did not answer in time.", ex) { IsTimeout = true };
        }
        catch (HttpRequestException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MeTubeException("Could not reach MeTube at " + baseUrl + ".", ex);
        }
    }

    /// <summary>Reads MeTube's queue and history.</summary>
    public async Task<MeTubeHistory> GetHistoryAsync(string baseUrl, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(baseUrl, "history", TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        return MeTubeParsers.ParseHistory(document.RootElement);
    }

    /// <summary>Reads MeTube's version, as a connectivity check.</summary>
    public async Task<string> GetVersionAsync(string baseUrl, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(baseUrl, "version", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var ytdlp = root.TryGetProperty("yt-dlp", out var y) && y.ValueKind == JsonValueKind.String ? y.GetString() : null;
        return string.Format(CultureInfo.InvariantCulture, "MeTube {0}, yt-dlp {1}", version ?? "?", ytdlp ?? "?");
    }

    /// <summary>Lists the presets MeTube knows.</summary>
    public async Task<IReadOnlyList<string>> GetPresetsAsync(string baseUrl, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(baseUrl, "presets", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        return MeTubeParsers.ParsePresets(document.RootElement);
    }

    /// <summary>
    /// Searches YouTube through MeTube's yt-dlp ("ytsearchN:"). Fallback only: it is
    /// slower and returns titles and durations without channels or dates, but it
    /// uses yt-dlp's parser, which keeps working when YouTube changes its page layout.
    /// </summary>
    public async Task<SearchPage> BrowseSearchAsync(string baseUrl, string query, int count, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>
        {
            ["url"] = "ytsearch" + count.ToString(CultureInfo.InvariantCulture) + ":" + query
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            using var response = await Client().PostAsync(BuildUri(baseUrl, "browse"), JsonContent.Create(body), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new MeTubeException("MeTube search returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
            }

            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
                return MeTubeParsers.ParseBrowse(document.RootElement);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            throw new MeTubeException("MeTube search failed.", ex);
        }
    }

    /// <summary>
    /// Removes entries from MeTube's history. Files are never deleted this way: the
    /// request carries no "delete_files", so MeTube's own trashcan setting decides, and
    /// the plugin only calls this after it has deleted the files itself.
    /// </summary>
    public async Task RemoveHistoryAsync(string baseUrl, IReadOnlyCollection<string> urls, CancellationToken cancellationToken)
    {
        if (urls.Count == 0)
        {
            return;
        }

        var body = new Dictionary<string, object> { ["ids"] = urls, ["where"] = "done" };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await Client().PostAsync(BuildUri(baseUrl, "delete"), JsonContent.Create(body), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new MeTubeException("MeTube /delete returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new MeTubeException("Could not reach MeTube at " + baseUrl + ".", ex);
        }
    }

    /// <summary>Validates and normalizes the configured MeTube base URL.</summary>
    public static Uri BuildUri(string baseUrl, string path)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new MeTubeException("The MeTube URL is not configured.");
        }

        if (!Uri.TryCreate(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var root)
            || (root.Scheme != Uri.UriSchemeHttp && root.Scheme != Uri.UriSchemeHttps))
        {
            throw new MeTubeException("The MeTube URL must start with http:// or https://.");
        }

        return new Uri(root, path);
    }

    private HttpClient Client() => _httpClientFactory.CreateClient(HttpClientName);

    private async Task<JsonDocument> GetJsonAsync(string baseUrl, string path, TimeSpan timeoutAfter, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutAfter);
        try
        {
            using var response = await Client().GetAsync(BuildUri(baseUrl, path), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new MeTubeException("MeTube /" + path + " returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
            }

            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            throw new MeTubeException("Could not reach MeTube at " + baseUrl + ".", ex);
        }
    }
}
