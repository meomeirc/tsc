using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace TSC.Updater;

internal static class UpdaterEntry
{
    public static void Run(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var options = UpdateOptions.Parse(args);
            Application.Run(new UpdateForm(options));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "TSC Updater", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
        finally
        {
            // Clean the temporary app/runtime copy after this process exits, including
            // the case where a user closes an update-error dialog.
            try { UpdateRunnerLauncher.ScheduleCleanupAfterExit(); } catch { }
        }
    }
}

internal sealed class UpdateForm : Form
{
    private readonly UpdateOptions _options;
    private readonly Label _status;
    private readonly ProgressBar _progress;
    private readonly Label _detail;
    private bool _finished;

    public UpdateForm(UpdateOptions options)
    {
        _options = options;
        Text = "TSC Updater";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new System.Drawing.Size(440, 126);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;

        _status = new Label
        {
            Left = 18, Top = 15, Width = 404, Height = 24,
            Text = "Preparing update…", AutoEllipsis = true
        };
        _progress = new ProgressBar
        {
            Left = 18, Top = 45, Width = 404, Height = 20,
            Minimum = 0, Maximum = 100, Value = 0, Style = ProgressBarStyle.Continuous
        };
        _detail = new Label
        {
            Left = 18, Top = 76, Width = 404, Height = 30,
            Text = "Please keep this window open.", AutoEllipsis = true
        };
        Controls.AddRange([_status, _progress, _detail]);
        Shown += async (_, _) => await RunUpdateAsync();
        FormClosing += (_, e) =>
        {
            if (!_finished) e.Cancel = true;
        };
    }

    private async Task RunUpdateAsync()
    {
        try
        {
            var engine = new UpdateEngine(_options, ReportProgress);
            await engine.RunAsync();
            ReportProgress("Update installed. Restarting TSC…", 100, "The updater will close and start the game.");
            await Task.Delay(500);
            _finished = true;
            Close();
        }
        catch (Exception ex)
        {
            ReportProgress("Update failed", _progress.Value, ex.Message);
            _detail.Width = 306;
            _detail.Text = ex.Message;
            _finished = true;
            var close = new Button { Text = "Close", Width = 82, Height = 26, Left = 340, Top = 82, DialogResult = DialogResult.OK };
            close.Click += (_, _) => Close();
            Controls.Add(close);
            AcceptButton = close;
            ClientSize = new System.Drawing.Size(440, 128);
            Environment.ExitCode = 1;
        }
    }

    private void ReportProgress(string status, int percent, string? detail = null)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ReportProgress(status, percent, detail)));
            return;
        }

        _status.Text = status;
        _progress.Value = Math.Clamp(percent, _progress.Minimum, _progress.Maximum);
        if (detail is not null) _detail.Text = detail;
    }
}

internal sealed class UpdateEngine
{
    private const string StateFileName = ".tsc-update-state.json";
    private const string SkinStateFileName = ".tsc-skin-state.json";
    private const string PackageManifestName = ".tsc-package-manifest.json";
    private const string PatchManifestName = "patch-manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly HttpClient Client = CreateClient();
    private readonly UpdateOptions _options;
    private readonly Action<string, int, string?> _progress;
    private readonly string _root;
    private readonly string _work;
    private bool _restartScheduled;

    public UpdateEngine(UpdateOptions options, Action<string, int, string?> progress)
    {
        _options = options;
        _progress = progress;
        _root = Path.GetFullPath(options.InstallDirectory);
        _work = Path.Combine(Path.GetTempPath(), "TSC-update-" + Guid.NewGuid().ToString("N"));
    }

    public async Task RunAsync()
    {
        Directory.CreateDirectory(_work);
        try
        {
            Report("Waiting for TSC to close…", 1);
            await WaitForGameAsync(_options.WaitPid).ConfigureAwait(true);

            Report("Checking GitHub releases…", 4);
            var releases = await FetchReleasesAsync().ConfigureAwait(true);
            var current = UpdateCheckServiceCompat.ParseVersion(_options.CurrentVersion)
                ?? throw new InvalidDataException($"The installed version '{_options.CurrentVersion}' is not a valid numeric version.");
            var latest = releases.Where(r => !r.IsDraft && !r.IsPrerelease && r.Version is not null)
                .OrderByDescending(r => r.Version)
                .FirstOrDefault(r => r.Version! > current)
                ?? throw new InvalidOperationException("No newer stable GitHub release was found. The release may have been removed or its tag may not be a numeric version.");

            var versionText = latest.Version!.ToString();
            var fullAsset = FindAsset(latest, $"TSC-win-x64-{versionText}.zip");
            var minimalAsset = FindAsset(latest, $"TSC-win-x64-{versionText}-minimal.zip");
            // A patch-only release is valid as long as a verified patch chain can
            // take the installed version to this release. Keep a full/minimal package
            // optional here; we decide whether one is required after trying patches.
            var fallback = minimalAsset ?? fullAsset;
            string? fallbackKind = fallback is null
                ? null
                : ReferenceEquals(fallback, minimalAsset) ? "minimal" : "full";
            var releaseManifestAsset = FindAsset(latest, GetReleaseManifestAssetName(versionText));
            ReleaseAsset? fallbackManifestAsset = null;
            long fallbackCost = long.MaxValue;
            if (fallback is not null && fallbackKind is not null)
            {
                var legacyKindManifestAsset = FindAsset(latest, GetPackageManifestAssetName(versionText, fallbackKind));
                // Older releases have a separate minimal manifest. New releases have one
                // combined manifest; prefer the legacy minimal manifest only when present.
                fallbackManifestAsset = fallbackKind.Equals("minimal", StringComparison.OrdinalIgnoreCase)
                    ? legacyKindManifestAsset ?? releaseManifestAsset
                    : releaseManifestAsset ?? legacyKindManifestAsset;
                var packageAndMetadataSize = (decimal)Math.Max(0L, fallback.Size)
                    + (decimal)Math.Max(0L, fallbackManifestAsset?.Size ?? 0L);
                fallbackCost = (long)Math.Min((decimal)long.MaxValue, packageAndMetadataSize);
            }

            // When no complete package is published, don't cap the patch chain by a
            // nonexistent fallback package. The chain is still fully verified below.
            var patchChain = FindPatchChain(releases, current, latest.Version!, fallbackCost);
            PatchResult? preparedPatch = null;
            string? patchChainFailure = null;
            if (patchChain.Count > 0)
            {
                try
                {
                    Report("Downloading update patches…", 6, $"{patchChain.Count} patch file(s)");
                    var patchArchives = new List<string>();
                    var patchManifestPaths = new List<string?>();
                    var totalPatchBytes = patchChain.Sum(p => (double)Math.Max(0L, p.Asset.Size) + Math.Max(0L, p.ManifestAsset?.Size ?? 0L));
                    double completedBytes = 0;
                    for (var i = 0; i < patchChain.Count; i++)
                    {
                        var patch = patchChain[i];
                        var archivePath = Path.Combine(_work, $"patch-{i + 1}.zip");
                        var baseCompleted = completedBytes;
                        await DownloadAssetAsync(patch.Asset, archivePath, (received, _) =>
                        {
                            var overall = totalPatchBytes <= 0 ? 1 : (baseCompleted + received) / totalPatchBytes;
                            Report($"Downloading patch {i + 1} of {patchChain.Count}…", 6 + (int)(overall * 50), patch.Asset.Name);
                        }).ConfigureAwait(true);
                        completedBytes += Math.Max(0L, patch.Asset.Size);
                        patchArchives.Add(archivePath);

                        string? patchManifestPath = null;
                        var patchManifestAsset = patch.ManifestAsset;
                        if (patchManifestAsset is not null)
                        {
                            patchManifestPath = Path.Combine(_work, $"patch-{i + 1}-manifest.json");
                            var manifestBaseCompleted = completedBytes;
                            await DownloadAssetAsync(patchManifestAsset, patchManifestPath, (received, _) =>
                            {
                                var overall = totalPatchBytes <= 0 ? 1 : (manifestBaseCompleted + received) / totalPatchBytes;
                                Report($"Downloading patch metadata {i + 1} of {patchChain.Count}…", 6 + (int)(overall * 50), patchManifestAsset.Name);
                            }).ConfigureAwait(true);
                            completedBytes += Math.Max(0L, patchManifestAsset.Size);
                        }
                        patchManifestPaths.Add(patchManifestPath);
                    }

                    preparedPatch = await PreparePatchChainAsync(patchChain, patchArchives, patchManifestPaths, current, latest, 60).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    // A patch chain is an optimization, never a reason to leave the game partially updated.
                    patchChainFailure = ex.Message;
                    Report(fallback is not null
                        ? "Patch chain unavailable; using a complete package…"
                        : "Patch chain unavailable; checking available fallback…", 5, ex.Message);
                    foreach (var path in Directory.EnumerateFiles(_work, "patch-*.zip"))
                    {
                        try { File.Delete(path); } catch { }
                    }
                    foreach (var path in Directory.EnumerateFiles(_work, "patch-*-manifest.json"))
                    {
                        try { File.Delete(path); } catch { }
                    }
                }
            }

            if (preparedPatch is not null)
            {
                Report("Installing update…", 80);
                await ApplyPatchResultAsync(preparedPatch).ConfigureAwait(true);
                return;
            }

            // If the release has no complete package, a patch chain is the only
            // safe route. Never try to install a partial ZIP as though it were full.
            if (fallback is null || fallbackKind is null)
            {
                var patchAsset = FindAsset(latest, $"TSC-win-x64-{versionText}-patch.zip");
                if (patchAsset is null)
                {
                    throw new InvalidDataException(
                        $"Release {latest.TagName} contains neither a full/minimal game ZIP nor a patch ZIP. " +
                        "Publish a full or minimal package, or publish a valid patch with its combined manifest.");
                }

                var reason = string.IsNullOrWhiteSpace(patchChainFailure)
                    ? "No complete patch chain was found from the installed version."
                    : $"Patch validation failed: {patchChainFailure}";
                throw new InvalidDataException(
                    $"Release {latest.TagName} has a patch but no full/minimal fallback, and a safe patch chain " +
                    $"from installed version {current} to {latest.Version} could not be verified. {reason} " +
                    "Make sure every intervening stable release has its patch ZIP and combined manifest, " +
                    "and that the patch targets this exact installed version. Otherwise, add a full or minimal game ZIP.");
            }

            var flavor = fallbackKind.Equals("minimal", StringComparison.OrdinalIgnoreCase) ? "Minimal" : "Full";
            Report($"Downloading {flavor} game…", 6, fallback.Name);
            var packagePath = Path.Combine(_work, "game-package.zip");
            await DownloadAssetAsync(fallback, packagePath, (received, total) =>
            {
                var ratio = total > 0 ? (double)received / total : 0;
                Report($"Downloading {flavor} game…", 6 + (int)(ratio * 58), FormatBytes(received) + (total > 0 ? $" / {FormatBytes(total)}" : string.Empty));
            }).ConfigureAwait(true);

            PackageManifest? externalManifest = null;
            if (fallbackManifestAsset is not null)
            {
                var manifestPath = Path.Combine(_work, "package-manifest.json");
                Report("Downloading package manifest…", 65, fallbackManifestAsset.Name);
                await DownloadAssetAsync(fallbackManifestAsset, manifestPath, null).ConfigureAwait(true);
                externalManifest = await ReadPackageManifestAssetAsync(manifestPath, fallbackManifestAsset.Name, versionText, fallbackKind).ConfigureAwait(true);
            }

            Report("Verifying package…", 66);
            var packageStage = Path.Combine(_work, "package");
            Directory.CreateDirectory(packageStage);
            var package = await ExtractPackageAsync(packagePath, packageStage, versionText, fallbackKind, externalManifest).ConfigureAwait(true);

            Dictionary<string, byte[]> skinFiles = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> skinDeletedPaths = new(StringComparer.OrdinalIgnoreCase);
            string? skinCommit = null;
            if (ReferenceEquals(fallback, minimalAsset))
            {
                Report("Downloading Default skin from main…", 70);
                var skinResult = await DownloadMainSkinAsync(71).ConfigureAwait(true);
                skinFiles = skinResult.Files;
                skinDeletedPaths = skinResult.DeletedPaths;
                skinCommit = skinResult.Commit;
            }

            Report("Installing game files…", 80);
            var oldState = await ReadStateAsync(Path.Combine(_root, StateFileName)).ConfigureAwait(true);
            var deletePaths = FindObsoleteManagedPaths(oldState, package.Manifest, package.Manifest.Kind == "minimal");
            deletePaths.UnionWith(skinDeletedPaths);
            if (package.Manifest.Kind == "full")
            {
                var oldSkinState = await ReadSkinStateAsync().ConfigureAwait(true);
                var newSkinPaths = package.Manifest.Files.Where(f => IsDefaultSkinPath(f.Path))
                    .Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var oldSkinPath in oldSkinState?.Files ?? [])
                    if (IsDefaultSkinPath(oldSkinPath) && !newSkinPaths.Contains(oldSkinPath))
                        deletePaths.Add(oldSkinPath);
            }
            var writes = package.Files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var skinFile in skinFiles) writes[skinFile.Key] = skinFile.Value;

            await ApplyTransactionAsync(writes, deletePaths, 82, 95).ConfigureAwait(true);

            var newState = new ManagedState
            {
                Version = versionText,
                Kind = package.Manifest.Kind,
                Files = package.Manifest.Files.Where(f => !IsProtectedPath(f.Path)).ToList()
            };

            if (package.Manifest.Kind == "minimal")
            {
                await SaveSkinStateAsync(skinFiles.Keys, skinCommit, versionText).ConfigureAwait(true);
            }
            else
            {
                await SaveSkinStateFromPackageAsync(package.Manifest.Files, versionText).ConfigureAwait(true);
            }

            await File.WriteAllTextAsync(Path.Combine(_root, StateFileName), JsonSerializer.Serialize(newState, JsonOptions)).ConfigureAwait(true);
            await ScheduleRestartAsync(latest).ConfigureAwait(true);
        }
        catch
        {
            if (!_restartScheduled)
            {
                try { if (Directory.Exists(_work)) Directory.Delete(_work, true); } catch { }
            }
            throw;
        }
    }

    private async Task WaitForGameAsync(int? pid)
    {
        if (pid is null) return;
        await Task.Run(() =>
        {
            try
            {
                using var process = Process.GetProcessById(pid.Value);
                process.WaitForExit();
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }).ConfigureAwait(true);
    }

    private async Task<List<ReleaseInfo>> FetchReleasesAsync()
    {
        var url = $"https://api.github.com/repos/{_options.Repository}/releases?per_page=100";
        using var response = await Client.GetAsync(url).ConfigureAwait(true);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(true);
        using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(true);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub returned an unexpected release list.");

        var result = new List<ReleaseInfo>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var tag = GetString(item, "tag_name");
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var version = UpdateCheckServiceCompat.ParseVersion(tag);
            var assets = new List<ReleaseAsset>();
            if (item.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetArray.EnumerateArray())
                {
                    var name = GetString(asset, "name");
                    var downloadUrl = GetString(asset, "browser_download_url");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl)) continue;
                    var size = asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var n) ? n : -1;
                    assets.Add(new ReleaseAsset(name, downloadUrl, size, GetString(asset, "digest")));
                }
            }
            result.Add(new ReleaseInfo(
                tag,
                version,
                item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
                item.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True,
                assets));
        }
        return result.Where(r => r.Version is not null).OrderBy(r => r.Version).ToList();
    }

    private static List<PatchRelease> FindPatchChain(List<ReleaseInfo> releases, Version current, Version target, long fallbackSize)
    {
        var currentIndex = releases.FindIndex(r => r.Version is not null && r.Version.CompareTo(current) == 0 && !r.IsDraft && !r.IsPrerelease);
        if (currentIndex < 0) return [];
        var chainReleases = releases.Skip(currentIndex + 1)
            .Where(r => r.Version is not null && r.Version.CompareTo(target) <= 0 && !r.IsDraft && !r.IsPrerelease)
            .ToList();
        if (chainReleases.Count == 0 || chainReleases[^1].Version is null || chainReleases[^1].Version!.CompareTo(target) != 0) return [];

        var chain = new List<PatchRelease>();
        var previousVersion = current;
        foreach (var release in chainReleases)
        {
            var version = release.Version!;
            var asset = FindAsset(release, $"TSC-win-x64-{version}-patch.zip");
            if (asset is null || asset.Size < 0) return [];
            // Prefer the old dedicated patch manifest when it exists. New releases
            // carry patch instructions inside their single combined release manifest.
            var manifestAsset = FindAsset(release, GetPatchManifestAssetName(version.ToString()))
                ?? FindAsset(release, GetReleaseManifestAssetName(version.ToString()));
            chain.Add(new PatchRelease(previousVersion, version, release, asset, manifestAsset));
            previousVersion = version;
        }
        var combinedSize = chain.Sum(p => (decimal)Math.Max(0L, p.Asset.Size) + (decimal)Math.Max(0L, p.ManifestAsset?.Size ?? 0L));
        return combinedSize <= fallbackSize ? chain : [];
    }

    private async Task<string> DownloadAssetAsync(ReleaseAsset asset, string outputPath, Action<long, long>? progress)
    {
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("GitHub supplied an invalid asset URL.");

        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(true);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? asset.Size;
        await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(true);
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long received = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory()).ConfigureAwait(true)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(true);
            hash.AppendData(buffer, 0, read);
            received += read;
            progress?.Invoke(received, total);
        }
        await output.FlushAsync().ConfigureAwait(true);
        if (asset.Size >= 0 && received != asset.Size)
            throw new InvalidDataException($"Downloaded {asset.Name} has the wrong size (expected {asset.Size}, got {received}).");

        var digest = asset.Digest;
        if (!string.IsNullOrWhiteSpace(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            var actual = Convert.ToHexString(hash.GetHashAndReset());
            var expected = digest[7..];
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-256 verification failed for {asset.Name}.");
        }
        progress?.Invoke(received, total);
        return outputPath;
    }

    private async Task<PreparedPackage> ExtractPackageAsync(string archivePath, string destination, string expectedVersion, string expectedKind, PackageManifest? externalManifest)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        PackageManifest? manifest = externalManifest;
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 50000) throw new InvalidDataException("Package has too many entries.");
        long totalExpanded = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var path = NormalizeArchivePath(entry.FullName);
            if (path.Equals(PackageManifestName, StringComparison.OrdinalIgnoreCase))
            {
                // Older releases embedded the package manifest in the ZIP. New releases
                // publish it as a separate GitHub Release asset; prefer that external copy.
                if (manifest is null)
                {
                    if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("Package manifest is unexpectedly large.");
                    await using var manifestStream = entry.Open();
                    manifest = await JsonSerializer.DeserializeAsync<PackageManifest>(manifestStream, JsonOptions).ConfigureAwait(true);
                }
                continue;
            }
            if (IsProtectedPath(path) || path.Equals("version.txt", StringComparison.OrdinalIgnoreCase) || path.Equals("info.md", StringComparison.OrdinalIgnoreCase)) continue;
            totalExpanded += entry.Length;
            if (totalExpanded > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("Package expands beyond the allowed 8 GiB limit.");
            if (entry.Length > int.MaxValue) throw new InvalidDataException($"Package entry is too large: {path}");
            await using var input = entry.Open();
            using var memory = new MemoryStream((int)Math.Min(entry.Length, 16 * 1024 * 1024));
            await input.CopyToAsync(memory).ConfigureAwait(true);
            var bytes = memory.ToArray();
            files[path] = bytes;
            var staged = ResolveSafePath(destination, path);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            await File.WriteAllBytesAsync(staged, bytes).ConfigureAwait(true);
        }

        if (manifest is not null)
        {
            if (manifest.SchemaVersion != 1 || !string.Equals(manifest.Version, expectedVersion, StringComparison.Ordinal) ||
                !string.Equals(manifest.Kind, expectedKind, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The package manifest version or type does not match the selected GitHub release asset.");
            foreach (var spec in manifest.Files)
            {
                var path = NormalizeArchivePath(spec.Path);
                if (IsProtectedPath(path)) throw new InvalidDataException($"Package manifest includes a protected path: {path}");
                if (!files.TryGetValue(path, out var bytes))
                    throw new InvalidDataException($"Package manifest entry is missing: {path}");
                if (bytes.LongLength != spec.Size || !HashMatches(bytes, spec.Sha256))
                    throw new InvalidDataException($"Package file failed verification: {path}");
            }
            var listed = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (files.Keys.Any(path => !listed.Contains(path)))
                throw new InvalidDataException("Package contains files not listed in its manifest.");
        }
        else
        {
            throw new InvalidDataException("The selected release has no separate package manifest asset and the ZIP does not contain a legacy embedded manifest.");
        }

        if (files.Count == 0) throw new InvalidDataException("Downloaded package contained no installable files.");
        return new PreparedPackage(manifest, files);
    }

    private async Task<PatchResult> PreparePatchChainAsync(List<PatchRelease> chain, List<string> archives, List<string?> externalManifestPaths, Version current, ReleaseInfo latest, int initialPercent)
    {
        var stateHashes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var finalWrites = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var finalDeletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PackageManifest? targetManifest = null;
        var previousVersion = current;

        for (var i = 0; i < chain.Count; i++)
        {
            var patchRelease = chain[i];
            Report($"Verifying patch {i + 1} of {chain.Count}…", initialPercent + (i * 5));
            using var archive = ZipFile.OpenRead(archives[i]);
            PatchManifest? manifest = null;
            var externalManifestPath = i < externalManifestPaths.Count ? externalManifestPaths[i] : null;
            if (!string.IsNullOrWhiteSpace(externalManifestPath))
            {
                manifest = await ReadPatchManifestAssetAsync(externalManifestPath, patchRelease.ManifestAsset?.Name ?? "patch manifest").ConfigureAwait(true);
            }
            if (manifest is null)
            {
                // Backward compatibility for old releases that stored patch-manifest.json
                // inside the patch ZIP, or whose external asset was an older package-only manifest.
                var manifestEntry = archive.GetEntry(PatchManifestName) ?? throw new InvalidDataException($"Patch {patchRelease.Asset.Name} has no patch entry in its external release manifest or embedded {PatchManifestName}.");
                await using var stream = manifestEntry.Open();
                manifest = await JsonSerializer.DeserializeAsync<PatchManifest>(stream, JsonOptions).ConfigureAwait(true)
                    ?? throw new InvalidDataException("Patch manifest is empty.");
            }
            if (manifest.SchemaVersion != 1 || !string.Equals(manifest.FromVersion, previousVersion.ToString(), StringComparison.Ordinal) ||
                !string.Equals(manifest.ToVersion, patchRelease.Version.ToString(), StringComparison.Ordinal))
                throw new InvalidDataException($"Patch version chain mismatch in {patchRelease.Asset.Name}.");
            if (manifest.TargetManifest is null || manifest.TargetManifest.Version != manifest.ToVersion)
                throw new InvalidDataException("Patch lacks a valid target package manifest.");

            var payloads = archive.Entries.Where(e => e.FullName.StartsWith("files/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(e.Name))
                .ToDictionary(e => NormalizeArchivePath(e.FullName[6..]), e => e, StringComparer.OrdinalIgnoreCase);

            foreach (var file in manifest.Files)
            {
                var path = NormalizeArchivePath(file.Path);
                if (IsProtectedPath(path) || path.Equals("version.txt", StringComparison.OrdinalIgnoreCase) || path.Equals("info.md", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Patch attempts to change protected path: {path}");
                if (!payloads.TryGetValue(path, out var payload)) throw new InvalidDataException($"Patch payload is missing: {path}");
                if (payload.Length > int.MaxValue) throw new InvalidDataException($"Patch file is too large: {path}");
                await using var payloadStream = payload.Open();
                using var memory = new MemoryStream((int)Math.Min(payload.Length, 16 * 1024 * 1024));
                await payloadStream.CopyToAsync(memory).ConfigureAwait(true);
                var bytes = memory.ToArray();
                if (bytes.LongLength != file.Size || !HashMatches(bytes, file.Sha256))
                    throw new InvalidDataException($"Patch file failed verification: {path}");

                var currentHash = await GetCurrentHashAsync(path, stateHashes).ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(file.FromSha256) && currentHash is not null && currentHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    // Already at this step's target state; tolerate interrupted/manual reuse.
                }
                else if (string.IsNullOrWhiteSpace(file.FromSha256))
                {
                    if (currentHash is not null && !currentHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Patch expected new file {path}, but it already exists with different content.");
                }
                else if (!string.Equals(currentHash, file.FromSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Installed file does not match the expected patch base: {path}");
                }

                stateHashes[path] = file.Sha256;
                finalWrites[path] = bytes;
                finalDeletes.Remove(path);
            }

            foreach (var deletion in manifest.DeletedFiles)
            {
                var path = NormalizeArchivePath(deletion.Path);
                if (IsProtectedPath(path)) throw new InvalidDataException($"Patch attempts to delete protected path: {path}");
                var currentHash = await GetCurrentHashAsync(path, stateHashes).ConfigureAwait(true);
                if (currentHash is not null && !string.IsNullOrWhiteSpace(deletion.FromSha256) &&
                    !currentHash.Equals(deletion.FromSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Installed file does not match the expected deletion base: {path}");
                stateHashes[path] = null;
                finalWrites.Remove(path);
                finalDeletes.Add(path);
            }

            targetManifest = manifest.TargetManifest;
            previousVersion = patchRelease.Version;
        }

        if (targetManifest is null || latest.Version is null || previousVersion.CompareTo(latest.Version) != 0)
            throw new InvalidDataException("The patch chain does not reach the latest release.");

        var workOutput = Path.Combine(_work, "patch-output");
        Directory.CreateDirectory(workOutput);
        foreach (var pair in finalWrites)
        {
            var output = ResolveSafePath(workOutput, pair.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllBytesAsync(output, pair.Value).ConfigureAwait(true);
        }
        return new PatchResult(targetManifest, finalDeletes, finalWrites);
    }

    private async Task<string?> GetCurrentHashAsync(string path, Dictionary<string, string?> stateHashes)
    {
        if (stateHashes.TryGetValue(path, out var value)) return value;
        var localPath = ResolveSafePath(_root, path);
        if (!File.Exists(localPath))
        {
            stateHashes[path] = null;
            return null;
        }
        await using var stream = File.OpenRead(localPath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(true));
        stateHashes[path] = hash;
        return hash;
    }

    private async Task ApplyPatchResultAsync(PatchResult result)
    {
        Report("Applying patch files…", 82);
        var writes = new Dictionary<string, byte[]>(result.FinalWrites, StringComparer.OrdinalIgnoreCase);
        var deletes = result.Deletions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        await ApplyTransactionAsync(writes, deletes, 83, 94).ConfigureAwait(true);
        var newState = new ManagedState
        {
            Version = result.Manifest.Version,
            Kind = "patch",
            Files = result.Manifest.Files.Where(f => !IsProtectedPath(f.Path)).ToList()
        };
        await File.WriteAllTextAsync(Path.Combine(_root, StateFileName), JsonSerializer.Serialize(newState, JsonOptions)).ConfigureAwait(true);
        var skinFiles = result.Manifest.Files.Where(f => IsDefaultSkinPath(f.Path)).ToList();
        await File.WriteAllTextAsync(Path.Combine(_root, SkinStateFileName), JsonSerializer.Serialize(new SkinState
        {
            Version = result.Manifest.Version,
            Commit = null,
            Files = skinFiles.Select(f => f.Path).ToList()
        }, JsonOptions)).ConfigureAwait(true);
        var targetRelease = new ReleaseInfo(result.Manifest.Version, UpdateCheckServiceCompat.ParseVersion(result.Manifest.Version), false, false, []);
        await ScheduleRestartAsync(targetRelease).ConfigureAwait(true);
    }

    private async Task<SkinDownloadResult> DownloadMainSkinAsync(int startPercent)
    {
        var repo = _options.Repository;
        using var commitResponse = await Client.GetAsync($"https://api.github.com/repos/{repo}/commits/main").ConfigureAwait(true);
        commitResponse.EnsureSuccessStatusCode();
        using var commitDoc = JsonDocument.Parse(await commitResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(true));
        var commit = GetString(commitDoc.RootElement, "sha") ?? throw new InvalidDataException("Could not resolve the main branch commit.");

        using var treeResponse = await Client.GetAsync($"https://api.github.com/repos/{repo}/git/trees/{commit}?recursive=1").ConfigureAwait(true);
        treeResponse.EnsureSuccessStatusCode();
        using var treeDoc = JsonDocument.Parse(await treeResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(true));
        if (treeDoc.RootElement.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
            throw new InvalidDataException("The GitHub repository tree was truncated; refusing an incomplete skin update.");
        if (!treeDoc.RootElement.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub did not return a repository tree.");

        var entries = new List<(string Path, string Sha, long Size)>();
        foreach (var node in tree.EnumerateArray())
        {
            var path = GetString(node, "path");
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("Skins/Default/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(GetString(node, "type"), "blob", StringComparison.OrdinalIgnoreCase)) continue;
            var relative = path["Skins/Default/".Length..];
            if (string.IsNullOrWhiteSpace(relative)) continue;
            if (node.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var size) && size >= 0)
                entries.Add(("Skins/Default/" + relative.Replace('\\', '/'), GetString(node, "sha") ?? string.Empty, size));
        }
        if (entries.Count == 0) throw new InvalidDataException("No files were found under Skins/Default on main.");

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var previousSkinState = await ReadSkinStateAsync().ConfigureAwait(true);
        var total = entries.Sum(e => (double)e.Size);
        double completed = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var uri = $"https://raw.githubusercontent.com/{repo}/{commit}/{string.Join('/', entry.Path.Split('/').Select(Uri.EscapeDataString))}";
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(true);
            using var output = new MemoryStream((int)Math.Min(entry.Size, 16 * 1024 * 1024));
            await input.CopyToAsync(output).ConfigureAwait(true);
            var bytes = output.ToArray();
            if (bytes.LongLength != entry.Size || !GitBlobSha1(bytes).Equals(entry.Sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Default skin file failed GitHub verification: {entry.Path}");
            files[entry.Path] = bytes;
            completed += entry.Size;
            var ratio = total > 0 ? completed / total : (double)(i + 1) / entries.Count;
            Report("Downloading Default skin from main…", startPercent + (int)(ratio * 8), $"{i + 1} / {entries.Count} files");
        }

        // Remove only files recorded as previously managed by this updater. An
        // installation without skin state is never scanned for speculative deletions.
        var currentPaths = files.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedPaths = (previousSkinState?.Files ?? [])
            .Select(NormalizeArchivePath)
            .Where(path => IsDefaultSkinPath(path) && !currentPaths.Contains(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new SkinDownloadResult(files, commit, removedPaths);
    }

    private async Task ApplyTransactionAsync(Dictionary<string, byte[]> writes, HashSet<string> deletions, int startPercent, int endPercent)
    {
        var actualWrites = writes.Where(p => !IsProtectedPath(p.Key) &&
            !p.Key.Equals(StateFileName, StringComparison.OrdinalIgnoreCase) && !p.Key.Equals(SkinStateFileName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => NormalizeArchivePath(p.Key), p => p.Value, StringComparer.OrdinalIgnoreCase);
        var actualDeletes = deletions.Where(p => !IsProtectedPath(p) &&
            !p.Equals(StateFileName, StringComparison.OrdinalIgnoreCase) && !p.Equals(SkinStateFileName, StringComparison.OrdinalIgnoreCase))
            .Select(NormalizeArchivePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var backup = Path.Combine(_work, "rollback");
        Directory.CreateDirectory(backup);
        var touched = new List<(string Destination, string? Backup, bool Existed)>();
        var operations = actualWrites.Count + actualDeletes.Count;
        var done = 0;
        try
        {
            foreach (var pair in actualWrites)
            {
                var destination = ResolveSafePath(_root, pair.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string? backupPath = null;
                var existed = File.Exists(destination);
                if (existed)
                {
                    backupPath = ResolveSafePath(backup, pair.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.Copy(destination, backupPath, true);
                }
                touched.Add((destination, backupPath, existed));
                await File.WriteAllBytesAsync(destination, pair.Value).ConfigureAwait(true);
                done++;
                Report("Installing game files…", startPercent + (operations == 0 ? 0 : done * (endPercent - startPercent) / operations), pair.Key);
            }

            foreach (var relative in actualDeletes)
            {
                var destination = ResolveSafePath(_root, relative);
                var existed = File.Exists(destination);
                string? backupPath = null;
                if (existed)
                {
                    backupPath = ResolveSafePath(backup, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.Copy(destination, backupPath, true);
                    File.Delete(destination);
                }
                touched.Add((destination, backupPath, existed));
                done++;
                Report("Removing obsolete files…", startPercent + (operations == 0 ? 0 : done * (endPercent - startPercent) / operations), relative);
            }
        }
        catch
        {
            Report("Rolling back failed update…", startPercent);
            foreach (var item in touched.AsEnumerable().Reverse())
            {
                try
                {
                    if (item.Existed && item.Backup is not null && File.Exists(item.Backup))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
                        File.Copy(item.Backup, item.Destination, true);
                    }
                    else if (File.Exists(item.Destination)) File.Delete(item.Destination);
                }
                catch { }
            }
            throw;
        }
    }

    private HashSet<string> FindObsoleteManagedPaths(ManagedState? oldState, PackageManifest newManifest, bool isMinimal)
    {
        var newPaths = newManifest.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in oldState?.Files ?? [])
        {
            if (newPaths.Contains(file.Path) || IsProtectedPath(file.Path)) continue;
            if (isMinimal && IsDefaultSkinPath(file.Path)) continue;
            result.Add(file.Path);
        }
        return result;
    }

    private async Task SaveSkinStateAsync(IEnumerable<string> currentPaths, string? commit, string version)
    {
        var state = new SkinState { Version = version, Commit = commit, Files = currentPaths.Where(IsDefaultSkinPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList() };
        await File.WriteAllTextAsync(Path.Combine(_root, SkinStateFileName), JsonSerializer.Serialize(state, JsonOptions)).ConfigureAwait(true);
    }

    private async Task SaveSkinStateFromPackageAsync(IEnumerable<ManagedFile> files, string version)
    {
        await SaveSkinStateAsync(files.Where(f => IsDefaultSkinPath(f.Path)).Select(f => f.Path), null, version).ConfigureAwait(true);
    }

    private async Task<SkinState?> ReadSkinStateAsync()
    {
        var path = Path.Combine(_root, SkinStateFileName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<SkinState>(await File.ReadAllTextAsync(path).ConfigureAwait(true), JsonOptions); }
        catch { return null; }
    }

    private static async Task<ManagedState?> ReadStateAsync(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ManagedState>(await File.ReadAllTextAsync(path).ConfigureAwait(true), JsonOptions); }
        catch { return null; }
    }

    private async Task ScheduleRestartAsync(ReleaseInfo target)
    {
        // The updater runs from a temporary copy of TSC.exe, so the installed
        // executable and runtime files are not locked when the game restarts.
        var scriptPath = Path.Combine(_work, "finish-update.ps1");
        var script = "param([int]$UpdaterProcessId,[string]$InstallRoot,[string]$WorkRoot)\n" +
                     "$ErrorActionPreference = 'Stop'\n" +
                     "while (Get-Process -Id $UpdaterProcessId -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 250 }\n" +
                     "$legacyUpdater = Join-Path $InstallRoot 'updater'\n" +
                     "if (Test-Path -LiteralPath $legacyUpdater) { Remove-Item -LiteralPath $legacyUpdater -Recurse -Force -ErrorAction SilentlyContinue }\n" +
                     "$nextUpdater = Join-Path $InstallRoot 'updater.next'\n" +
                     "$oldUpdater = Join-Path $InstallRoot 'updater.old'\n" +
                     "if (Test-Path -LiteralPath $nextUpdater) { Remove-Item -LiteralPath $nextUpdater -Recurse -Force -ErrorAction SilentlyContinue }\n" +
                     "if (Test-Path -LiteralPath $oldUpdater) { Remove-Item -LiteralPath $oldUpdater -Recurse -Force -ErrorAction SilentlyContinue }\n" +
                     "$game = Join-Path $InstallRoot 'TSC.exe'\n" +
                     "if (Test-Path -LiteralPath $game) { Start-Process -FilePath $game -WorkingDirectory $InstallRoot }\n" +
                     "Start-Sleep -Milliseconds 350\n" +
                     "Remove-Item -LiteralPath $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue\n";
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(true);

        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) powershell = "powershell.exe";
        var start = new ProcessStartInfo { FileName = powershell, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File"); start.ArgumentList.Add(scriptPath);
        start.ArgumentList.Add("-UpdaterProcessId"); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("-InstallRoot"); start.ArgumentList.Add(_root);
        start.ArgumentList.Add("-WorkRoot"); start.ArgumentList.Add(_work);
        _ = Process.Start(start) ?? throw new InvalidOperationException("Could not start the update handoff process.");
        _restartScheduled = true;
        Report("Restarting TSC…", 99, $"Installed {target.TagName}");
    }

    private static ReleaseAsset? FindAsset(ReleaseInfo release, string name) => release.Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    private static string GetReleaseManifestAssetName(string version) => $"TSC-win-x64-{version}-manifest.json";
    // Kept for reading releases created before the unified manifest format.
    private static string GetPackageManifestAssetName(string version, string kind) => kind.Equals("minimal", StringComparison.OrdinalIgnoreCase)
        ? $"TSC-win-x64-{version}-minimal-manifest.json"
        : GetReleaseManifestAssetName(version);
    private static string GetPatchManifestAssetName(string version) => $"TSC-win-x64-{version}-patch-manifest.json";

    private async Task<PackageManifest?> ReadPackageManifestAssetAsync(string path, string assetName, string expectedVersion, string expectedKind)
    {
        var info = new FileInfo(path);
        if (info.Length > 16 * 1024 * 1024) throw new InvalidDataException($"Manifest asset {assetName} is unexpectedly large.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(true);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"Manifest asset {assetName} is not a JSON object.");
        if (root.TryGetProperty("full", out _) || root.TryGetProperty("minimal", out _))
        {
            var combined = root.Deserialize<ReleaseManifest>(JsonOptions)
                ?? throw new InvalidDataException($"Release manifest {assetName} is empty.");
            if (combined.SchemaVersion != 2 || !string.Equals(combined.Version, expectedVersion, StringComparison.Ordinal))
                throw new InvalidDataException($"Release manifest {assetName} has the wrong schema or version.");
            var selected = expectedKind.Equals("minimal", StringComparison.OrdinalIgnoreCase) ? combined.Minimal : combined.Full;
            if (selected is null)
                throw new InvalidDataException($"Release manifest {assetName} does not describe the {expectedKind} package.");
            if (!string.Equals(selected.Version, expectedVersion, StringComparison.Ordinal) ||
                !string.Equals(selected.Kind, expectedKind, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Release manifest {assetName} contains invalid {expectedKind} package metadata.");
            return selected;
        }

        // Schema v1 package manifests were separate assets in older releases. If
        // this is a legacy full manifest but the selected ZIP is minimal, ignore it
        // and let the archive's embedded manifest be used when available.
        var legacy = root.Deserialize<PackageManifest>(JsonOptions)
            ?? throw new InvalidDataException($"Package manifest {assetName} is empty.");
        return string.Equals(legacy.Version, expectedVersion, StringComparison.Ordinal) &&
               string.Equals(legacy.Kind, expectedKind, StringComparison.OrdinalIgnoreCase)
            ? legacy
            : null;
    }

    private static async Task<PatchManifest?> ReadPatchManifestAssetAsync(string path, string assetName)
    {
        var info = new FileInfo(path);
        if (info.Length > 16 * 1024 * 1024) throw new InvalidDataException($"Manifest asset {assetName} is unexpectedly large.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(true);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"Manifest asset {assetName} is not a JSON object.");
        if (root.TryGetProperty("patch", out _))
        {
            var combined = root.Deserialize<ReleaseManifest>(JsonOptions)
                ?? throw new InvalidDataException($"Release manifest {assetName} is empty.");
            var patch = combined.Patch;
            if (patch is null) return null;
            patch.TargetManifest ??= combined.Full;
            return patch;
        }
        if (root.TryGetProperty("fromVersion", out _) && root.TryGetProperty("toVersion", out _))
            return root.Deserialize<PatchManifest>(JsonOptions)
                ?? throw new InvalidDataException($"Patch manifest {assetName} is empty.");

        // Some legacy releases have a package-only manifest asset alongside a patch
        // ZIP whose patch manifest is embedded. Let the caller try that archive entry.
        return null;
    }

    private static async Task<T> ReadJsonAssetAsync<T>(string path, string assetName) where T : class
    {
        var info = new FileInfo(path);
        if (info.Length > 16 * 1024 * 1024) throw new InvalidDataException($"Manifest asset {assetName} is unexpectedly large.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions).ConfigureAwait(true)
            ?? throw new InvalidDataException($"Manifest asset {assetName} is empty or invalid.");
    }

    private void Report(string status, int percent, string? detail = null) => _progress(status, Math.Clamp(percent, 0, 100), detail);

    private static bool IsProtectedPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var parts = normalized.Split('/');
        if (parts.Length == 0) return true;
        if (parts[0].Equals("songs", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals("save", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals("logs", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals("state", StringComparison.OrdinalIgnoreCase)) return true;
        if (parts[0].Equals("skins", StringComparison.OrdinalIgnoreCase) &&
            (parts.Length < 3 || !parts[1].Equals("Default", StringComparison.OrdinalIgnoreCase))) return true;
        if (parts.Length == 1)
        {
            string[] protectedFiles = ["gameconfig.json", "keybinds.json", "tsc_crash.log", "info.md", "version.txt", StateFileName, SkinStateFileName];
            if (protectedFiles.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) return true;
        }
        return parts.Length >= 2 &&
               parts[0].Equals("tools", StringComparison.OrdinalIgnoreCase) &&
               parts[^1].Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDefaultSkinPath(string path) => path.Replace('\\', '/').StartsWith("Skins/Default/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Archive contains an empty file path.");
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':')) throw new InvalidDataException($"Unsafe archive path: {path}");
        var parts = normalized.Split('/');
        if (parts.Any(part => part.Length == 0 || part == "." || part == "..")) throw new InvalidDataException($"Unsafe archive path: {path}");
        return string.Join('/', parts);
    }

    private static string ResolveSafePath(string root, string relative)
    {
        var clean = NormalizeArchivePath(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, clean.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Path escapes target directory: {relative}");
        return full;
    }

    private static bool HashMatches(byte[] bytes, string expected) => Sha256(bytes).Equals(expected, StringComparison.OrdinalIgnoreCase);
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string GitBlobSha1(byte[] bytes)
    {
        var header = Encoding.UTF8.GetBytes($"blob {bytes.LongLength}\0");
        using var sha = SHA1.Create();
        sha.TransformBlock(header, 0, header.Length, header, 0);
        sha.TransformFinalBlock(bytes, 0, bytes.Length);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }


    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.0} {units[unit]}";
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TSC", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed record ReleaseInfo(string TagName, Version? Version, bool IsDraft, bool IsPrerelease, List<ReleaseAsset> Assets);
    private sealed record ReleaseAsset(string Name, string DownloadUrl, long Size, string? Digest);
    private sealed record PatchRelease(Version FromVersion, Version Version, ReleaseInfo Release, ReleaseAsset Asset, ReleaseAsset? ManifestAsset);
    private sealed record PreparedPackage(PackageManifest Manifest, Dictionary<string, byte[]> Files);
    private sealed record PatchResult(PackageManifest Manifest, HashSet<string> Deletions, Dictionary<string, byte[]> FinalWrites);
    private sealed record SkinDownloadResult(Dictionary<string, byte[]> Files, string Commit, HashSet<string> DeletedPaths);

    private sealed class PackageManifest { public int SchemaVersion { get; set; } = 1; public string Version { get; set; } = ""; public string Kind { get; set; } = "full"; public List<ManagedFile> Files { get; set; } = []; }
    private sealed class ReleaseManifest { public int SchemaVersion { get; set; } = 2; public string Version { get; set; } = ""; public PackageManifest? Full { get; set; } public PackageManifest? Minimal { get; set; } public PatchManifest? Patch { get; set; } }
    private sealed class ManagedFile { public string Path { get; set; } = ""; public long Size { get; set; } public string Sha256 { get; set; } = ""; }
    private sealed class PatchManifest { public int SchemaVersion { get; set; } = 1; public string FromVersion { get; set; } = ""; public string ToVersion { get; set; } = ""; public List<PatchFile> Files { get; set; } = []; public List<PatchDeletion> DeletedFiles { get; set; } = []; public PackageManifest? TargetManifest { get; set; } }
    private sealed class PatchFile { public string Path { get; set; } = ""; public string? FromSha256 { get; set; } public long Size { get; set; } public string Sha256 { get; set; } = ""; }
    private sealed class PatchDeletion { public string Path { get; set; } = ""; public string? FromSha256 { get; set; } }
    private sealed class ManagedState { public string Version { get; set; } = ""; public string Kind { get; set; } = "full"; public List<ManagedFile> Files { get; set; } = []; }
    private sealed class SkinState { public string Version { get; set; } = ""; public string? Commit { get; set; } public List<string> Files { get; set; } = []; }
}

internal sealed record UpdateOptions(string Repository, string InstallDirectory, string CurrentVersion, int? WaitPid)
{
    public static UpdateOptions Parse(string[] args)
    {
        string? repository = null, install = null, currentVersion = null;
        int? waitPid = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repository" when i + 1 < args.Length: repository = args[++i]; break;
                case "--install-dir" when i + 1 < args.Length: install = args[++i]; break;
                case "--current-version" when i + 1 < args.Length: currentVersion = args[++i]; break;
                case "--wait-pid" when i + 1 < args.Length && int.TryParse(args[++i], out var pid): waitPid = pid; break;
                default: throw new ArgumentException("Usage: TSC.exe --updater --repository owner/repo --install-dir <folder> --current-version <version> [--wait-pid <pid>]");
            }
        }
        if (string.IsNullOrWhiteSpace(repository) || repository.Split('/').Length != 2 || repository.Contains(".."))
            throw new ArgumentException("--repository must be in owner/repo format.");
        if (string.IsNullOrWhiteSpace(install)) throw new ArgumentException("--install-dir is required.");
        if (string.IsNullOrWhiteSpace(currentVersion)) throw new ArgumentException("--current-version is required.");
        return new UpdateOptions(repository, install, currentVersion, waitPid);
    }
}

internal static class UpdateCheckServiceCompat
{
    public static Version? ParseVersion(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var normalized = input.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V')) normalized = normalized[1..];
        var match = System.Text.RegularExpressions.Regex.Match(normalized, @"^\d+(?:\.\d+){1,3}");
        return Version.TryParse(match.Value, out var version) ? version : null;
    }
}
