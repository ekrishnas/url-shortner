using FastEndpoints;
using UrlShortener.Api.Health;
using UrlShortener.Api.Logging;

var builder = WebApplication.CreateBuilder(args);

// Write logs to <repository root>/logs/api.log (the repository root is the nearest parent holding .git).
var root = new DirectoryInfo(builder.Environment.ContentRootPath);
while (root is not null && !Directory.Exists(Path.Combine(root.FullName, ".git")))
{
    root = root.Parent;
}
var logDirectory = Path.Combine(root?.FullName ?? builder.Environment.ContentRootPath, "logs");
Directory.CreateDirectory(logDirectory);
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(logDirectory, "api.log")));

builder.Services.AddFastEndpoints();
builder.Services.AddSingleton<IDependencyCheck>(sp => new PostgresDependencyCheck("postgres-write", "Write", sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IDependencyCheck>(sp => new PostgresDependencyCheck("postgres-read", "Read", sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IDependencyCheck, RedisDependencyCheck>();

var app = builder.Build();
app.UseFastEndpoints();
app.Run();

public partial class Program { }
