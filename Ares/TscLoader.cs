using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TSC.Ares;

/// <summary>
/// Loader for the project's own line-oriented TSC chart format.
/// TSC is intentionally independent from the ARES binary format; it is converted
/// into the renderer's existing AresChart model, with normal ARES callback
/// reconstruction performed for every TSC chart.
/// </summary>
public static class TscLoader
{
    private const string Header = "TSC";

    // Opaque ARES-only data survives a TSC editing pass inside a Base64 JSON
    // metadata value. This avoids dropping song instrument mappings, the B2
    // magic value, outer encryption/header fields, and empty track-table slots.
    private sealed class AresBinaryMetadata
    {
        public AresBinaryMetadata() { }

        public uint RawMagic { get; set; }
        public int TrackCount { get; set; } = 64;
        public List<string> InstrumentNames { get; set; } = new();
        public List<uint> InstrumentNameHuffmanFrequencies { get; set; } = new();
        public List<int> InstrumentNameReferences { get; set; } = new();
        public List<AresInstrumentDefinition> Instruments { get; set; } = new();
        public byte[]? HeaderTemplate { get; set; }
    }

    public static void Save(string path, AresChart chart, string? musicPathOverride = null, bool includeJudgementWindows = true, bool writeSongJsonSidecar = true)
    {
        ArgumentNullException.ThrowIfNull(chart);
        AresMetadataDefaults.Apply(chart);
        var lines = new List<string> { "TSC,1", "[meta]" };

        void Meta(string key, string? value)
        {
            if (value is null)
                return;
            var escaped = value.Replace("\"", "\"\"");
            lines.Add($"{key}=\"{escaped}\"");
        }

        // Song-wide metadata lives in song.json. A TSC contains only playback
        // state and chart events; ticks-per-measure is fixed by the game.
        if (chart.PlayTick != 0)
            Meta("play_tick", chart.PlayTick.ToString(CultureInfo.InvariantCulture));
        if (chart.PlayTime != 0)
            Meta("play_time", chart.PlayTime.ToString("0.########", CultureInfo.InvariantCulture));
        Meta("initial_gear_attr", chart.InitialGearAttr.ToString(CultureInfo.InvariantCulture));
        if (chart.AudioOriginTick != 0)
            Meta("audio_origin_tick", chart.AudioOriginTick.ToString(CultureInfo.InvariantCulture));

        var aresMetadata = new AresBinaryMetadata
        {
            RawMagic = chart.RawMagic,
            TrackCount = Math.Clamp(chart.AresTrackCount, 1, byte.MaxValue),
            InstrumentNames = chart.InstrumentNames.ToList(),
            InstrumentNameHuffmanFrequencies = chart.InstrumentNameHuffmanFrequencies.ToList(),
            InstrumentNameReferences = chart.InstrumentNameReferences.ToList(),
            Instruments = chart.Instruments.ToList(),
            HeaderTemplate = chart.AresHeaderTemplate is { Length: >= 28 } ? chart.AresHeaderTemplate.ToArray() : null
        };
        Meta("ares_binary_metadata", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(aresMetadata)));

        lines.Add(string.Empty);
        lines.Add("[events]");
        // TSC's event section is globally ordered by tick. Keep the ordering
        // stable for equal ticks, but do not promote track number ahead of time.
        // Callback-reconstructed notes are generated from source ARES data;
        // writing them as authored events would duplicate them on the next load.
        var authoredEvents = chart.Events
            .Where(ev => !ev.LoopReconstructed)
            .OrderBy(ev => ev.Tick);
        foreach (var ev in authoredEvents)
        {
            var typeIndex = ev.TypeIndex >= 0
                ? "," + ev.TypeIndex.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            switch (ev.RawType)
            {
                case 1:
                    lines.Add(string.Join(",", "N",
                        ev.Tick.ToString(CultureInfo.InvariantCulture),
                        ev.Track.ToString(CultureInfo.InvariantCulture),
                        ev.Attr.ToString(CultureInfo.InvariantCulture),
                        ev.Duration.ToString(CultureInfo.InvariantCulture),
                        ev.Instrument.ToString(CultureInfo.InvariantCulture),
                        ev.Velocity.ToString(CultureInfo.InvariantCulture),
                        ev.Pan.ToString(CultureInfo.InvariantCulture)) + typeIndex);
                    break;
                case 2:
                    lines.Add(string.Join(",", "V",
                        ev.Tick.ToString(CultureInfo.InvariantCulture),
                        ev.Track.ToString(CultureInfo.InvariantCulture),
                        ev.Value.ToString("0.########", CultureInfo.InvariantCulture)) + typeIndex);
                    break;
                case 3:
                    lines.Add(string.Join(",", "T",
                        ev.Tick.ToString(CultureInfo.InvariantCulture),
                        ev.Track.ToString(CultureInfo.InvariantCulture),
                        ev.Value.ToString("0.########", CultureInfo.InvariantCulture)) + typeIndex);
                    break;
                case 30:
                    lines.Add(string.Join(",", "D",
                        ev.Tick.ToString(CultureInfo.InvariantCulture),
                        ev.Track.ToString(CultureInfo.InvariantCulture),
                        ev.Value.ToString("0.########", CultureInfo.InvariantCulture)) + typeIndex);
                    break;
                case 4:
                    lines.Add(string.Join(",", "B",
                        ev.Tick.ToString(CultureInfo.InvariantCulture),
                        ev.Track.ToString(CultureInfo.InvariantCulture),
                        ev.Value.ToString("0.########", CultureInfo.InvariantCulture)) + typeIndex);
                    break;
                case 5:
                    lines.Add(string.Join(",", "U",
                        ev.Tick.ToString(CultureInfo.InvariantCulture),
                        ev.Track.ToString(CultureInfo.InvariantCulture)));
                    break;
            }
        }

        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (writeSongJsonSidecar)
            UpdateSongJsonForSave(path, chart, musicPathOverride, supportsSeries: true, includeJudgementWindows: includeJudgementWindows);
    }

    /// <summary>Creates or updates the JSON sidecar used by TSC and TSTS bundles.</summary>
    public static void SaveSongJsonSidecar(string chartPath, AresChart chart, string? musicPathOverride = null, bool supportsSeries = true, int schema = 3, string? sidecarPathOverride = null, bool includeJudgementWindows = true)
        => UpdateSongJsonForSave(chartPath, chart, musicPathOverride, supportsSeries, schema, sidecarPathOverride, includeJudgementWindows);

    private static void UpdateSongJsonForSave(string chartPath, AresChart chart, string? musicPathOverride, bool supportsSeries, int schema = 3, string? sidecarPathOverride = null, bool includeJudgementWindows = true)
    {
        // Kept in the public/call-site signature for compatibility. Media paths
        // are now fixed by the bundle template and are never written to song.json.
        _ = musicPathOverride;
        var directory = Path.GetDirectoryName(Path.GetFullPath(chartPath));
        if (string.IsNullOrWhiteSpace(directory))
            return;
        var existingSidecarDirectory = FindNearestSongJsonDirectory(directory);
        var assetDirectory = existingSidecarDirectory ?? FindNearestSongAssetDirectory(directory) ?? directory;
        var sidecarPath = sidecarPathOverride is null ? Path.Combine(assetDirectory, "song.json") : Path.GetFullPath(sidecarPathOverride);
        Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath) ?? directory);
        JsonObject root;
        var existed = File.Exists(sidecarPath);
        try
        {
            root = existed ? JsonNode.Parse(File.ReadAllText(sidecarPath)) as JsonObject ?? new JsonObject() : new JsonObject();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            root = new JsonObject();
            existed = false;
        }

        if (existed)
            NormalizeDifficultyLevels(root);

        if (!existed)
        {
            var level = new JsonObject();
            var levelKey = (chart.Difficulty ?? Path.GetFileNameWithoutExtension(chartPath)).Trim().ToLowerInvariant() switch
            {
                "ez" or "easy" => "easy",
                "nm" or "normal" => "normal",
                "hd" or "hard" => "hard",
                "ex" or "expert" => "expert",
                "xt" or "extra" or "sc" or "secret" => "sc",
                _ => string.Empty
            };
            if (levelKey.Length > 0 && chart.Level.HasValue)
                level[levelKey] = chart.Level.Value;

            root["schema"] = schema;
            root["title"] = chart.Name ?? Path.GetFileName(directory);
            root["artist"] = chart.Artist ?? string.Empty;
            if (supportsSeries)
                root["series"] = chart.Series ?? "Unknown";
            root["element"] = string.IsNullOrWhiteSpace(chart.Element) ? "DANCER" : chart.Element;
            root["audio_format"] = "ogg-opus";
            root["mapper"] = chart.Mapper;
            root["description"] = chart.Description ?? string.Empty;
            root["version"] = chart.Version ?? "1.0.0";
            root["bpm"] = chart.MetadataBpm;
            root["difficulty_levels"] = level;
            if (chart.AlbumBigPath is not null)
                root["album_big"] = chart.AlbumBigPath;
        }
        else
        {
            if (chart.Name is not null) root["title"] = chart.Name;
            if (chart.Artist is not null) root["artist"] = chart.Artist;
            if (chart.Mapper is not null) root["mapper"] = chart.Mapper;
            if (chart.Description is not null) root["description"] = chart.Description;
            if (chart.Element is not null) root["element"] = chart.Element;
            if (chart.Version is not null) root["version"] = chart.Version;
            if (supportsSeries)
            {
                if (chart.Series is not null) root["series"] = chart.Series;
                else root.Remove("series");
            }
            else
            {
                root.Remove("series");
            }
            root["bpm"] = chart.MetadataBpm;
            if (chart.AlbumBigPath is not null) root["album_big"] = chart.AlbumBigPath;
            if (chart.Level.HasValue)
            {
                var existingLevels = root["difficulty_levels"] as JsonObject;
                if (existingLevels is null)
                {
                    existingLevels = new JsonObject();
                    root["difficulty_levels"] = existingLevels;
                }
                var levelKey = GetDifficultyJsonKey(chart.Difficulty, chartPath);
                if (levelKey.Length > 0) existingLevels[levelKey] = chart.Level.Value;
            }
        }

        // Media filenames are defined by the selected bundle template, not by song.json.
        root.Remove("music");
        root.Remove("preview");
        root.Remove("album");

        // Upgrade the old `level` object and old `secret` spelling to the current schema.
        NormalizeDifficultyLevels(root);
        if (root["judgment_windows_ms"] is JsonObject existingWindows)
            RenameSecretDifficultyKey(existingWindows);
        if (HasScChart(chartPath, sidecarPath))
        {
            // The SC passcode is a string, not a number; do not convert it
            // through a numeric representation when writing song.json.
            root["sc_passcode"] = string.IsNullOrEmpty(chart.ScPasscode) ? null : chart.ScPasscode;
        }
        else
        {
            root.Remove("sc_passcode");
        }

        // Merge the old British spelling into the schema spelling without
        // dropping window settings for other difficulties.
        if (root["judgement_windows_ms"] is JsonObject britishWindows)
        {
            if (root["judgment_windows_ms"] is JsonObject canonicalWindows)
            {
                foreach (var pair in britishWindows)
                    if (!canonicalWindows.ContainsKey(pair.Key))
                        canonicalWindows[pair.Key] = pair.Value?.DeepClone();
            }
            else
            {
                root["judgment_windows_ms"] = britishWindows.DeepClone();
            }
            root.Remove("judgement_windows_ms");
        }

        var difficultyKeyForWindows = GetDifficultyJsonKey(chart.Difficulty, chartPath);
        var allWindows = root["judgment_windows_ms"] as JsonObject;
        if (allWindows is not null)
            RenameSecretDifficultyKey(allWindows);

        static bool IsDifficultyWindowsKey(string key)
            => key.Equals("easy", StringComparison.OrdinalIgnoreCase) || key.Equals("ez", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("normal", StringComparison.OrdinalIgnoreCase) || key.Equals("nm", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("hard", StringComparison.OrdinalIgnoreCase) || key.Equals("hd", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("expert", StringComparison.OrdinalIgnoreCase) || key.Equals("ex", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("sc", StringComparison.OrdinalIgnoreCase) || key.Equals("secret", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("xt", StringComparison.OrdinalIgnoreCase) || key.Equals("extra", StringComparison.OrdinalIgnoreCase);

        if (!includeJudgementWindows)
        {
            if (allWindows is not null)
            {
                var hasPerDifficultyWindows = allWindows.Any(pair => IsDifficultyWindowsKey(pair.Key));
                if (!hasPerDifficultyWindows)
                {
                    // A legacy flat object applies to every difficulty, so it
                    // cannot safely be retained when this difficulty is disabled.
                    root.Remove("judgment_windows_ms");
                }
                else
                {
                    // Remove only the current difficulty; other difficulties keep
                    // their own custom windows when the metadata is saved again.
                    var keysToRemove = difficultyKeyForWindows switch
                    {
                        "easy" => new[] { "easy", "ez" },
                        "normal" => new[] { "normal", "nm" },
                        "hard" => new[] { "hard", "hd" },
                        "expert" => new[] { "expert", "ex" },
                        "sc" => new[] { "sc", "secret", "xt", "extra" },
                        _ => Array.Empty<string>()
                    };
                    foreach (var key in keysToRemove) allWindows.Remove(key);
                    allWindows.Remove("perfect_plus");
                    allWindows.Remove("perfect");
                    allWindows.Remove("good");
                    if (allWindows.Count == 0)
                        root.Remove("judgment_windows_ms");
                }
            }
        }
        else if (difficultyKeyForWindows.Length > 0)
        {
            if (allWindows is null)
            {
                allWindows = new JsonObject();
                root["judgment_windows_ms"] = allWindows;
            }
            // Upgrade the old flat single-difficulty shape; these values must not
            // leak to every difficulty after a save. Also normalize the selected
            // difficulty's aliases to its canonical schema key.
            allWindows.Remove("perfect_plus");
            allWindows.Remove("perfect");
            allWindows.Remove("good");
            var selectedAliases = difficultyKeyForWindows switch
            {
                "easy" => new[] { "easy", "ez" },
                "normal" => new[] { "normal", "nm" },
                "hard" => new[] { "hard", "hd" },
                "expert" => new[] { "expert", "ex" },
                "sc" => new[] { "sc", "secret", "xt", "extra" },
                _ => Array.Empty<string>()
            };
            foreach (var key in selectedAliases) allWindows.Remove(key);
            allWindows[difficultyKeyForWindows] = new JsonObject
            {
                ["perfect_plus"] = chart.PerfectPlusWindowMs,
                ["perfect"] = chart.PerfectWindowMs,
                ["good"] = chart.GoodWindowMs
            };
        }

        root["schema"] = schema;
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(sidecarPath, root.ToJsonString(options) + Environment.NewLine, new UTF8Encoding(false));
    }

    private static void NormalizeDifficultyLevels(JsonObject root)
    {
        var current = root["difficulty_levels"] as JsonObject;
        var legacy = root["level"] as JsonObject;
        if (current is null && legacy is null)
        {
            root["difficulty_levels"] = new JsonObject();
            root.Remove("level");
            return;
        }

        var normalized = new JsonObject();
        void Copy(JsonObject? source, bool overwrite)
        {
            if (source is null) return;
            // Copy normal keys first so an explicit `sc` value wins over a
            // stale `secret` alias regardless of JSON property order.
            foreach (var pair in source.Where(pair => !string.Equals(pair.Key, "secret", StringComparison.OrdinalIgnoreCase)))
            {
                // Missing chart difficulties must be omitted, not serialized as null.
                var value = pair.Value;
                if (value is null)
                    continue;
                var key = string.Equals(pair.Key, "sc", StringComparison.OrdinalIgnoreCase) ? "sc" : pair.Key;
                if (overwrite || !normalized.ContainsKey(key))
                    normalized[key] = value.DeepClone();
            }
            foreach (var pair in source.Where(pair => string.Equals(pair.Key, "secret", StringComparison.OrdinalIgnoreCase)))
            {
                var value = pair.Value;
                if (value is not null && !normalized.ContainsKey("sc"))
                    normalized["sc"] = value.DeepClone();
            }
        }
        Copy(current, overwrite: true);
        Copy(legacy, overwrite: false);
        root["difficulty_levels"] = normalized;
        root.Remove("level");
    }

    private static void RenameSecretDifficultyKey(JsonObject levels)
    {
        var secretKey = levels.Select(pair => pair.Key)
            .FirstOrDefault(key => string.Equals(key, "secret", StringComparison.OrdinalIgnoreCase));
        if (secretKey is null) return;
        var scExists = levels.Any(pair => string.Equals(pair.Key, "sc", StringComparison.OrdinalIgnoreCase));
        if (!scExists)
            levels["sc"] = levels[secretKey]?.DeepClone();
        levels.Remove(secretKey);
    }

    private static bool HasScChart(string chartPath, string sidecarPath)
    {
        var names = new[]
        {
            "sc.tsc", "xt.tsc", "extra.tsc", "secret.tsc",
            "sc.ares", "xt.ares", "extra.ares", "secret.ares",
            "pattern_sc.ares", "pattern_xt.ares", "pattern_extra.ares", "pattern_secret.ares"
        };
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddDirectory(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var full = Path.GetFullPath(value);
            directories.Add(full);
            directories.Add(Path.Combine(full, "charts"));
            if (string.Equals(Path.GetFileName(full), "charts", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Directory.GetParent(full)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent)) directories.Add(parent);
            }
        }
        AddDirectory(Path.GetDirectoryName(Path.GetFullPath(chartPath)));
        AddDirectory(Path.GetDirectoryName(Path.GetFullPath(sidecarPath)));
        var stem = Path.GetFileName(chartPath).ToLowerInvariant();
        if (stem is "sc.tsc" or "xt.tsc" or "extra.tsc" or "secret.tsc" or "sc.ares" or "xt.ares" or "extra.ares" or "secret.ares" or "pattern_sc.ares" or "pattern_xt.ares" or "pattern_extra.ares" or "pattern_secret.ares")
            return true;
        return directories.Any(directory => names.Any(name => File.Exists(Path.Combine(directory, name))));
    }

    private static string? FindNearestSongJsonDirectory(string chartDirectory)
    {
        string? current = Path.GetFullPath(chartDirectory);
        for (var depth = 0; current is not null && depth < 10; depth++)
        {
            if (File.Exists(Path.Combine(current, "song.json")))
                return current;
            current = Directory.GetParent(current)?.FullName;
        }
        return null;
    }

    private static string? FindNearestSongAssetDirectory(string chartDirectory)
    {
        var ancestors = new List<string>();
        string? current = Path.GetFullPath(chartDirectory);
        for (var depth = 0; current is not null && depth < 10; depth++)
        {
            ancestors.Add(current);
            current = Directory.GetParent(current)?.FullName;
        }
        var musicRoot = ancestors.FirstOrDefault(directory =>
            File.Exists(Path.Combine(directory, "music.opus")) || File.Exists(Path.Combine(directory, "song.opus")));
        if (musicRoot is not null)
            return musicRoot;
        return ancestors.FirstOrDefault(directory => new[] { "preview.opus", "cover.webp", "album.png", "album_big.png", "album", "album_big" }
            .Any(name => File.Exists(Path.Combine(directory, name))));
    }

    private static bool IsTstsBundleChartPath(string chartPath, string metadataDirectory)
    {
        if (!string.Equals(Path.GetExtension(chartPath), ".ares", StringComparison.OrdinalIgnoreCase))
            return false;
        var chartDirectory = Path.GetDirectoryName(Path.GetFullPath(chartPath));
        if (string.IsNullOrWhiteSpace(chartDirectory))
            return false;
        var stem = Path.GetFileNameWithoutExtension(chartPath).ToLowerInvariant();
        if (stem.StartsWith("pattern_", StringComparison.OrdinalIgnoreCase))
            stem = stem[8..];
        if (stem is not ("ez" or "easy" or "nm" or "normal" or "hd" or "hard" or "ex" or "expert" or "xt" or "extra" or "secret" or "sc"))
            return false;

        if (Path.GetFileName(chartDirectory).Equals("charts", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(chartDirectory)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent) &&
                string.Equals(Path.GetFullPath(parent), Path.GetFullPath(metadataDirectory), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return string.Equals(Path.GetFullPath(chartDirectory), Path.GetFullPath(metadataDirectory), StringComparison.OrdinalIgnoreCase) &&
            (File.Exists(Path.Combine(metadataDirectory, "song.opus")) || File.Exists(Path.Combine(metadataDirectory, "cover.webp"))) &&
            !File.Exists(Path.Combine(metadataDirectory, "music.opus"));
    }

    private static string GetDifficultyJsonKey(string? difficulty, string? chartPath)
    {
        var value = (difficulty ?? Path.GetFileNameWithoutExtension(chartPath ?? string.Empty)).Trim().ToLowerInvariant();
        if (value.StartsWith("pattern_", StringComparison.OrdinalIgnoreCase))
            value = value[8..];
        return value switch
        {
            "ez" or "easy" => "easy",
            "nm" or "normal" => "normal",
            "hd" or "hard" => "hard",
            "ex" or "expert" => "expert",
            "xt" or "extra" or "sc" or "secret" => "sc",
            _ => string.Empty
        };
    }

    public static AresChart Load(string path) => Parse(File.ReadAllText(path), path);

    /// <summary>
    /// Reads only the TSC header and [meta] section. This is intentionally
    /// separate from Load() because song-select only needs metadata and should
    /// not parse/rebuild the full event stream for every chart in the library.
    /// </summary>
    public static AresChart LoadMetadata(string path)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var version = 0;
        var section = string.Empty;
        var lineNumber = 0;

        foreach (var rawLine in File.ReadLines(path))
        {
            lineNumber++;
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (section.Equals("events", StringComparison.OrdinalIgnoreCase))
                    break;
                continue;
            }

            if (section.Length == 0 && line.StartsWith("TSC,", StringComparison.OrdinalIgnoreCase))
            {
                var parts = SplitCsv(line);
                if (parts.Count != 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out version))
                    throw Error(lineNumber, "Invalid TSC header. Expected TSC,1.");
                if (version != 1)
                    throw Error(lineNumber, $"Unsupported TSC version {version}.");
                continue;
            }

            if (section.Equals("meta", StringComparison.OrdinalIgnoreCase))
            {
                var equals = line.IndexOf('=');
                if (equals <= 0)
                    throw Error(lineNumber, "Metadata lines must use key=value syntax.");
                var key = line[..equals].Trim();
                var value = Unquote(line[(equals + 1)..].Trim());
                metadata[key] = value;
                continue;
            }

            throw Error(lineNumber, $"Unknown or missing section for line: {line}");
        }

        if (version == 0)
            throw new InvalidDataException("TSC file is missing its TSC,1 header.");

        var chart = CreateMetadataChart(metadata);
        ApplySongJsonMetadata(chart, path);
        return chart;
    }

    public static AresChart Parse(string text, string? sourcePath = null)
    {
        using var reader = new StringReader(text);
        var lineNumber = 0;
        var section = string.Empty;
        var version = 0;
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trackEvents = new List<(int Track, ChartEvent Event)>();
        var tempoEvents = new List<(int Tick, double Bpm)>();
        var trackEventCounts = new Dictionary<int, int>();

        string? rawLine;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            lineNumber++;
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            if (section.Length == 0 && line.StartsWith("TSC,", StringComparison.OrdinalIgnoreCase))
            {
                var parts = SplitCsv(line);
                if (parts.Count != 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out version))
                    throw Error(lineNumber, "Invalid TSC header. Expected TSC,1.");
                if (version != 1)
                    throw Error(lineNumber, $"Unsupported TSC version {version}.");
                continue;
            }

            if (section.Equals("meta", StringComparison.OrdinalIgnoreCase))
            {
                var equals = line.IndexOf('=');
                if (equals <= 0)
                    throw Error(lineNumber, "Metadata lines must use key=value syntax.");
                var key = line[..equals].Trim();
                var value = Unquote(line[(equals + 1)..].Trim());
                metadata[key] = value;
                continue;
            }

            if (section.Equals("events", StringComparison.OrdinalIgnoreCase))
            {
                ParseEventLine(line, lineNumber, trackEvents, tempoEvents, trackEventCounts);
                continue;
            }

            throw Error(lineNumber, $"Unknown or missing section for line: {line}");
        }

        if (version == 0)
            throw new InvalidDataException("TSC file is missing its TSC,1 header.");

        var chart = CreateMetadataChart(metadata);
        if (!string.IsNullOrWhiteSpace(sourcePath))
            ApplySongJsonMetadata(chart, sourcePath);

        foreach (var trackIndex in trackEventCounts.Keys.OrderBy(x => x))
        {
            chart.Tracks.Add(new ChartTrack { TrackIndex = trackIndex });
        }

        foreach (var (track, ev) in trackEvents)
        {
            var chartTrack = chart.FindTrack(track);
            if (chartTrack is null)
            {
                chartTrack = new ChartTrack { TrackIndex = track };
                chart.Tracks.Add(chartTrack);
            }
            chartTrack.Events.Add(ev);
        }

        chart.TempoEvents.AddRange(tempoEvents);

        // TSC stores the playable starting tempo in the tick-0 tempo event.
        // Keep it separate from song.json's metadata `bpm`, which is the base
        // BPM used by song-level behavior such as MMOD.
        var tickZeroTempo = chart.Tracks.SelectMany(track => track.Events)
            .Where(ev => ev.RawType == 3 && ev.Tick == 0 && float.IsFinite(ev.Value) && ev.Value > 0f)
            .OrderBy(ev => ev.Track)
            .ThenBy(ev => ev.EventIndex)
            .FirstOrDefault();
        if (tickZeroTempo is not null)
            chart.InitialTempo = tickZeroTempo.Value;

        // TSC is now always loaded through the ARES semantic finalization path.
        // The file no longer needs an import=ares marker: callback reconstruction
        // is part of normal TSC loading.
        AresLoader.FinalizeChart(chart, reconstructAresCallbacks: true);
        return chart;
    }


    public static void ApplySongJsonMetadata(AresChart chart, string chartPath)
    {
        try
        {
            var chartDirectory = Path.GetDirectoryName(Path.GetFullPath(chartPath));
            if (string.IsNullOrWhiteSpace(chartDirectory))
                return;
            // A song can keep its charts in a child folder and its common audio,
            // artwork, and metadata at the bundle root. Use the nearest ancestor
            // containing song.json instead of assuming charts and assets share a folder.
            var directory = FindNearestSongJsonDirectory(chartDirectory) ?? chartDirectory;
            var sidecarPath = Path.Combine(directory, "song.json");
            if (!File.Exists(sidecarPath))
                return;

            using var document = JsonDocument.Parse(File.ReadAllText(sidecarPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;

            string? GetString(string name)
                => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

            chart.Name = GetString("title") ?? chart.Name;
            chart.Artist = GetString("artist") ?? chart.Artist;
            chart.Mapper = GetString("mapper") ?? chart.Mapper;
            chart.Description = GetString("description") ?? chart.Description;
            chart.Element = GetString("element") ?? chart.Element;
            chart.Version = GetString("version") ?? chart.Version ?? "1.0.0";
            chart.ScPasscode = GetString("sc_passcode");
            // The ARES filename defines the loose-file family. BOLD uses
            // pattern_{ez,nm,hd,ex,xt}.ares and supports series; TSTS uses
            // {ez,nm,hd,ex,sc}.ares (or full difficulty names inside its .tsts
            // charts/ folder) and does not. Both use schema 3 JSON.
            var isTsts = IsTstsBundleChartPath(chartPath, directory);
            var isBold = string.Equals(Path.GetExtension(chartPath), ".ares", StringComparison.OrdinalIgnoreCase) && !isTsts;
            if (string.Equals(Path.GetExtension(chartPath), ".tsc", StringComparison.OrdinalIgnoreCase) || isBold)
                chart.Series = GetString("series") ?? chart.Series;
            else
                chart.Series = null;
            // Media files use the paths fixed by the detected bundle layout. The
            // names in song.json are ignored so old sidecars cannot redirect loading.
            chart.MusicPath = isTsts ? "song.opus" : "music.opus";
            chart.PreviewPath = "preview.opus";
            chart.AlbumPath = isTsts ? "cover.webp" : isBold ? "album" : "album.png";
            // album_big remains a legacy optional field for TSC/BOLD metadata.
            chart.AlbumBigPath = isTsts ? null : GetString("album_big") ?? (isBold ? "album_big" : "album_big.png");
            if (root.TryGetProperty("bpm", out var bpmValue) && bpmValue.ValueKind == JsonValueKind.Number && bpmValue.TryGetDouble(out var bpm) && double.IsFinite(bpm) && bpm > 0)
                chart.MetadataBpm = bpm;

            var stem = Path.GetFileNameWithoutExtension(chartPath).Trim().ToLowerInvariant();
            if (stem.StartsWith("pattern_", StringComparison.OrdinalIgnoreCase))
                stem = stem[8..];
            chart.Difficulty = stem switch
            {
                "ez" or "easy" => "ez",
                "nm" or "normal" => "nm",
                "hd" or "hard" => "hd",
                "ex" or "expert" => "ex",
                "sc" or "xt" or "extra" or "secret" => "xt",
                _ => chart.Difficulty ?? stem
            };
            var key = GetDifficultyJsonKey(chart.Difficulty, chartPath);
            if ((root.TryGetProperty("difficulty_levels", out var levels) && levels.ValueKind == JsonValueKind.Object) ||
                (root.TryGetProperty("level", out levels) && levels.ValueKind == JsonValueKind.Object))
            {
                if (key.Length > 0 && TryGetDifficultyJsonValue(levels, key, out var level) && level.ValueKind == JsonValueKind.Number && level.TryGetInt32(out var parsedLevel))
                    chart.Level = parsedLevel;
            }

            if (root.TryGetProperty("judgment_windows_ms", out var windows) && windows.ValueKind == JsonValueKind.Object)
            {
                JsonElement perDifficulty = windows;
                if (key.Length > 0 && TryGetDifficultyJsonValue(windows, key, out var keyed) && keyed.ValueKind == JsonValueKind.Object)
                    perDifficulty = keyed;
                if (TryReadJudgementWindow(perDifficulty, "perfect_plus", out var perfectPlus) &&
                    TryReadJudgementWindow(perDifficulty, "perfect", out var perfect) &&
                    TryReadJudgementWindow(perDifficulty, "good", out var good) &&
                    IsValidJudgementWindows(perfectPlus, perfect, good))
                {
                    chart.PerfectPlusWindowMs = perfectPlus;
                    chart.PerfectWindowMs = perfect;
                    chart.GoodWindowMs = good;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Sidecar metadata is optional for legacy charts; retain safe defaults.
        }
    }

    private static bool TryGetDifficultyJsonValue(JsonElement levels, string key, out JsonElement value)
    {
        if (levels.TryGetProperty(key, out value)) return true;
        var aliases = key switch
        {
            "easy" => new[] { "ez" },
            "normal" => new[] { "nm" },
            "hard" => new[] { "hd" },
            "expert" => new[] { "ex" },
            "sc" => new[] { "secret", "xt", "extra" },
            _ => Array.Empty<string>()
        };
        foreach (var alias in aliases)
            if (levels.TryGetProperty(alias, out value)) return true;
        value = default;
        return false;
    }

    private static bool TryReadJudgementWindow(JsonElement element, string key, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(key, out var node) &&
               node.ValueKind == JsonValueKind.Number &&
               node.TryGetDouble(out value) && double.IsFinite(value);
    }

    public static bool IsValidJudgementWindows(double perfectPlus, double perfect, double good)
        => perfectPlus >= 0 && perfectPlus <= perfect && perfect <= good && good > 0 && good <= 1000;

    private static AresChart CreateMetadataChart(Dictionary<string, string> metadata)
    {
        // Ticks per measure is a game setting, never a chart property.
        const int ticksPerMeasure = 192;
        // Read legacy `bpm` from old TSC files as a fallback for both values.
        // On load, a tick-0 tempo event overrides InitialTempo, while schema-3
        // song.json independently overrides MetadataBpm. New TSC saves do not
        // write the legacy [meta] bpm value.
        var initialTempo = GetDouble(metadata, "bpm", 120.0);

        AresBinaryMetadata? aresMetadata = null;
        if (metadata.TryGetValue("ares_binary_metadata", out var encodedAresMetadata) && encodedAresMetadata.Length > 0)
        {
            try
            {
                var json = Convert.FromBase64String(encodedAresMetadata);
                aresMetadata = JsonSerializer.Deserialize<AresBinaryMetadata>(json)
                    ?? throw new InvalidDataException("ARES round-trip metadata is empty.");
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                throw new InvalidDataException("Invalid ares_binary_metadata in TSC file.", ex);
            }

            if (aresMetadata.TrackCount is < 1 or > byte.MaxValue)
                throw new InvalidDataException($"Invalid ARES track count {aresMetadata.TrackCount} in TSC metadata.");
            if (aresMetadata.HeaderTemplate is { Length: > 0 } header &&
                (header.Length != 28 || !header.AsSpan(0, 4).SequenceEqual("ARES"u8)))
                throw new InvalidDataException("Invalid ARES header template in TSC metadata.");
        }

        var chart = new AresChart
        {
            Name = GetNullableString(metadata, "name"),
            Artist = GetNullableString(metadata, "artist"),
            Series = GetNullableString(metadata, "series"),
            Difficulty = GetNullableString(metadata, "difficulty"),
            Level = GetNullableInt(metadata, "level"),
            MusicPath = GetNullableString(metadata, "music"),
            PreviewPath = GetNullableString(metadata, "preview"),
            AlbumPath = GetNullableString(metadata, "album"),
            AlbumBigPath = GetNullableString(metadata, "album_big"),
            RawMagic = aresMetadata?.RawMagic ?? 0,
            AresTrackCount = aresMetadata?.TrackCount ?? 64,
            AresHeaderTemplate = aresMetadata?.HeaderTemplate?.ToArray(),
            TicksPerMeasure = ticksPerMeasure,
            MetadataBpm = initialTempo,
            InitialTempo = initialTempo,
            PlayTick = GetInt(metadata, "play_tick", 0),
            PlayTime = GetDouble(metadata, "play_time", 0),
            InitialGearAttr = GetInt(metadata, "initial_gear_attr", 6),
            AudioOriginTick = GetInt(metadata, "audio_origin_tick", 0)
        };
        if (aresMetadata is not null)
        {
            chart.InstrumentNames.AddRange(aresMetadata.InstrumentNames ?? new List<string>());
            chart.InstrumentNameHuffmanFrequencies.AddRange(aresMetadata.InstrumentNameHuffmanFrequencies ?? new List<uint>());
            chart.InstrumentNameReferences.AddRange(aresMetadata.InstrumentNameReferences ?? new List<int>());
            chart.Instruments.AddRange(aresMetadata.Instruments ?? new List<AresInstrumentDefinition>());
        }
        return chart;
    }

    private static void ParseEventLine(
        string line,
        int lineNumber,
        List<(int Track, ChartEvent Event)> output,
        List<(int Tick, double Bpm)> tempoEvents,
        Dictionary<int, int> trackEventCounts)
    {
        var fields = SplitCsv(line);
        if (fields.Count == 0)
            return;

        var kind = fields[0].Trim().ToUpperInvariant();
        switch (kind)
        {
            case "N":
                if (fields.Count != 8 && fields.Count != 9)
                    throw Error(lineNumber, "Expected N,tick,track,attr,duration,instrument,velocity,pan[,typeIndex].");
                var noteTick = ParseInt(fields[1], lineNumber, "tick");
                var track = ParseInt(fields[2], lineNumber, "track");
                var attr = ParseInt(fields[3], lineNumber, "attr");
                var duration = ParseInt(fields[4], lineNumber, "duration");
                var instrument = ParseInt(fields[5], lineNumber, "instrument");
                var velocity = ParseInt(fields[6], lineNumber, "velocity");
                var pan = ParseInt(fields[7], lineNumber, "pan");
                var noteTypeIndex = ParseOptionalTypeIndex(fields, 8, lineNumber);
                if ((uint)attr > 255)
                    throw Error(lineNumber, "Note attr must be 0..255.");
                if (duration < 0)
                    throw Error(lineNumber, "Note duration cannot be negative.");

                var noteType = AresLoader.GetNoteTypeForTrack(track, attr, duration);
                var eventIndex = trackEventCounts.GetValueOrDefault(track, 0);
                trackEventCounts[track] = eventIndex + 1;
                output.Add((track, new ChartEvent
                {
                    Tick = noteTick,
                    Track = track,
                    Attr = attr,
                    RawType = 1,
                    TypeIndex = noteTypeIndex,
                    Duration = duration,
                    NoteType = noteType,
                    EventIndex = eventIndex,
                    LinePair = noteType == NoteType.LinkHead ? attr % 10 : noteType == NoteType.LinkTail ? attr % 10 - 1 : -1,
                    Kind = "Note",
                    Instrument = instrument,
                    Velocity = velocity,
                    Pan = pan
                }));
                break;

            case "T":
                if (fields.Count != 4 && fields.Count != 5)
                    throw Error(lineNumber, "Expected T,tick,track,bpm[,typeIndex].");
                var tempoTick = ParseInt(fields[1], lineNumber, "tick");
                var tempoTrack = ParseInt(fields[2], lineNumber, "track");
                var bpm = ParseDouble(fields[3], lineNumber, "bpm");
                var tempoTypeIndex = ParseOptionalTypeIndex(fields, 4, lineNumber);
                tempoEvents.Add((tempoTick, bpm));
                var tempoIndex = trackEventCounts.GetValueOrDefault(tempoTrack, 0);
                trackEventCounts[tempoTrack] = tempoIndex + 1;
                output.Add((tempoTrack, new ChartEvent
                {
                    Tick = tempoTick,
                    Track = tempoTrack,
                    RawType = 3,
                    TypeIndex = tempoTypeIndex,
                    EventIndex = tempoIndex,
                    Kind = "Tempo",
                    Value = (float)bpm
                }));
                break;

            case "D":
                if (fields.Count != 4 && fields.Count != 5)
                    throw Error(lineNumber, "Expected D,tick,track,bpm[,typeIndex] for a disabled tempo event.");
                var disabledTempoTick = ParseInt(fields[1], lineNumber, "tick");
                var disabledTempoTrack = ParseInt(fields[2], lineNumber, "track");
                var disabledTempoBpm = ParseDouble(fields[3], lineNumber, "bpm");
                var disabledTempoTypeIndex = ParseOptionalTypeIndex(fields, 4, lineNumber);
                if (!double.IsFinite(disabledTempoBpm) || disabledTempoBpm <= 0)
                    throw Error(lineNumber, "Disabled tempo BPM must be a positive finite number.");
                var disabledTempoIndex = trackEventCounts.GetValueOrDefault(disabledTempoTrack, 0);
                trackEventCounts[disabledTempoTrack] = disabledTempoIndex + 1;
                output.Add((disabledTempoTrack, new ChartEvent
                {
                    Tick = disabledTempoTick,
                    Track = disabledTempoTrack,
                    RawType = 30,
                    TypeIndex = disabledTempoTypeIndex,
                    EventIndex = disabledTempoIndex,
                    Kind = "DisabledTempo",
                    Value = (float)disabledTempoBpm
                }));
                break;

            case "V":
                if (fields.Count != 4 && fields.Count != 5)
                    throw Error(lineNumber, "Expected V,tick,track,volume[,typeIndex].");
                var volumeTick = ParseInt(fields[1], lineNumber, "tick");
                var volumeTrack = ParseInt(fields[2], lineNumber, "track");
                var volume = ParseDouble(fields[3], lineNumber, "volume");
                var volumeTypeIndex = ParseOptionalTypeIndex(fields, 4, lineNumber);
                var volumeIndex = trackEventCounts.GetValueOrDefault(volumeTrack, 0);
                trackEventCounts[volumeTrack] = volumeIndex + 1;
                output.Add((volumeTrack, new ChartEvent
                {
                    Tick = volumeTick,
                    Track = volumeTrack,
                    RawType = 2,
                    TypeIndex = volumeTypeIndex,
                    EventIndex = volumeIndex,
                    Kind = "Volume",
                    Value = (float)volume
                }));
                break;

            case "B":
                if (fields.Count != 4 && fields.Count != 5)
                    throw Error(lineNumber, "Expected B,tick,track,beat[,typeIndex].");
                var beatTick = ParseInt(fields[1], lineNumber, "tick");
                var beatTrack = ParseInt(fields[2], lineNumber, "track");
                var beat = ParseDouble(fields[3], lineNumber, "beat");
                var beatTypeIndex = ParseOptionalTypeIndex(fields, 4, lineNumber);
                var beatIndex = trackEventCounts.GetValueOrDefault(beatTrack, 0);
                trackEventCounts[beatTrack] = beatIndex + 1;
                output.Add((beatTrack, new ChartEvent
                {
                    Tick = beatTick,
                    Track = beatTrack,
                    RawType = 4,
                    TypeIndex = beatTypeIndex,
                    EventIndex = beatIndex,
                    Kind = "Beat",
                    Value = (float)beat
                }));
                break;

            case "U":
                RequireCount(fields, 3, lineNumber, "U,tick,track");
                var userTick = ParseInt(fields[1], lineNumber, "tick");
                var userTrack = ParseInt(fields[2], lineNumber, "track");
                var userIndex = trackEventCounts.GetValueOrDefault(userTrack, 0);
                trackEventCounts[userTrack] = userIndex + 1;
                output.Add((userTrack, new ChartEvent
                {
                    Tick = userTick,
                    Track = userTrack,
                    RawType = 5,
                    EventIndex = userIndex,
                    Kind = "User"
                }));
                break;

            default:
                throw Error(lineNumber, $"Unknown TSC event type '{fields[0]}'.");
        }
    }

    private static string StripComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    i++;
                    continue;
                }
                inQuotes = !inQuotes;
            }
            else if (line[i] == '#' && !inQuotes)
            {
                return line[..i];
            }
        }
        return line;
    }

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        if (inQuotes)
            throw new InvalidDataException("Unterminated quoted CSV field.");
        fields.Add(current.ToString().Trim());
        return fields;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\"\"", "\"");
        return value;
    }

    private static void RequireCount(List<string> fields, int count, int lineNumber, string shape)
    {
        if (fields.Count != count)
            throw Error(lineNumber, $"Expected {shape}; got {fields.Count} fields.");
    }

    private static int ParseOptionalTypeIndex(List<string> fields, int index, int lineNumber)
    {
        if (fields.Count <= index)
            return -1;

        var typeIndex = ParseInt(fields[index], lineNumber, "typeIndex");
        if (typeIndex < -1 || typeIndex > 65535)
            throw Error(lineNumber, "typeIndex must be -1..65535.");
        return typeIndex;
    }

    private static int ParseInt(string value, int lineNumber, string name)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            throw Error(lineNumber, $"Invalid integer for {name}: '{value}'.");
        return result;
    }

    private static int GetInt(Dictionary<string, string> metadata, string key, int fallback)
        => metadata.TryGetValue(key, out var value)
            ? ParseMetadataInt(value, key)
            : fallback;

    private static int? GetNullableInt(Dictionary<string, string> metadata, string key)
        => metadata.TryGetValue(key, out var value) ? ParseMetadataInt(value, key) : null;

    private static int ParseMetadataInt(string value, string key)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            throw new InvalidDataException($"Invalid integer metadata '{key}={value}'.");
        return result;
    }

    private static double ParseDouble(string value, int lineNumber, string name)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            throw Error(lineNumber, $"Invalid number for {name}: '{value}'.");
        return result;
    }

    private static double GetDouble(Dictionary<string, string> metadata, string key, double fallback)
        => metadata.TryGetValue(key, out var value)
            ? ParseMetadataDouble(value, key)
            : fallback;

    private static double ParseMetadataDouble(string value, string key)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            throw new InvalidDataException($"Invalid number metadata '{key}={value}'.");
        return result;
    }

    private static string GetString(Dictionary<string, string> metadata, string key, string fallback)
        => metadata.TryGetValue(key, out var value) ? value : fallback;

    private static string? GetNullableString(Dictionary<string, string> metadata, string key)
        => metadata.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    private static InvalidDataException Error(int lineNumber, string message)
        => new($"TSC line {lineNumber}: {message}");
}
