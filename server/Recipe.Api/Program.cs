using System.Text.Json.Serialization;
using Asp.Versioning;
using Recipe.Api.Auth;
using Recipe.Api.Data.App;
using Recipe.Api.Middleware;
using Recipe.Api.OpenApi;
using Recipe.Api.Services.Classification;
using Recipe.Api.Services.Extraction;
using Recipe.Api.Services.Import;
using Recipe.Api.Services.Metadata;
using Recipe.Api.Services.Queue;
using Recipe.Api.Services.Recipes;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Development and Testing get a stub authentication scheme so authorized endpoints are
// reachable from the Swagger UI and the test suite without a real token. Production must
// only ever see JWT bearer.
var useDevAuth = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        // Enums go over the wire as strings. Their underlying numeric values are still
        // part of the contract once the app ships — see server/CLAUDE.md.
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services
    .AddApiVersioning(options =>
    {
        options.DefaultApiVersion = new ApiVersion(1, 0);
        options.AssumeDefaultVersionWhenUnspecified = true;
        options.ReportApiVersions = true;
    })
    .AddMvc()
    .AddApiExplorer(options =>
    {
        options.GroupNameFormat = "'v'VVV";
        options.SubstituteApiVersionInUrl = true;
    })
    .AddOpenApi();

// Registered against the same document name the versioned OpenAPI setup produces, so it
// augments that document rather than declaring a second one (which is what AV0029 warns
// about). Adds worked example payloads for the Swagger UI "Try it out" box.
builder.Services.Configure<Microsoft.AspNetCore.OpenApi.OpenApiOptions>(
    "v1", options => options.AddSchemaTransformer<ImportExampleTransformer>());

// Postgres in production, SQLite in tests. Registration is skipped under the Testing
// environment so the test fixture can supply the SQLite provider itself: EF allows only
// one database provider per service container, and registering Npgsql here would
// conflict with it no matter what the fixture removes afterwards.
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("AppDb")));
}

if (useDevAuth)
{
    builder.Services
        .AddAuthentication(DevAuthenticationHandler.SchemeName)
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DevAuthenticationHandler>(
            DevAuthenticationHandler.SchemeName, _ => { });
}
else
{
    builder.Services.AddAuthentication().AddJwtBearer();
}

builder.Services.AddAuthorization();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IImportService, ImportService>();
builder.Services.AddScoped<IRecipeService, RecipeService>();
builder.Services.AddScoped<IMetadataService, MetadataService>();
builder.Services.AddScoped<IClassificationService, ClassificationService>();

builder.Services.AddHttpClient<IClassifierClient, ClassifierClient>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Sidecar:BaseUrl"] ?? "http://localhost:8000");
    // A hundred captions in one call; generous, but nothing like a video download.
    client.Timeout = TimeSpan.FromMinutes(3);
});

// Redis backs the job queue. The test fixture replaces both registrations, so a missing
// Redis only matters for a real run.
if (!builder.Environment.IsEnvironment("Testing"))
{
    var redis = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
    builder.Services.AddSingleton<IConnectionMultiplexer>(
        _ => ConnectionMultiplexer.Connect(redis));
    builder.Services.AddSingleton<IJobQueue, RedisJobQueue>();
    builder.Services.AddHostedService<QueueWorker>();
}

// Follows share-sheet short links to the post they point at. Redirects are the whole
// point here, so the handler must be allowed to follow them.
builder.Services.AddHttpClient<IShortLinkResolver, ShortLinkResolver>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = true,
    MaxAutomaticRedirections = 5
});

builder.Services.AddHttpClient<IOEmbedClient, OEmbedClient>(client =>
{
    client.BaseAddress = new Uri("https://www.tiktok.com");
    client.Timeout = TimeSpan.FromSeconds(20);
});

// Transcription runs at roughly a fifth of realtime, so the timeout is generous by
// design: a three-minute video is a ~35 second call before the model even runs.
builder.Services.AddHttpClient<ISidecarClient, SidecarClient>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Sidecar:BaseUrl"] ?? "http://localhost:8000");
    client.Timeout = TimeSpan.FromMinutes(10);
});

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().WithDocumentPerVersion();

    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Recipe API v1");
        options.RoutePrefix = "swagger";
        options.DocumentTitle = "Recipe API";
    });
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

/// <summary>Exposed so the test project can drive the app with WebApplicationFactory.</summary>
public partial class Program;
