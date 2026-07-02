using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace UltraBlur.Plugin.Services
{
    /// <summary>
    /// Injects the UltraBlur client &lt;script&gt; tag into jellyfin-web's
    /// index.html at request time via ASP.NET middleware (pattern from
    /// Jellyfin-Enhanced's ScriptInjectionStartupFilter).
    ///
    /// Request-time injection needs no writable web folder, survives
    /// jellyfin-web updates, and works on both Jellyfin 10.11 and 12.
    /// Defensive and additive: only touches the web index.html response,
    /// idempotent, serves the original response unchanged on any error, and
    /// can be disabled via the EnableClientScript config flag.
    /// </summary>
    public class ScriptInjectionStartupFilter : IStartupFilter
    {
        private readonly ILogger<ScriptInjectionStartupFilter> _logger;
        private int _loggedOnce;

        /// <summary>
        /// Initializes a new instance of the <see cref="ScriptInjectionStartupFilter"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        public ScriptInjectionStartupFilter(ILogger<ScriptInjectionStartupFilter> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                // Registered before the rest of the pipeline so this runs outermost —
                // stripping Accept-Encoding below then reliably yields an uncompressed
                // response we can read and rewrite.
                app.Use(InvokeAsync);
                next(app);
            };
        }

        // Matches the web app shell however it is requested: bare "/web", "/web/"
        // (SPA serve), and explicit "/web/index.html". EndsWith keeps this correct
        // when Jellyfin is hosted under a base-url prefix (e.g. /jellyfin/web/).
        private static bool IsIndexRequest(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/web", StringComparison.OrdinalIgnoreCase);
        }

        private async Task InvokeAsync(HttpContext context, Func<Task> nextMw)
        {
            if (!IsIndexRequest(context.Request.Path.Value))
            {
                await nextMw().ConfigureAwait(false);
                return;
            }

            // Only GET produces a body we can rewrite. HEAD/OPTIONS/etc. must pass
            // straight through so the host emits correct headers.
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                await nextMw().ConfigureAwait(false);
                return;
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnableClientScript)
            {
                await nextMw().ConfigureAwait(false);
                return;
            }

            // Normalize the request so the static handler returns a complete,
            // plain-text 200 we can rewrite: no compression, no partial responses.
            context.Request.Headers.Remove("Accept-Encoding");
            context.Request.Headers.Remove("Range");
            context.Request.Headers.Remove("If-Range");

            var originalBody = context.Response.Body;
            using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try
            {
                await nextMw().ConfigureAwait(false);
            }
            catch
            {
                // Downstream failure is not ours to swallow: discard the partial
                // buffer and rethrow so the host renders a clean error page.
                context.Response.Body = originalBody;
                throw;
            }

            context.Response.Body = originalBody;
            buffer.Seek(0, SeekOrigin.Begin);

            var isHtml = context.Response.StatusCode == 200
                && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);

            if (!isHtml)
            {
                // 304, redirects, non-HTML — pass straight through unchanged.
                await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
                return;
            }

            string html;
            using (var reader = new StreamReader(buffer, Encoding.UTF8, true, 1024, leaveOpen: true))
            {
                html = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            try
            {
                var alreadyInjected = html.Contains("/UltraBlur/ClientScript", StringComparison.OrdinalIgnoreCase);
                var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);

                if (!alreadyInjected && bodyClose >= 0)
                {
                    var tag = Plugin.Instance!.BuildScriptTag();
                    html = string.Concat(html.AsSpan(0, bodyClose), tag, "\n", html.AsSpan(bodyClose));

                    if (Interlocked.Exchange(ref _loggedOnce, 1) == 0)
                    {
                        _logger.LogInformation("UltraBlur: injected the client script via request-time middleware");
                    }
                }
            }
            catch (Exception ex)
            {
                // Never break index.html — serve whatever we have.
                _logger.LogWarning(ex, "UltraBlur script injection error (serving original HTML)");
            }

            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html;charset=utf-8";
            context.Response.ContentLength = bytes.Length;
            // The body changed: validators from the static-file handler no longer
            // hold, and range requests were stripped on the way in.
            context.Response.Headers.Remove("ETag");
            context.Response.Headers.Remove("Last-Modified");
            context.Response.Headers.Remove("Accept-Ranges");
            await originalBody.WriteAsync(bytes.AsMemory(0, bytes.Length)).ConfigureAwait(false);
        }
    }
}
