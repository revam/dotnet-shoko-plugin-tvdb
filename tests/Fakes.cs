using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using NJsonSchema;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Config.Enums;
using Shoko.Abstractions.Config.Events;
using Shoko.Abstractions.Config.Services;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.User;

// The host's interface declares events this fake has no reason to raise.
#pragma warning disable CS0067

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// Just enough configuration service to hand a <see cref="ConfigurationProvider{TConfig}"/>
/// back one configuration instance. Everything else throws rather than
/// returning a plausible default, so a future call into the host that these
/// tests do not model announces itself instead of quietly passing.
/// </summary>
internal sealed class FakeConfigurationService(TvdbConfiguration configuration) : IConfigurationService
{
    public event EventHandler<ConfigurationSavedEventArgs>? Saved;

    /// <summary>Raises <see cref="Saved"/>, as a save through the WebUI would.</summary>
    public void RaiseSaved() => Saved?.Invoke(this, new() { ConfigurationInfo = null! });

    public event EventHandler<ConfigurationRequiresRestartEventArgs>? RequiresRestart;

    public IReadOnlyDictionary<Guid, IReadOnlySet<string>> RestartPendingFor => throw new NotSupportedException();

    public IReadOnlyDictionary<Guid, IReadOnlySet<string>> LoadedEnvironmentVariables => throw new NotSupportedException();

    public void AddParts(IEnumerable<Type> configurationTypes) => throw new NotSupportedException();

    public string SerializeWithMasking(IConfiguration config) => throw new NotSupportedException();

    public string MaskSecrets(ConfigurationInfo info, string json) => throw new NotSupportedException();

    public string RestoreMaskedSecrets(ConfigurationInfo info, string json) => throw new NotSupportedException();

    public ConfigurationProvider<TConfig> CreateProvider<TConfig>() where TConfig : class, IConfiguration, new() => new(this);

    public IEnumerable<ConfigurationInfo> GetAllConfigurationInfos() => throw new NotSupportedException();

    public IReadOnlyList<ConfigurationInfo> GetConfigurationInfo(IPlugin plugin) => throw new NotSupportedException();

    public ConfigurationInfo? GetConfigurationInfo(Guid configurationId) => throw new NotSupportedException();

    public ConfigurationInfo? GetConfigurationInfo(Type type) => throw new NotSupportedException();

    // The provider routes Load() through the configuration's info, and the
    // info is only ever compared for identity on a Saved event this fake
    // never raises — so it need not be a real one.
    public ConfigurationInfo GetConfigurationInfo<TConfig>() where TConfig : class, IConfiguration, new() => null!;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Validate(ConfigurationInfo info, string json) => throw new NotSupportedException();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Validate(ConfigurationInfo info, IConfiguration config) => throw new NotSupportedException();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Validate<TConfig>(TConfig config) where TConfig : class, IConfiguration, new() => throw new NotSupportedException();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Validate(string json, JsonSchema schema) => throw new NotSupportedException();

    public ConfigurationActionResult PerformCustomAction(ConfigurationInfo info, IConfiguration configuration, string path, string actionID, IUser? user = null, Uri? uri = null) => throw new NotSupportedException();

    public ConfigurationActionResult PerformCustomAction<TConfig>(TConfig configuration, string path, string actionID, IUser? user = null, Uri? uri = null) where TConfig : class, IConfiguration, new() => throw new NotSupportedException();

    public ConfigurationActionResult PerformReactiveAction(ConfigurationInfo info, IConfiguration configuration, string path, ConfigurationActionType actionType, ReactiveEventType reactiveEventType = ReactiveEventType.All, IUser? user = null, Uri? uri = null) => throw new NotSupportedException();

    public ConfigurationActionResult PerformReactiveAction<TConfig>(TConfig configuration, string path, ConfigurationActionType actionType, ReactiveEventType reactiveEventType = ReactiveEventType.All, IUser? user = null, Uri? uri = null) where TConfig : class, IConfiguration, new() => throw new NotSupportedException();

    public IConfiguration New(ConfigurationInfo info) => throw new NotSupportedException();

    public TConfig New<TConfig>() where TConfig : class, IConfiguration, new() => throw new NotSupportedException();

    public IConfiguration Load(ConfigurationInfo info, bool copy = false) => configuration;

    public TConfig Load<TConfig>(bool copy = false) where TConfig : class, IConfiguration, new() => (TConfig)(object)configuration;

    public bool Save(ConfigurationInfo info, IConfiguration json) => throw new NotSupportedException();

    public bool Save(ConfigurationInfo info, string json) => throw new NotSupportedException();

    public bool Save<TConfig>() where TConfig : class, IConfiguration, new() => throw new NotSupportedException();

    public bool Save<TConfig>(TConfig config) where TConfig : class, IConfiguration, new() => throw new NotSupportedException();

    public bool Save<TConfig>(string json) where TConfig : class, IConfiguration, new() => throw new NotSupportedException();

    public string GetSchema(ConfigurationInfo info) => throw new NotSupportedException();

    public JsonSchema GenerateSchema(Type type) => throw new NotSupportedException();

    public string Serialize(IConfiguration config) => throw new NotSupportedException();

    public IConfiguration Deserialize(ConfigurationInfo info, string json) => throw new NotSupportedException();
}

/// <summary>
/// An <see cref="System.Net.Http.HttpMessageHandler"/> that fails the test if
/// it is ever asked to send a request. Used to prove that a code path with no
/// API key never reaches the network.
/// </summary>
internal sealed class UnreachableHttpMessageHandler : System.Net.Http.HttpMessageHandler
{
    protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        => throw new InvalidOperationException($"Unexpected HTTP request sent to {request.RequestUri}.");
}

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> that just records the level of
/// each entry logged, so a test can assert on how noisy a code path is
/// without pulling in a mocking framework.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<LogLevel> Entries { get; } = [];

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add(logLevel);
}

/// <summary>
/// Reads the TheTVDB fixtures in <c>tests/Fixtures</c>.
/// </summary>
/// <remarks>
/// <strong>Every fixture in that folder is hand-written, not captured.</strong>
/// TheTVDB v4 requires an API key for all access and none was available when
/// this plugin was written, so the fixtures reproduce the shapes documented at
/// <c>thetvdb.github.io/v4-api</c> rather than real responses. They prove the
/// plugin does what it means to do with those shapes; they cannot prove the
/// shapes are right. Replace them with real captures once a key is available.
/// </remarks>
internal static class Fixture
{
    public static string Read(string fileName)
        => System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));
}

/// <summary>
/// One request the stub handler saw.
/// </summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Uri">
/// The absolute URI, unescaped by nothing: <c>AbsoluteUri</c> rather than
/// <c>ToString()</c>, because the latter unescapes for display and would hide
/// whether a value was escaped on the way out.
/// </param>
/// <param name="Body">The request body, or an empty string.</param>
/// <param name="Authorization">The Authorization header, or null.</param>
internal readonly record struct RecordedRequest(string Method, string Uri, string Body, string? Authorization);

/// <summary>
/// An <see cref="System.Net.Http.HttpMessageHandler"/> that replays a fixed
/// queue of responses, recording what it was asked for. Used to drive the API
/// client over the fixtures without touching the network.
/// </summary>
internal sealed class StubHttpMessageHandler : System.Net.Http.HttpMessageHandler
{
    private readonly Queue<(System.Net.HttpStatusCode StatusCode, string Body, string ContentType, TimeSpan? RetryAfter)> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public List<string> Uris => [.. Requests.Select(request => request.Uri)];

    public StubHttpMessageHandler Enqueue(System.Net.HttpStatusCode statusCode, string body, string contentType = "application/json", TimeSpan? retryAfter = null)
    {
        _responses.Enqueue((statusCode, body, contentType, retryAfter));
        return this;
    }

    protected override async System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new(request.Method.Method, request.RequestUri?.AbsoluteUri ?? "", body, request.Headers.Authorization?.ToString()));
        if (!_responses.TryDequeue(out var response))
            throw new InvalidOperationException($"No stubbed response left for {request.RequestUri}.");

        var message = new System.Net.Http.HttpResponseMessage(response.StatusCode)
        {
            Content = new System.Net.Http.StringContent(response.Body, System.Text.Encoding.UTF8, response.ContentType),
        };
        if (response.RetryAfter is { } retryAfter)
            message.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);

        return message;
    }
}
