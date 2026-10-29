using FastEndpoints;
using FastEndpoints.Swagger;
using FluentMigrator.Runner;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using StackExchange.Redis;
using System.Threading.RateLimiting;
using UrlShortener.Api;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Data;
using UrlShortener.Api.Health;
using UrlShortener.Api.Urls;

Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

// --- App:Role ---
var roleStr = builder.Configuration["App:Role"] ?? "All";
if (!Enum.TryParse<AppRole>(roleStr, ignoreCase: true, out var appRole))
    throw new InvalidOperationException($"App:Role '{roleStr}' is invalid. Valid values: All, Write, Read.");

// --- Connection strings ---
var writeCs = builder.Configuration.GetConnectionString("Write") ?? "";
var readCs = builder.Configuration.GetConnectionString("Read") ?? "";
var redisCs = builder.Configuration.GetConnectionString("Redis") ?? "";

// --- Health checks (always register all three) ---
builder.Services.AddHealthChecks()
    .AddCheck("postgres-write", new PostgresHealthCheck(writeCs))
    .AddCheck("postgres-read", new PostgresHealthCheck(readCs))
    .AddCheck("redis", new RedisHealthCheck(redisCs));

// --- Database ---
builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IUrlRepository, UrlRepository>();

// --- Migrations ---
if (!string.IsNullOrEmpty(writeCs))
{
    builder.Services.AddFluentMigratorCore()
        .ConfigureRunner(r => r
            .AddPostgres()
            .WithGlobalConnectionString(writeCs)
            .ScanIn(typeof(Program).Assembly).For.Migrations())
        .AddLogging(l => l.AddFluentMigratorConsole());
}

// --- Redis (optional) ---
if (!string.IsNullOrEmpty(redisCs))
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        ConnectionMultiplexer.Connect(redisCs));
}

// --- Short code generator ---
builder.Services.AddSingleton<ShortCodeGenerator>();

// --- Analytics ---
builder.Services.AddSingleton<ClickChannel>();
builder.Services.AddHostedService<ClickBackgroundService>();

// --- Auth ---
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromDays(1);
        o.SlidingExpiration = false;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

// --- Rate limiting ---
builder.Services.AddRateLimiter(o =>
{
    o.AddFixedWindowLimiter("api", opts =>
    {
        opts.PermitLimit = 100;
        opts.Window = TimeSpan.FromMinutes(1);
        opts.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opts.QueueLimit = 0;
    });
    o.RejectionStatusCode = 429;
});

// --- FastEndpoints ---
builder.Services.AddFastEndpoints(o =>
{
    o.Filter = t =>
    {
        if (t.Namespace?.Contains(".Health") == true) return true;
        if (t.Namespace?.Contains(".Redirect") == true) return true;

        return appRole switch
        {
            AppRole.Write => t.Namespace?.Contains(".Users") == true ||
                             t.Namespace?.Contains(".Urls") == true,
            AppRole.Read  => t.Namespace?.Contains(".Redirect") == true,
            _             => true
        };
    };
});

// --- Swagger ---
builder.Services.SwaggerDocument(o =>
{
    o.DocumentSettings = s =>
    {
        s.Title = "URL Shortener API";
        s.Version = "v1";
    };
});

var app = builder.Build();

// --- Run migrations ---
if (!string.IsNullOrEmpty(writeCs))
{
    using var scope = app.Services.CreateScope();
    try
    {
        var runner = scope.ServiceProvider.GetService<IMigrationRunner>();
        runner?.MigrateUp();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("Migration skipped at startup: {Type}", ex.GetType().Name);
    }
}

app.UseMiddleware<TimingMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseFastEndpoints(c =>
{
    c.Errors.UseProblemDetails();
});
app.UseSwaggerGen();

app.Run();

public partial class Program { }
