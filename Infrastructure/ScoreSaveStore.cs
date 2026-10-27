using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TSC;

/// <summary>
/// Persistent score records for normal (main-menu) gameplay.
/// Each chart is identified by the SHA-256 hash of its complete .tsc file,
/// so moving or renaming a chart does not change its identity. Each chart
/// keeps up to five best runs.
/// </summary>
public sealed class ScoreSaveStore
{
    private const int CurrentVersion = 2;
    private const int MaxRecordsPerChart = 5;
    private const int MaxSaveFileBytes = 8 * 1024 * 1024;
    private static readonly object Sync = new();
    private static ScoreSaveStore? _shared;

    private readonly string _filePath;
    private readonly Dictionary<string, List<ScoreRecord>> _records = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    private sealed class SaveFile
    {
        public int Version { get; set; } = CurrentVersion;
        public Dictionary<string, List<ScoreRecord>> Records { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class ScoreRecord
    {
        public int Score { get; set; }
        public double Accuracy { get; set; }
        public int MaxCombo { get; set; }
        public string Grade { get; set; } = string.Empty;
        public string ClearType { get; set; } = string.Empty;
        public string LastPlayedUtc { get; set; } = string.Empty;
    }

    public static ScoreSaveStore Shared
    {
        get
        {
            lock (Sync)
                return _shared ??= new ScoreSaveStore();
        }
    }

    private ScoreSaveStore()
    {
        UserDataPaths.Initialize();
        _filePath = UserDataPaths.ScoresPath;
    }

    /// <summary>
    /// Returns the best saved run for the chart, or null when there is no run.
    /// </summary>
    public ScoreRecord? Get(string chartPath)
    {
        lock (Sync)
        {
            EnsureLoaded();

            if (string.IsNullOrWhiteSpace(chartPath))
                return null;

            // Current v2 identity: sha256:<complete .tsc bytes>.
            if (TryMakeKey(chartPath, out var hashKey))
            {
                if (TryGetBest(hashKey, out var hashRecord))
                {
                    return Clone(hashRecord);
                }

                // Also accept a bare SHA-256 key. This is not what new writes use,
                // but makes the reader tolerant of the equivalent hash-keyed files.
                const string prefix = "sha256:";
                if (hashKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var bareHash = hashKey[prefix.Length..];
                    if (TryGetBest(bareHash, out var bareHashRecord))
                        return Clone(bareHashRecord);
                }
            }

            // Compatibility read path for the older path-keyed save data. New
            // records are still always written in the v2 hash-keyed format. Old
            // installs may have launched from either the game folder or the exe
            // folder, so check both bases after moving records into app data.
            var fullChartPath = Path.GetFullPath(chartPath);
            foreach (var baseDirectory in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory }
                         .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var relativePath = Path.GetRelativePath(baseDirectory, fullChartPath)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (TryGetBest(relativePath, out var pathRecord))
                    return Clone(pathRecord);
            }

            return null;
        }
    }

    /// <summary>
    /// Returns all saved runs for the chart, best first, up to five entries.
    /// </summary>
    public IReadOnlyList<ScoreRecord> GetTop(string chartPath)
    {
        lock (Sync)
        {
            EnsureLoaded();

            if (!TryMakeKey(chartPath, out var key) ||
                !_records.TryGetValue(key, out var records) ||
                records.Count == 0)
                return Array.Empty<ScoreRecord>();

            return records.Select(Clone).ToArray();
        }
    }

    /// <summary>
    /// Saves one completed run and keeps only the five best runs for the chart.
    /// The complete TSC file contents determine the chart key.
    /// </summary>
    public ScoreRecord Update(
        string chartPath,
        int score,
        double accuracy,
        int maxCombo,
        string grade,
        string clearType)
    {
        lock (Sync)
        {
            EnsureLoaded();

            var record = new ScoreRecord
            {
                Score = Math.Max(0, score),
                Accuracy = Math.Clamp(accuracy, 0.0, 100.0),
                MaxCombo = Math.Max(0, maxCombo),
                Grade = grade ?? string.Empty,
                ClearType = clearType ?? string.Empty,
                LastPlayedUtc = DateTime.UtcNow.ToString("O")
            };

            if (!TryMakeKey(chartPath, out var key))
                return Clone(record);

            if (!_records.TryGetValue(key, out var records))
            {
                records = new List<ScoreRecord>(MaxRecordsPerChart);
                _records[key] = records;
            }

            records.Add(record);
            records.Sort(CompareRecords);
            if (records.Count > MaxRecordsPerChart)
                records.RemoveRange(MaxRecordsPerChart, records.Count - MaxRecordsPerChart);

            Save();
            return Clone(records[0]);
        }
    }

    /// <summary>
    /// Returns the stable chart identity: SHA-256 of the complete .tsc file.
    /// The filesystem path is intentionally not part of the key.
    /// </summary>
    public static string MakeKey(string chartPath)
    {
        return Shared.TryMakeKey(chartPath, out var key) ? key : string.Empty;
    }

    private bool TryMakeKey(string chartPath, out string key)
    {
        key = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(chartPath))
                return false;

            var fullPath = Path.GetFullPath(chartPath);
            if (!File.Exists(fullPath))
            {
                return false;
            }

            using var stream = File.OpenRead(fullPath);
            var digest = SHA256.HashData(stream);
            key = "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
            return true;
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"SCORE_HASH_FAILED path={chartPath} error={ex.Message}");
            return false;
        }
    }

    private bool TryGetBest(string key, out ScoreRecord record)
    {
        if (_records.TryGetValue(key, out var records) && records.Count > 0)
        {
            record = records[0];
            return true;
        }

        record = new ScoreRecord();
        return false;
    }

    private static int CompareRecords(ScoreRecord? left, ScoreRecord? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return 1;
        if (right is null)
            return -1;

        var cmp = right.Score.CompareTo(left.Score);
        if (cmp != 0)
            return cmp;

        cmp = right.Accuracy.CompareTo(left.Accuracy);
        if (cmp != 0)
            return cmp;

        cmp = right.MaxCombo.CompareTo(left.MaxCombo);
        if (cmp != 0)
            return cmp;

        if (DateTime.TryParse(right.LastPlayedUtc, out var rightTime) &&
            DateTime.TryParse(left.LastPlayedUtc, out var leftTime))
            return rightTime.CompareTo(leftTime);

        return string.Compare(right.LastPlayedUtc, left.LastPlayedUtc, StringComparison.Ordinal);
    }

    private void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;

        try
        {
            if (!File.Exists(_filePath))
                return;

            var fileInfo = new FileInfo(_filePath);
            if (fileInfo.Length > MaxSaveFileBytes)
                return;

            var json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<SaveFile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (data is null || data.Records is null)
                return;

            // Version 2 is the current format. Keep the reader permissive because
            // some installations may still contain equivalent score records that
            // are keyed by path or by a bare SHA-256 string. The write side remains
            // strictly v2 hash-keyed.
            if (data.Version != CurrentVersion)
                Program.LogRuntime($"SCORE_SAVE_VERSION_READ version={data.Version} expected={CurrentVersion} — attempting compatible read");

            foreach (var pair in data.Records)
            {
                if (pair.Value is null)
                    continue;

                var records = pair.Value
                    .Where(r => r is not null)
                    .Take(MaxRecordsPerChart)
                    .Select(Clone)
                    .ToList();

                records.Sort(CompareRecords);
                if (records.Count > 0)
                    _records[pair.Key] = records;
            }

            Program.LogRuntime($"SCORE_SAVE_LOADED path={_filePath} version={data.Version} records={_records.Count}");
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"SCORE_SAVE_LOAD_FAILED path={_filePath} error={ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var data = new SaveFile
            {
                Version = CurrentVersion,
                Records = _records.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Select(Clone).ToList(),
                    StringComparer.OrdinalIgnoreCase)
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            var json = JsonSerializer.Serialize(data, options) + Environment.NewLine;
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, true);
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"SCORE_SAVE_WRITE_FAILED path={_filePath} error={ex.Message}");
        }
    }

    private static ScoreRecord Clone(ScoreRecord value)
        => new()
        {
            Score = value.Score,
            Accuracy = value.Accuracy,
            MaxCombo = value.MaxCombo,
            Grade = value.Grade,
            ClearType = value.ClearType,
            LastPlayedUtc = value.LastPlayedUtc
        };
}
