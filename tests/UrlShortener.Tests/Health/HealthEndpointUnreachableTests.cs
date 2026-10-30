using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace UrlShortener.Tests.Health;

/// <summary>
/// Health tests that run with no Docker and no reachable dependency: every connection
/// string points at an address nothing listens on, or is blank.
/// </summary>
public class HealthEndpointUnreachableTests
{
    [Fact]
    public async Task Get_health_with_everything_unreachable_returns_503_naming_all_three_in_order()
    {
        using var factory = DependencyContainersFixture.CreateAllUnreachableFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await HealthEndpointTests.ReadBodyAsync(response);
        Assert.Equal("Unhealthy", body.Status);
        Assert.Equal(new[] { "postgres-write", "postgres-read", "redis" }, body.Failing);
    }

    [Fact]
    public async Task Get_health_with_everything_unreachable_responds_within_5_seconds()
    {
        using var factory = DependencyContainersFixture.CreateAllUnreachableFactory();
        using var client = factory.CreateClient();

        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetAsync("/health");
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Expected a response within 5 seconds but it took {stopwatch.Elapsed.TotalSeconds:F1}s.");
    }

    [Fact]
    public async Task Get_health_with_blank_read_connection_string_starts_and_reports_postgres_read()
    {
        using var factory = DependencyContainersFixture.CreateFactory(
            DependencyContainersFixture.UnreachablePostgres,
            "",
            DependencyContainersFixture.UnreachableRedis);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await HealthEndpointTests.ReadBodyAsync(response);
        Assert.Contains("postgres-read", body.Failing);
    }

    [Fact]
    public async Task Get_health_with_every_connection_string_blank_starts_and_reports_all_three()
    {
        using var factory = DependencyContainersFixture.CreateFactory("", "", "");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await HealthEndpointTests.ReadBodyAsync(response);
        Assert.Equal(new[] { "postgres-write", "postgres-read", "redis" }, body.Failing);
    }

    [Fact]
    public async Task Get_health_failure_body_has_only_status_and_failing_and_no_connection_details()
    {
        using var factory = DependencyContainersFixture.CreateAllUnreachableFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(raw);
        var propertyNames = document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "failing", "status" }, propertyNames);

        foreach (var forbidden in new[] { "127.0.0.1", "leakprobe_db", "Host", "Port", "Username", "Password", "Exception" })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Get_health_logs_a_warning_naming_redis_and_leaks_no_connection_details()
    {
        var captured = new CapturingLoggerProvider();
        using var factory = DependencyContainersFixture.CreateFactory(
            DependencyContainersFixture.UnreachablePostgres,
            DependencyContainersFixture.UnreachablePostgres,
            DependencyContainersFixture.UnreachableRedis,
            logging => logging.AddProvider(captured));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var entries = captured.Entries.ToArray();
        Assert.Contains(entries, e => e.Level >= LogLevel.Warning && e.Text.Contains("redis", StringComparison.Ordinal));
        foreach (var entry in entries)
        {
            Assert.DoesNotContain("127.0.0.1", entry.Text);
            Assert.DoesNotContain("leakprobe_db", entry.Text);
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/urls")]
    public async Task Get_unknown_route_returns_404_without_dependencies(string path)
    {
        using var factory = DependencyContainersFixture.CreateAllUnreachableFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Wrong_method_on_health_returns_405_or_404_without_dependencies(string method)
    {
        using var factory = DependencyContainersFixture.CreateAllUnreachableFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/health");

        var response = await client.SendAsync(request);

        Assert.True(
            response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound,
            $"Expected 405 or 404 for {method} /health but got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task Get_health_without_credentials_does_not_return_401_or_403_when_dependencies_are_down()
    {
        using var factory = DependencyContainersFixture.CreateAllUnreachableFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        Assert.Null(request.Headers.Authorization);
        Assert.Null(request.Content);

        var response = await client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private sealed record LogEntry(LogLevel Level, string Text);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IEnumerable<LogEntry> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var text = formatter(state, exception);
                if (exception is not null)
                {
                    text += " " + exception;
                }

                entries.Enqueue(new LogEntry(logLevel, text));
            }
        }
    }
}
