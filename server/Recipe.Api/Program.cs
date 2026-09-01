using System.Text.Json.Serialization;
using Asp.Versioning;
using Recipe.Api.Auth;
using Recipe.Api.Data.App;
using Recipe.Api.Middleware;
using Recipe.Api.OpenApi;
using Recipe.Api.Services.Auth;
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

void ConfigureJwtBearer(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions options)
{
    var key = builder.Configuration["Auth:Jwt:Key"];

    options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = builder.Configuration["Auth:Jwt:Issuer"] ?? "recipe-api",
        ValidateAudience = true,
        ValidAudience = builder.Configuration["Auth:Jwt:Audience"] ?? "recipe-app",
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = string.IsNullOrWhiteSpace(key)
            ? null
            : new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                System.Text.Encoding.UTF8.GetBytes(key)),
        ClockSkew = TimeSpan.FromMinutes(1)
    };
}

if (useDevAuth)
{
    // Both schemes, chosen per request: a bearer token means a real signed-in user, and
    // anything else falls back to the stub. Without this the dev handler would swallow
    // every request and there would be no way to exercise real auth outside production.
    builder.Services
        .AddAuthentication(options =>
        {
            options.DefaultScheme = "DevOrJwt";
            options.DefaultChallengeScheme = "DevOrJwt";
        })
        .AddPolicyScheme("DevOrJwt", "Dev stub or JWT", options =>
        {
            options.ForwardDefaultSelector = context =>
                context.Request.Headers.Authorization.ToString()
                    .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme
                    : DevAuthenticationHandler.SchemeName;
        })
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DevAuthenticationHandler>(
            DevAuthenticationHandler.SchemeName, _ => { })
        .AddJwtBearer(ConfigureJwtBearer);
}
else
{
    builder.Services.AddAuthentication(
            Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(ConfigureJwtBearer);
}

builder.Services.AddAuthorization();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IImportService, ImportService>();
builder.Services.AddScoped<IRecipeService, RecipeService>();
builder.Services.AddScoped<IMetadataService, MetadataService>();
builder.Services.AddScoped<IClassificationService, ClassificationService>();
builder.Services.AddScoped<IAuthService, AuthService>();

builder.Services.AddHttpClient<IAppleTokenValidator, AppleTokenValidator>(client =>
{
    client.BaseAddress = new Uri("https://appleid.apple.com");
    client.Timeout = TimeSpan.FromSeconds(15);
});

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

// Fail at startup, not on the first sign-in. A missing signing key means the server
// cannot mint tokens at all, and a missing Apple client id means it would accept tokens
// minted for a different app entirely — both are deployment mistakes that must be loud
// and immediate rather than a 500 for the first user who tries to sign in.
if (!useDevAuth)
{
    var signingKey = builder.Configuration["Auth:Jwt:Key"];

    if (string.IsNullOrWhiteSpace(signingKey) || signingKey.Length < 32)
    {
        throw new InvalidOperationException(
            "Auth:Jwt:Key must be set to at least 32 characters outside Development.");
    }

    if (string.IsNullOrWhiteSpace(builder.Configuration["Auth:Apple:ClientId"]))
    {
        throw new InvalidOperationException(
            "Auth:Apple:ClientId must be set to the app's bundle id outside Development. "
            + "Without it, an identity token issued for any other app would be accepted.");
    }
}

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
