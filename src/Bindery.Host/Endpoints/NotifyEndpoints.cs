using System.Net.Http.Headers;
using Bindery.Host.Configuration;
using Bindery.Host.Data;
using Bindery.Host.Downloads;
using Bindery.Host.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bindery.Host.Endpoints;

/// <summary>
/// Protocol §3.7: the one endpoint a plugin calls on Bindery.
/// </summary>
/// <remarks>
/// Every other exchange in the protocol is host-pull. This inverts the direction, so it is
/// kept as small as a useful endpoint can be: it carries no content, names one source, and
/// its entire effect is "maybe queue an update sooner than the schedule would have".
///
/// Because it inverts the direction it is also the only inbound surface a plugin can reach,
/// so the checks are deliberately blunt. The caller's identity comes from its token and
/// never from the body. A plugin can only ever cause a refresh of books it is already the
/// source of. The result is idempotent and rate-limited, so the worst a compromised or buggy
/// plugin achieves is making Bindery re-download its own books — which it can already cause
/// by reporting new chapters.
/// </remarks>
public static class NotifyEndpoints
{
    public static IEndpointRouteBuilder MapPluginNotify(this IEndpointRouteBuilder builder)
    {
        // Anonymous by design: the caller is a plugin, not a signed-in person, and it
        // authenticates with the token from X-Bindery-Notify-Token. It is not reachable from
        // the internet in the default deployment — the advertised URL is loopback.
        builder.MapPost("/bindery/v1/notify", async (
            [FromBody] NotifyRequest request,
            HttpContext http,
            PluginNotifyTokens tokens,
            BinderyDbContext db,
            DownloadService downloads,
            IOptions<BinderyOptions> options,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Bindery.Host.Endpoints.Notify");
            var plugin = tokens.Resolve(BearerOf(http));

            if (plugin is null)
            {
                return Results.Json(
                    new { error = new { code = "auth_required", message = "missing or invalid notification token" } },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            // The body's `plugin` is a claim, checked against the token's identity. A
            // mismatch is a plugin reaching for another's books, so it is refused outright
            // rather than quietly coerced to the token's own name.
            if (!string.Equals(request.Plugin, plugin, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "plugin {Plugin} sent a notification claiming to be {Claimed}", plugin, request.Plugin);

                return Results.Json(
                    new { error = new { code = "auth_required", message = "token does not match the named plugin" } },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            if (string.IsNullOrWhiteSpace(request.SourceUrl))
            {
                return Results.BadRequest(new { error = new { code = "internal", message = "sourceUrl is required" } });
            }

            var book = await db.Books.FirstOrDefaultAsync(
                candidate => candidate.SourcePlugin == plugin && candidate.SourceUrl == request.SourceUrl, ct);

            if (book is null)
            {
                // Routine, not an error: a plugin may track works Bindery has never filed.
                logger.LogDebug("notification from {Plugin} named an unfiled source", plugin);
                return Results.Ok(new NotifyResponse(false, "no filed book has that source"));
            }

            var settings = options.Value.Updates;
            var since = DateTimeOffset.UtcNow - settings.NotifyDebounce;

            var recent = await db.Jobs.AnyAsync(
                job => job.BookId == book.Id
                       && (job.CreatedAt >= since
                           || job.Status == JobStatus.Queued
                           || job.Status == JobStatus.Running),
                ct);

            if (recent)
            {
                // A burst of arrivals collapses to one update: the run already queued will
                // pick up everything ingested since, because the plugin rebuilds from its
                // whole store rather than from a delta.
                return Results.Ok(new NotifyResponse(false, "an update for this book is already queued or was just run"));
            }

            var result = await downloads.EnqueueUpdateAsync(book.Id, requestedBy: $"plugin:{plugin}", ct);

            if (result.Job is null)
            {
                return Results.Ok(new NotifyResponse(false, result.Problem ?? "could not queue an update"));
            }

            logger.LogInformation(
                "{Plugin} reported a change to {Title} ({Reason}); queued job {Job}",
                plugin, book.Title, string.IsNullOrWhiteSpace(request.Reason) ? "no reason given" : request.Reason,
                result.Job.Id);

            return Results.Accepted($"/api/downloads/{result.Job.Id}", new NotifyResponse(true, null));
        })
        .AllowAnonymous()
        .WithTags("Plugins");

        return builder;
    }

    private static string? BearerOf(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();

        return AuthenticationHeaderValue.TryParse(header, out var parsed)
               && string.Equals(parsed.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            ? parsed.Parameter
            : null;
    }

    public sealed record NotifyRequest(string Plugin, string SourceUrl, string? Reason);

    public sealed record NotifyResponse(bool Accepted, string? Reason);
}
