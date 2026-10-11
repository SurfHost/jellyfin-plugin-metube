using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.MeTube.Web;

/// <summary>
/// Adds the MeTube client script to Jellyfin Web's index.html by patching the HTTP
/// response, never the file on disk (the web root is read-only for a non-root
/// container, as on TrueNAS).
/// <para>
/// Unlike GuestPass this wraps the normal response instead of answering on its
/// own, so it composes with other plugins that inject into the same page
/// (GuestPass, Jellyfin Enhanced, JavaScript Injector): whichever runs first, the
/// next one sees and patches the already-patched page. To avoid the trap that
/// made GuestPass short-circuit (a revalidating browser getting a 304 with no
/// body to patch), the conditional and range request headers are removed before
/// the request continues, and the patched response carries no ETag or
/// Last-Modified, so the browser cannot revalidate its way back to an unpatched
/// copy. Accept-Encoding is removed too, so the body arrives uncompressed.
/// </para>
/// </summary>
public sealed class WebInjectionStartupFilter : IStartupFilter
{
    private const string ScriptPath = "/MeTube/ClientScript";

    private readonly IServerConfigurationManager _config;
    private readonly WebInjectionStatus _status;
    private readonly ILogger<WebInjectionStartupFilter> _logger;

    /// <summary>Initializes a new instance of the <see cref="WebInjectionStartupFilter"/> class.</summary>
    public WebInjectionStartupFilter(
        IServerConfigurationManager config,
        WebInjectionStatus status,
        ILogger<WebInjectionStartupFilter> logger)
    {
        _config = config;
        _status = status;
        _logger = logger;
    }

    /// <summary>Inserts the script tag before the last &lt;/body&gt;, unless it is already there.</summary>
    public static string Patch(string html, string baseUrl)
    {
        if (html.Contains(ScriptPath, StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        var snippet = "<script src=\"" + baseUrl + ScriptPath + "\" defer></script>\n";
        var bodyIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return bodyIndex >= 0 ? html.Insert(bodyIndex, snippet) : html + snippet;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        _status.FilterInstalled = true;
        return app =>
        {
            app.Use(InvokeAsync);
            next(app);
        };
    }

    private async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!IsIndexRequest(context) || Plugin.Instance?.Configuration.InjectionEnabled == false)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var headers = context.Request.Headers;
        headers.Remove(HeaderNames.AcceptEncoding);
        headers.Remove(HeaderNames.Range);
        headers.Remove(HeaderNames.IfRange);
        headers.Remove(HeaderNames.IfNoneMatch);
        headers.Remove(HeaderNames.IfModifiedSince);

        var response = context.Response;
        var originalBody = response.Body;
        var buffer = new MemoryStream();
        await using (buffer.ConfigureAwait(false))
        {
            response.Body = buffer;
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                response.Body = originalBody;
            }

            byte[]? patched = null;
            try
            {
                if (response.StatusCode == StatusCodes.Status200OK
                    && (response.ContentType ?? string.Empty).StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrEmpty(response.Headers.ContentEncoding))
                {
                    var html = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                    patched = Encoding.UTF8.GetBytes(Patch(html, GetBaseUrl()));
                }
            }
            catch (Exception ex)
            {
                // This runs outside Jellyfin's exception middleware; an exception here
                // would take the whole web UI down. Serve the page unpatched instead.
                _status.LastError = ex.Message;
                _logger.LogError(ex, "MeTube: could not patch index.html; serving it unchanged.");
                patched = null;
            }

            if (patched is null)
            {
                buffer.Position = 0;
                await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            response.Headers.Remove(HeaderNames.ETag);
            response.Headers.Remove(HeaderNames.LastModified);
            response.Headers.CacheControl = "no-cache";
            response.ContentLength = patched.Length;
            await originalBody.WriteAsync(patched, context.RequestAborted).ConfigureAwait(false);

            _status.IncrementServed();
            _status.LastServedUtc = DateTimeOffset.UtcNow;
            _status.LastError = null;
        }
    }

    private bool IsIndexRequest(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return false;
        }

        // This filter wraps the whole pipeline, outside app.Map(BaseUrl), so the
        // path still carries the base URL, and it runs before UseDefaultFiles, so
        // "/web/" has not been rewritten to "/web/index.html" yet.
        var baseUrl = GetBaseUrl();
        var path = context.Request.Path.Value;
        return !string.IsNullOrEmpty(path)
            && (string.Equals(path, baseUrl + "/web/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, baseUrl + "/web/index.html", StringComparison.OrdinalIgnoreCase));
    }

    private string GetBaseUrl()
    {
        try
        {
            return (_config.GetNetworkConfiguration().BaseUrl ?? string.Empty).TrimEnd('/');
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
