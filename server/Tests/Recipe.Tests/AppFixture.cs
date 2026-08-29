using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Recipe.Api.Data.App;
using Recipe.Api.Services.Extraction;
using Recipe.Api.Services.Classification;
using Recipe.Api.Services.Metadata;
using Recipe.Api.Services.Queue;

namespace Recipe.Tests;

/// <summary>
/// Boots the real API against an in-memory SQLite database. The connection is held open
/// for the fixture's lifetime because SQLite drops an in-memory database as soon as the
/// last connection closes.
/// </summary>
public class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private DbConnection? _connection;

    /// <summary>
    /// Matches the API's serializer so assertions on response JSON see the same enum
    /// representation the app will.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Program.cs registers no provider under the Testing environment, so this is
            // the only DbContext registration in the container.
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));

            // The Python sidecar needs media, Whisper, and a model call. Tests drive a
            // stub instead, so the cascade is exercised without any of them. Registered
            // as a singleton so a test can set the next outcome and read what was sent.
            services.RemoveAll<ISidecarClient>();
            services.AddSingleton<StubSidecar>();
            services.AddSingleton<ISidecarClient>(sp => sp.GetRequiredService<StubSidecar>());

            // Same for TikTok's oEmbed endpoint: tests decide what the platform returns.
            services.RemoveAll<IOEmbedClient>();
            services.AddSingleton<StubOEmbed>();
            services.AddSingleton<IOEmbedClient>(sp => sp.GetRequiredService<StubOEmbed>());

            // And for following share-sheet short links.
            services.RemoveAll<IShortLinkResolver>();
            services.AddSingleton<StubShortLinkResolver>();
            services.AddSingleton<IShortLinkResolver>(sp => sp.GetRequiredService<StubShortLinkResolver>());

            // Program.cs registers neither of these under Testing, so there is no Redis
            // connection to replace and no background worker racing the assertions.
            services.AddSingleton<StubJobQueue>();
            services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<StubJobQueue>());

            services.RemoveAll<IClassifierClient>();
            services.AddSingleton<StubClassifier>();
            services.AddSingleton<IClassifierClient>(sp => sp.GetRequiredService<StubClassifier>());
        });
    }

    /// <summary>
    /// Creates a context in its own DI scope. Dispose it before asserting on data the
    /// API wrote, so you read through a fresh change tracker.
    /// </summary>
    public AppDbContext CreateDbContext()
    {
        var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }

    Task IAsyncLifetime.InitializeAsync()
    {
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
        return Task.CompletedTask;
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public override async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        await base.DisposeAsync();
    }
}
