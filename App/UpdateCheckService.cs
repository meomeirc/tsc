using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TSC;

/// <summary>Checks stable GitHub releases and keeps a short diagnostic history.</summary>
internal static class UpdateCheckService
{
    private const string Repository = "meomeirc/tsc";
    private const string LatestReleaseUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private const int MaximumLogEntries = 8;
    private static readonly object Sync = new();
    private static readonly HttpClient Client = CreateClient();
    private static readonly List<string> RecentLogEntries = new(MaximumLogEntries);
    private static bool _startupCheckRequested;
    private static bool _isChecking;
    private static string? _lastCheckSource;
    private static bool _finished;
    private static bool _updateAvailable;
    private static string? _latestTag;
    private static string? _releaseHtmlUrl;
    private static string _summaryText = "No update check has been run yet.";

    public static string StatusText
    {
        get
        {
            lock (Sync)
            {
                if (_isChecking) return "Checking for updates";
                return _finished && _updateAvailable ? "Update available" : string.Empty;
            }
        }
    }

    public static bool IsChecking
    {
        get { lock (Sync) return _isChecking; }
    }

    public static bool UpdateAvailable
    {
        get { lock (Sync) return _finished && !_isChecking && _updateAvailable; }
    }

    /// <summary>Only startup checks may trigger the legacy automatic update prompt.</summary>
    public static bool ShouldPromptAutomatically
    {
        get { lock (Sync) return _finished && !_isChecking && _updateAvailable && _lastCheckSource == "startup"; }
    }

    public static string? LatestTag
    {
        get { lock (Sync) return _latestTag; }
    }

    public static string? ReleaseHtmlUrl
    {
        get { lock (Sync) return _releaseHtmlUrl; }
    }

    public static string SummaryText
    {
        get { lock (Sync) return _summaryText; }
    }

    public static string RepositoryName => Repository;

    public static IReadOnlyList<string> GetRecentLogEntries(int maximumEntries = MaximumLogEntries)
    {
        lock (Sync)
        {
            var count = Math.Clamp(maximumEntries, 0, MaximumLogEntries);
            if (count == 0 || RecentLogEntries.Count == 0)
                return Array.Empty<string>();

            var start = Math.Max(0, RecentLogEntries.Count - count);
            return RecentLogEntries.Skip(start).ToArray();
        }
    }

    /// <summary>Starts the one automatic check performed during game startup.</summary>
    public static void Start()
    {
        lock (Sync)
        {
            if (_startupCheckRequested)
                return;
            _startupCheckRequested = true;
        }

        BeginCheck("startup");
    }

    /// <summary>Starts a fresh check requested manually from Settings.</summary>
    public static bool CheckNow()
        => BeginCheck("manual");

    private static bool BeginCheck(string source)
    {
        lock (Sync)
        {
            if (_isChecking)
            {
                // Do not queue duplicate requests, but make the button press visible.
                AddLog("A GitHub release check is already running.");
                return false;
            }

            _isChecking = true;
            _lastCheckSource = source;
            _finished = false;
            _updateAvailable = false;
            _latestTag = null;
            _releaseHtmlUrl = null;
            _summaryText = "Checking GitHub releases…";
        }

        AddLog($"Starting {source} check (installed version {GameVersion.Current}).");
        _ = Task.Run(() => CheckAsync(source));
        return true;
    }

    private static async Task CheckAsync(string source)
    {
        try
        {
            var currentTag = GameVersion.Current;
            var current = ParseVersion(currentTag);
            if (current is null)
            {
                Complete(false, null, null, "Could not check updates: the installed version is invalid.");
                AddLog($"Check stopped: installed version '{currentTag}' is not a valid numeric version.");
                return;
            }

            AddLog("GET api.github.com/repos/meomeirc/tsc/releases/latest.");
            using var response = await Client.GetAsync(LatestReleaseUrl).ConfigureAwait(false);
            var responseCode = (int)response.StatusCode;
            var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase) ? "" : $" {response.ReasonPhrase}";
            AddLog($"GitHub responded: HTTP {responseCode}{reason}.");

            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadApiErrorMessageAsync(response).ConfigureAwait(false);
                var rateLimitRemaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues)
                    ? remainingValues.FirstOrDefault()
                    : null;
                var rateLimitReached = response.StatusCode == System.Net.HttpStatusCode.Forbidden &&
                    string.Equals(rateLimitRemaining, "0", StringComparison.Ordinal);
                var failure = rateLimitReached
                    ? "GitHub API rate limit reached. Try again later."
                    : $"GitHub release request failed: HTTP {responseCode}{(detail is null ? "." : $" — {detail}")}";
                Complete(false, null, null, failure);
                AddLog(failure);
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagValue) && tagValue.ValueKind == JsonValueKind.String
                ? tagValue.GetString()
                : null;
            var htmlUrl = root.TryGetProperty("html_url", out var urlValue) && urlValue.ValueKind == JsonValueKind.String
                ? urlValue.GetString()
                : null;
            var isPrerelease = root.TryGetProperty("prerelease", out var prereleaseValue) && prereleaseValue.ValueKind == JsonValueKind.True;
            var latest = ParseVersion(tag);

            if (isPrerelease || latest is null)
            {
                var reasonText = isPrerelease ? "The latest GitHub release is marked as a prerelease." : "GitHub returned no valid stable numeric release tag.";
                Complete(false, tag, htmlUrl, reasonText);
                AddLog(reasonText);
                return;
            }

            var available = latest > current;
            if (available)
            {
                var summary = $"Update available: {tag} (installed: {currentTag}).";
                Complete(true, tag, htmlUrl, summary);
                AddLog($"Found update {tag}; installed version is {currentTag}.");
            }
            else
            {
                var summary = $"No update available. Installed version {currentTag} is current (latest: {tag}).";
                Complete(false, tag, htmlUrl, summary);
                AddLog($"No update found: installed {currentTag}, latest stable release {tag}.");
            }
        }
        catch (TaskCanceledException ex)
        {
            var message = ex.InnerException is TimeoutException
                ? "GitHub release check timed out."
                : "GitHub release check was cancelled or timed out.";
            Complete(false, null, null, message);
            AddLog(message);
        }
        catch (HttpRequestException ex)
        {
            var message = $"Could not reach GitHub: {ShortMessage(ex.Message)}";
            Complete(false, null, null, message);
            AddLog(message);
        }
        catch (JsonException ex)
        {
            var message = $"GitHub returned an unreadable release response: {ShortMessage(ex.Message)}";
            Complete(false, null, null, message);
            AddLog(message);
        }
        catch (Exception ex)
        {
            var message = $"Update check failed: {ex.GetType().Name}: {ShortMessage(ex.Message)}";
            Complete(false, null, null, message);
            AddLog(message);
        }
        finally
        {
            lock (Sync)
            {
                _isChecking = false;
                _finished = true;
            }
            Program.LogRuntime($"UPDATE_CHECK completed source={source} available={UpdateAvailable} latest={LatestTag ?? "<none>"}");
        }
    }

    private static void Complete(bool available, string? tag, string? htmlUrl, string summary)
    {
        lock (Sync)
        {
            _updateAvailable = available;
            _latestTag = tag;
            _releaseHtmlUrl = htmlUrl;
            _summaryText = summary;
        }
    }

    private static void AddLog(string message)
    {
        var safeMessage = ShortMessage(message, 132);
        var entry = $"{DateTime.Now:HH:mm:ss}  {safeMessage}";
        lock (Sync)
        {
            RecentLogEntries.Add(entry);
            if (RecentLogEntries.Count > MaximumLogEntries)
                RecentLogEntries.RemoveRange(0, RecentLogEntries.Count - MaximumLogEntries);
        }
        Program.LogRuntime($"UPDATE_CHECK {safeMessage}");
    }

    private static async Task<string?> ReadApiErrorMessageAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
                return null;
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                return ShortMessage(message.GetString() ?? string.Empty, 120);
        }
        catch
        {
            // A missing or non-JSON error body should not hide the HTTP status.
        }
        return null;
    }

    private static string ShortMessage(string value, int maximumLength = 140)
    {
        var compact = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        if (compact.Length > maximumLength)
            compact = compact[..Math.Max(0, maximumLength - 1)] + "…";
        return compact;
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
