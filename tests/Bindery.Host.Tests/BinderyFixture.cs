using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bindery.Host.Tests;

/// <summary>
/// A Bindery host wired to a stub plugin, a temporary library, and a temporary database.
/// </summary>
/// <remarks>
/// The host runs its real pipeline — real migrations, real EF Core over a real SQLite file,
/// real background workers — because the interesting failures in this codebase live in the
/// seams between those, not inside any one of them. Only authentication is dialled down,
/// via the same <c>Auth:Mode=None</c> switch a developer uses locally.
/// </remarks>
public sealed class BinderyFixture : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string _root = string.Empty;

    public StubPlugin Plugin { get; private set; } = default!;

    public HttpClient Client { get; private set; } = default!;

    public string LibraryPath => Path.Combine(_root, "library");

    public IServiceProvider Services => _factory!.Services;

    public async Task InitializeAsync()
    {
        Plugin = await StubPlugin.StartAsync();

        _root = Path.Combine(Path.GetTempPath(), "bindery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(LibraryPath);
        Directory.CreateDirectory(Path.Combine(_root, "data"));

        var settings = new Dictionary<string, string?>
        {
            ["Bindery:Auth:Mode"] = "None",
            ["Bindery:DataPath"] = Path.Combine(_root, "data"),
            ["Bindery:LibraryPath"] = LibraryPath,
            ["Bindery:Downloads:Concurrency"] = "1",
            ["Bindery:Downloads:RetryDelay"] = "00:00:01",
            ["Bindery:Plugins:Token"] = "test-token",
            ["Bindery:Plugins:Registry:0:Name"] = Plugin.Name,
            ["Bindery:Plugins:Registry:0:Enabled"] = "true",
            ["Bindery:Plugins:Registry:0:BaseUrl"] = Plugin.BaseUrl
        };

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("ENVIRONMENT", Environments.Development);

            foreach (var (key, value) in settings)
            {
                host.UseSetting(key, value);
            }
        });

        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // The registry is filled by a background service; nothing routes until it has run
        // once, so waiting here is the difference between a flaky suite and a real one.
        await WaitForPluginAsync();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await Plugin.DisposeAsync();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory that outlives the test run is not worth failing it over.
        }
    }

    public IServiceScope CreateScope() => Services.CreateScope();

    private async Task WaitForPluginAsync()
    {
        var registry = Services.GetRequiredService<Plugins.PluginRegistry>();

        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (registry.Find(Plugin.Name)?.IsUsable == true)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException("the stub plugin never became usable");
    }

    /// <summary>Polls a job until it reaches a terminal state, or gives up loudly.</summary>
    public async Task<JsonElementJob> WaitForJobAsync(Guid id, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = await Client.GetFromJsonAsync<JsonElementJob>($"/api/downloads/{id}");

            if (job is not null && job.Status is "succeeded" or "unchanged" or "failed" or "cancelled")
            {
                return job;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"job {id} never finished");
    }
}

public sealed record JsonElementJob(
    Guid Id,
    string Url,
    string? Plugin,
    string Status,
    double Percent,
    string? Message,
    string? ErrorCode,
    string? ErrorMessage,
    Guid? BookId);

[CollectionDefinition(Name)]
public sealed class BinderyCollection : ICollectionFixture<BinderyFixture>
{
    public const string Name = "bindery-host";
}
