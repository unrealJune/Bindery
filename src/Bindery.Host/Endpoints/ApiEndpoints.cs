using System.Security.Claims;
using Bindery.Host.Catalog;
using Bindery.Host.Downloads;
using Bindery.Host.Library;
using Bindery.Host.Plugins;
using Bindery.Host.Security;
using Microsoft.AspNetCore.Mvc;

namespace Bindery.Host.Endpoints;

/// <summary>
/// The JSON API. Thin by design: every handler here calls a service and shapes a response.
/// </summary>
public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapApi(this IEndpointRouteBuilder builder)
    {
        var api = builder.MapGroup("/api")
            .RequireAuthorization(AuthPolicies.UseUi)
            .WithTags("API");

        // ------------------------------------------------------------ downloads

        api.MapPost("/downloads", async (
            [FromBody] EnqueueRequest request,
            DownloadService downloads,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var result = await downloads.EnqueueAsync(request.Url, user.Identity?.Name, request.Plugin, ct);

            return result.Accepted
                ? Results.Created($"/api/downloads/{result.Job!.Id}", JobView.From(result.Job))
                : Results.BadRequest(new { error = result.Problem });
        });

        api.MapGet("/downloads", async (DownloadService downloads, int? limit, CancellationToken ct) =>
            Results.Ok((await downloads.RecentAsync(Math.Clamp(limit ?? 50, 1, 500), ct))
                .Select(job => JobView.From(job))));

        api.MapGet("/downloads/{id:guid}", async (Guid id, DownloadService downloads, CancellationToken ct) =>
        {
            var job = await downloads.FindAsync(id, ct);
            return job is null ? Results.NotFound() : Results.Ok(JobView.From(job, includeLog: true));
        });

        api.MapDelete("/downloads/{id:guid}", async (Guid id, DownloadService downloads, CancellationToken ct) =>
            await downloads.CancelAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/downloads/{id:guid}/retry", async (Guid id, DownloadService downloads, CancellationToken ct) =>
            await downloads.RetryAsync(id, ct) ? Results.Accepted() : Results.NotFound());

        // ------------------------------------------------------------ books

        api.MapGet("/books", async (CatalogService catalog, string? q, int? page, int? size, CancellationToken ct) =>
        {
            var pageSize = Math.Clamp(size ?? 24, 1, 200);
            var index = Math.Max(0, page ?? 0);

            var result = string.IsNullOrWhiteSpace(q)
                ? await catalog.RecentAsync(index, pageSize, ct)
                : await catalog.SearchAsync(q, index, pageSize, ct);

            return Results.Ok(new
            {
                total = result.Total,
                page = result.PageIndex,
                pageCount = result.PageCount,
                items = result.Items.Select(BookView.From)
            });
        });

        api.MapGet("/books/{id:guid}", async (Guid id, CatalogService catalog, CancellationToken ct) =>
        {
            var book = await catalog.FindAsync(id, ct);
            return book is null ? Results.NotFound() : Results.Ok(BookView.From(book));
        });

        api.MapDelete("/books/{id:guid}", async (Guid id, LibraryWriter writer, CancellationToken ct) =>
            await writer.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/books/{id:guid}/refresh", async (
            Guid id,
            DownloadService downloads,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var result = await downloads.EnqueueUpdateAsync(id, user.Identity?.Name, ct);

            return result.Accepted
                ? Results.Accepted($"/api/downloads/{result.Job!.Id}", JobView.From(result.Job))
                : Results.BadRequest(new { error = result.Problem });
        });

        // ------------------------------------------------------------ library

        api.MapPost("/library/scan", async (LibraryScanner scanner, CancellationToken ct) =>
            Results.Ok(await scanner.ScanAsync(ct)));

        // The form is read off HttpContext rather than bound with [FromForm]: minimal-API
        // form binding demands the antiforgery middleware, which Bindery does not run
        // because Razor Pages and the plugin proxy each handle their own token.
        api.MapPost("/library/upload", async (HttpContext context, LibraryWriter writer, CancellationToken ct) =>
        {
            if (!context.Request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Expected a multipart form with a 'file' part." });
            }

            var form = await context.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();

            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { error = "No file was uploaded." });
            }

            await using var content = file.OpenReadStream();

            var result = await writer.ImportAsync(
                content, file.FileName, form["title"], form["author"], ct);

            return result.Accepted
                ? Results.Created($"/api/books/{result.BookId}", new
                {
                    id = result.BookId,
                    title = result.Title,
                    alreadyHeld = result.AlreadyHeld
                })
                : Results.BadRequest(new { error = result.Problem });
        });

        // ------------------------------------------------------------ plugins

        api.MapGet("/plugins", (PluginRegistry registry) =>
            Results.Ok(registry.All.Select(PluginView.From)));

        api.MapPost("/plugins/{name}/refresh", async (string name, PluginRegistry registry, CancellationToken ct) =>
        {
            var descriptor = registry.Find(name);

            if (descriptor is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(PluginView.From(await registry.RefreshAsync(descriptor.Entry, ct)));
        });

        api.MapPost("/plugins/{name}/actions/{action}", async (
            string name,
            string action,
            [FromBody] Dictionary<string, string> input,
            PluginActionService actions,
            CancellationToken ct) =>
            Results.Ok(await actions.InvokeAsync(name, action, input, ct)));

        return builder;
    }
}

public sealed record EnqueueRequest(string Url, string? Plugin);

public sealed record JobView(
    Guid Id,
    string Url,
    string? Plugin,
    string Status,
    double Percent,
    string? Message,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    int Attempts,
    string? ErrorCode,
    string? ErrorMessage,
    Guid? BookId,
    IReadOnlyList<JobLogView>? Log)
{
    public static JobView From(Data.DownloadJobEntity job, bool includeLog = false) =>
        new(
            job.Id,
            job.Url,
            job.PluginName,
            job.Status.ToString().ToLowerInvariant(),
            Math.Round(job.Percent, 1),
            job.Message,
            job.CreatedAt,
            job.CompletedAt,
            job.Attempts,
            job.ErrorCode,
            job.ErrorMessage,
            job.BookId,
            includeLog ? [.. job.Log.Select(entry => new JobLogView(entry.At, entry.Level, entry.Message))] : null);
}

public sealed record JobLogView(DateTimeOffset At, string Level, string Message);

public sealed record BookView(
    Guid Id,
    string Title,
    IReadOnlyList<string> Authors,
    string? Series,
    double? SeriesIndex,
    string? Summary,
    IReadOnlyList<string> Tags,
    int? Chapters,
    string? SourceUrl,
    string? SourcePlugin,
    DateTimeOffset Added,
    DateTimeOffset Updated,
    bool HasCover,
    IReadOnlyList<BookFileView> Files)
{
    public static BookView From(Core.Domain.Book book) =>
        new(
            book.Id,
            book.Title,
            [.. book.Authors.AsList().Select(author => author.Name)],
            book.Series.OrNull()?.Name,
            book.Series.OrNull()?.Index.OrNullable(),
            book.Summary.OrNull(),
            [.. book.Tags.AsList()],
            book.Chapters.OrNullable(),
            book.SourceUrl.OrNull(),
            book.SourcePlugin.OrNull(),
            book.Added,
            book.Updated,
            book.HasCover,
            [.. book.Files.AsList().Select(file => new BookFileView(file.Format, file.SizeBytes))]);
}

public sealed record BookFileView(string Format, long SizeBytes);

public sealed record PluginView(
    string Name,
    string DisplayName,
    string? Version,
    bool Healthy,
    string? Error,
    DateTimeOffset? RefreshedAt,
    int Patterns,
    string UiMode,
    IReadOnlyList<string> Actions)
{
    public static PluginView From(PluginDescriptor descriptor) =>
        new(
            descriptor.Name,
            descriptor.DisplayName,
            descriptor.Manifest?.Version,
            descriptor.IsUsable,
            descriptor.Error,
            descriptor.RefreshedAt,
            descriptor.Compiled?.Patterns.Length ?? 0,
            descriptor.Manifest?.Ui.Mode.Wire ?? "declarative",
            [.. (descriptor.Manifest?.Actions.AsList() ?? []).Select(action => action.Name)]);
}
