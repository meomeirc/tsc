using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TSC;

/// <summary>Checks the latest stable GitHub release without blocking the game loop.</summary>
internal static class UpdateCheckService
{
    private const string Repository = "meomeirc/tsc";
    private const string LatestReleaseUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private static readonly object Sync = new();
    private static readonly HttpClient Client = CreateClient();
    private static bool _started;
    private static bool _finished;
    private static bool _updateAvailable;
    private static string? _latestTag;
    private static string? _releaseHtmlUrl;

    public static string StatusText
    {
        get
        {
            lock (Sync)
            {
                if (!_started) return string.Empty;
                if (!_finished) return "Checking for updates";
                return _updateAvailable ? "Update available" : string.Empty;
            }
        }
    }

    public static bool UpdateAvailable
    {
        get { lock (Sync) return _finished && _updateAvailable; }
    }

    public static string? LatestTag
    {
        get { lock (Sync) return _latestTag; }
    }

    public static string? ReleaseHtmlUrl
    {
        get { lock (Sync) return _releaseHtmlUrl; }
    }

    public static string RepositoryName => Repository;

    public static void Start()
    {
        lock (Sync)
        {
            if (_started) return;
            _started = true;
        }

        _ = Task.Run(CheckAsync);
    }

    private static async Task CheckAsync()
    {
        try
        {
            using var response = await Client.GetAsync(LatestReleaseUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return;

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() : null;
            var htmlUrl = root.TryGetProperty("html_url", out var urlValue) ? urlValue.GetString() : null;
            var isPrerelease = root.TryGetProperty("prerelease", out var prereleaseValue) && prereleaseValue.ValueKind == JsonValueKind.True;
            var current = ParseVersion(GameVersion.Current);
            var latest = ParseVersion(tag);
            if (isPrerelease || current is null || latest is null)
                return;

            lock (Sync)
            {
                _latestTag = tag;
                _releaseHtmlUrl = htmlUrl;
                _updateAvailable = latest > current;
            }
        }
        catch
        {
            // An update check is optional. Offline or rate-limited users should still
            // be able to play without a warning popup or a persistent error label.
        }
        finally
        {
            lock (Sync) _finished = true;
        }
    }

    public static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V')) normalized = normalized[1..];
        // Release tags are expected to be stable numeric versions. This strips an
        // optional prerelease/build suffix only so malformed tags never crash the check.
        normalized = Regex.Match(normalized, @"^\d+(?:\.\d+){1,3}").Value;
        return Version.TryParse(normalized, out var version) ? version : null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TSC", GameVersion.Current));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
