using EmptyEngine.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmptyEngine.Editor.Assets;

/// <summary>ソースアセットの置き場 1 つ</summary>
/// <param name="Path">置き場の実パス</param>
/// <param name="DisplayName">表示パスの前置き。空ならこの置き場からの相対パスがそのまま表示パスになる。</param>
/// <param name="IsReadOnly"><c>true</c> の置き場へは何も書き込まない。</param>
public sealed record AssetSource(string Path, string DisplayName, bool IsReadOnly = false);

public sealed class AssetImportService
{
    private const string MetaExtension = ".meta";

    private readonly AssetCatalog _catalog;
    private readonly AssetSource[] _sources;
    private readonly IReadOnlyDictionary<string, IAssetImporter> _importersByExtension;
    private readonly ILogger _logger;
    private readonly string _stampRoot;
    private readonly IFileIdentity _fileIdentity;
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private EditorArtifacts? _artifacts;
    private HashSet<string> _catalogFingerprint = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedMissingMeta = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="catalog">取り込み結果の書き込み先</param>
    /// <param name="sources">取り込むソースアセットの置き場</param>
    /// <param name="stampRootPath">取り込みスタンプの置き場</param>
    public AssetImportService(
        AssetCatalog catalog,
        IEnumerable<AssetSource> sources,
        IEnumerable<IAssetImporter> importers,
        string stampRootPath,
        ILogger<AssetImportService>? logger = null,
        IFileIdentity? fileIdentity = null)
    {
        _catalog = catalog;
        _sources = sources
            .Select(source => source with { Path = Path.GetFullPath(source.Path) })
            .ToArray();
        _fileIdentity = fileIdentity ?? new NativeFileIdentity();
        _stampRoot = Path.GetFullPath(stampRootPath);
        _logger = logger ?? NullLogger<AssetImportService>.Instance;

        var map = new Dictionary<string, IAssetImporter>(StringComparer.OrdinalIgnoreCase);
        foreach (IAssetImporter importer in importers)
        {
            foreach (string ext in importer.SupportedExtensions)
            {
                map[NormalizeExtension(ext)] = importer;
            }
        }

        _importersByExtension = map;
    }

    /// <summary>アーティファクトを書き換えたときのキー集合の通知</summary>
    public event Action<IReadOnlyCollection<string>>? ArtifactsChanged;

    /// <summary>取り込むソースアセットの置き場</summary>
    public IReadOnlyList<AssetSource> Sources => _sources;

    /// <summary>ソースごとの guid を決め、カタログの guid からの引き当てを作り直す</summary>
    private Dictionary<string, string> IndexSources(IReadOnlyList<string> files)
    {
        var guidByFile = files.ToDictionary(f => f, ArtifactKeyFor, StringComparer.OrdinalIgnoreCase);
        var sourceByGuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string file, string guid) in guidByFile) sourceByGuid[guid] = file;
        _catalog.SetSources(sourceByGuid);
        return guidByFile;
    }

    /// <summary>ファイルを含む置き場（入れ子のときは最も深いもの）</summary>
    private AssetSource? SourceOf(string fullPath) =>
        _sources.Where(source => IsUnder(source.Path, fullPath)).MaxBy(source => source.Path.Length);

    private static bool IsUnder(string directory, string fullPath) =>
        fullPath.StartsWith(EnsureTrailingSeparator(directory), StringComparison.OrdinalIgnoreCase);

    public void SetArtifacts(EditorArtifacts artifacts) => _artifacts = artifacts;

    public bool TryGetImporter(string extension, out IAssetImporter? importer) =>
        _importersByExtension.TryGetValue(NormalizeExtension(extension), out importer);

    /// <summary>拡張子に結び付いたインポータの、求める役ができるものとしての取り出し</summary>
    public bool TryGetImporter<T>(string extension, out T importer) where T : class, IAssetImporter
    {
        if (TryGetImporter(extension, out IAssetImporter? found) && found is T typed)
        {
            importer = typed;
            return true;
        }

        importer = null!;
        return false;
    }

    /// <summary>その役ができるインポータが拡張子に結び付いているか</summary>
    public bool HasImporter<T>(string extension) where T : class, IAssetImporter =>
        TryGetImporter<T>(extension, out _);

    /// <summary>全アセットの再インポート</summary>
    public Task<IReadOnlyList<AssetImportResult>> ImportAllAsync(CancellationToken cancellationToken = default) =>
        RunImportAsync(() => ImportInternalAsync(force: true, cancellationToken), cancellationToken);

    /// <summary>ソースに差分のあるアセットだけの再インポート</summary>
    public Task<IReadOnlyList<AssetImportResult>> ImportIfChangedAsync(CancellationToken cancellationToken = default) =>
        RunImportAsync(() => ImportInternalAsync(force: false, cancellationToken), cancellationToken);

    /// <summary>インポートを UI の同期コンテキスト外で順番に実行する</summary>
    private async Task<T> RunImportAsync<T>(Func<Task<T>> import, CancellationToken cancellationToken)
    {
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(import, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _importGate.Release();
        }
    }

    private async Task<IReadOnlyList<AssetImportResult>> ImportInternalAsync(bool force, CancellationToken cancellationToken)
    {
        foreach (AssetSource source in _sources.Where(source => !source.IsReadOnly))
            Directory.CreateDirectory(source.Path);

        string[] files = EnumerateImportableFiles();

        RecoverMovedSources(files);

        Dictionary<string, string> baseGuidByFile = IndexSources(files);
        HashSet<string> deletedKeys = CleanupDeletedSources(
            baseGuidByFile.Values.ToHashSet(StringComparer.OrdinalIgnoreCase), cancellationToken);

        bool anyStale = force || files.Any(f => IsArtifactStale(f, baseGuidByFile[f]));
        if (!force && !anyStale && deletedKeys.Count == 0)
        {
            var currentFingerprint = files
                .SelectMany(f => ReadStampEntries(baseGuidByFile[f])
                    .Select(e => FingerprintOf(RelativeKey(f), e.Key)))
                .ToHashSet(StringComparer.Ordinal);
            if (currentFingerprint.Count > 0 && _catalogFingerprint.SetEquals(currentFingerprint))
            {
                return Array.Empty<AssetImportResult>();
            }
        }

        var results = new List<AssetImportResult>();
        var importedAssets = new Dictionary<string, ImportedSource>(StringComparer.Ordinal);
        var reimportedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updatedKeys = new HashSet<string>(deletedKeys, StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = RelativeKey(file);
            string baseGuid = baseGuidByFile[file];
            bool isScene = IsSceneSource(relative);

            IReadOnlyList<ImportedSource>? assets = force || IsArtifactStale(file, baseGuid)
                ? null
                : RehydrateAll(file, relative, baseGuid, isScene);

            if (assets is null)
            {
                AssetImportResult result = await ImportOneAsync(file, baseGuid, cancellationToken);
                results.Add(result);
                assets = result.Success ? result.Assets : Array.Empty<ImportedSource>();
                reimportedFiles.Add(file);
                foreach (ImportedSource asset in assets) updatedKeys.Add(asset.Key);
            }

            foreach (ImportedSource asset in assets) importedAssets[asset.Key] = asset;
        }

        results.AddRange(await ReimportDependentsAsync(files, baseGuidByFile, updatedKeys, reimportedFiles,
            asset => importedAssets[asset.Key] = asset, cancellationToken));

        _catalog.Update(importedAssets);
        _catalogFingerprint = importedAssets.Values
            .Select(a => FingerprintOf(a.RelativePath, a.Key))
            .ToHashSet(StringComparer.Ordinal);

        _logger.LogInformation(
            "Asset import finished. assets={Assets}, reimported={Reimported}, source={Source}",
            importedAssets.Count, results.Count, string.Join(';', _sources.Select(source => source.Path)));
        if (results.Count > 0 || deletedKeys.Count > 0) ArtifactsChanged?.Invoke(updatedKeys);
        return results;
    }

    public Task<AssetImportResult> ImportSingleAsync(string sourcePath, CancellationToken cancellationToken = default) =>
        RunImportAsync(() => ImportSingleInternalAsync(sourcePath, cancellationToken), cancellationToken);

    private async Task<AssetImportResult> ImportSingleInternalAsync(
        string sourcePath, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(sourcePath);
        if (!HasUsableGuid(fullPath))
            return AssetImportResult.Failed($"No .meta next to read-only source: {fullPath}");

        RecoverMovedSources(new[] { fullPath });
        string[] files = EnumerateImportableFiles();
        Dictionary<string, string> baseGuidByFile = IndexSources(files);
        AssetImportResult result = await ImportOneAsync(fullPath, ArtifactKeyFor(fullPath), cancellationToken);
        if (result.Success)
        {
            foreach (ImportedSource asset in result.Assets)
            {
                _catalog.AddOrUpdate(asset);
                _catalogFingerprint.Add(FingerprintOf(asset.RelativePath, asset.Key));
            }
        }

        string ext = NormalizeExtension(Path.GetExtension(sourcePath));
        string relative = result.Asset?.RelativePath ?? Path.GetFileName(sourcePath);
        if (result.Success)
            _logger.LogInformation("Import ok [{Extension}] {Asset}", ext, relative);

        if (result.Success)
        {
            var updatedKeys = result.Assets.Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fullPath };
            List<AssetImportResult> cascaded = await ReimportDependentsAsync(
                files, baseGuidByFile, updatedKeys, processedFiles,
                asset =>
                {
                    _catalog.AddOrUpdate(asset);
                    _catalogFingerprint.Add(FingerprintOf(asset.RelativePath, asset.Key));
                }, cancellationToken);
            if (cascaded.Count > 0)
                _logger.LogInformation("Reimported {Count} dependent source(s) of {Asset}", cascaded.Count, relative);
            ArtifactsChanged?.Invoke(updatedKeys);
        }

        return result;
    }

    private string[] EnumerateImportableFiles() =>
        _sources.Select(source => source.Path)
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            .Where(path => _importersByExtension.ContainsKey(NormalizeExtension(Path.GetExtension(path))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(HasUsableGuid)
            .ToArray();

    /// <summary>guid を決められるソースか</summary>
    /// <remarks>読み取り専用の置き場へは <c>.meta</c> を書けないので、同梱されていないソースは取り込まない</remarks>
    private bool HasUsableGuid(string fullPath)
    {
        if (SourceOf(fullPath) is not { IsReadOnly: true }) return true;
        if (IsSafeArtifactId(TryReadGuid(fullPath + MetaExtension))) return true;

        if (_reportedMissingMeta.Add(fullPath))
        {
            _logger.LogWarning(
                "Skipped {Asset}: it is in a read-only source and has no .meta. Ship the .meta next to it.",
                RelativeKey(fullPath));
        }

        return false;
    }

    private async Task<List<AssetImportResult>> ReimportDependentsAsync(
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, string> baseGuidByFile,
        HashSet<string> updatedKeys,
        HashSet<string> processedFiles,
        Action<ImportedSource> onImported,
        CancellationToken cancellationToken)
    {
        var results = new List<AssetImportResult>();
        if (updatedKeys.Count == 0) return results;

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (string file in files)
            {
                if (processedFiles.Contains(file)) continue;
                if (!ReadStampDependencies(baseGuidByFile[file]).Any(updatedKeys.Contains)) continue;

                cancellationToken.ThrowIfCancellationRequested();
                processedFiles.Add(file);
                changed = true;

                AssetImportResult result = await ImportOneAsync(file, baseGuidByFile[file], cancellationToken);
                results.Add(result);
                if (!result.Success) continue;

                foreach (ImportedSource asset in result.Assets)
                {
                    updatedKeys.Add(asset.Key);
                    onImported(asset);
                }
            }
        }

        return results;
    }

    private async Task<AssetImportResult> ImportOneAsync(string sourcePath, string baseGuid, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(sourcePath);
        if (SourceOf(fullPath) is null)
        {
            AssetImportResult failure = AssetImportResult.Failed($"Skip outside asset sources: {fullPath}");
            _logger.LogWarning("Import failed {Path}: {Error}", fullPath, failure.Message);
            return failure;
        }

        string ext = NormalizeExtension(Path.GetExtension(fullPath));
        if (!_importersByExtension.TryGetValue(ext, out IAssetImporter? importer))
        {
            AssetImportResult failure = AssetImportResult.Failed($"No importer for extension '{ext}' ({fullPath})");
            _logger.LogWarning("Import failed [{Extension}] {Path}: {Error}", ext, fullPath, failure.Message);
            return failure;
        }

        string relative = RelativeKey(fullPath);
        var request = new AssetImportRequest(fullPath, relative);

        try
        {
            AssetImportResult result = await importer.ImportAsync(request, cancellationToken);
            if (!result.Success)
            {
                _logger.LogError("Import failed [{Extension}] {Asset}: {Error}", ext, relative, result.Message);
                return result;
            }

            var stamped = new List<ImportedSource>(result.Assets.Count);
            var stampEntries = new List<(string LocalId, string Key)>(result.Assets.Count);

            foreach (ImportedSource asset in result.Assets)
            {
                string key = ComposeKey(baseGuid, asset.LocalId);
                ImportedSource a = asset with { Key = key };

                if (_artifacts is not null)
                {
                    switch (a)
                    {
                        case ImportedScene scene:
                            await _artifacts.SaveSceneAsync(
                                new AssetKey(key), new[] { scene.Root }, cancellationToken);
                            break;

                        case ImportedAsset value:
                            await _artifacts.SaveAssetAsync(new AssetKey(key), value.Value, cancellationToken);

                            // カタログが本体データを保持し続けないよう、保存済みアーティファクトの参照へ差し替える。
                            if (_artifacts.LoadAsset(new AssetKey(key)) is { } stored)
                                a = value with { Value = stored };
                            break;
                    }
                }

                stamped.Add(a);
                stampEntries.Add((a.LocalId, key));
            }

            WriteStamp(baseGuid, stampEntries, result.Dependencies, ReadIdentity(fullPath));

            return result with { Assets = stamped };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Asset import exception [{Extension}] {Asset}", ext, relative);
            return AssetImportResult.Failed($"Importer threw for {relative}: {ex.Message}");
        }
    }

    /// <summary>ソースの同一性ヒント</summary>
    private readonly record struct SourceIdentity(string FileId, long Size, long ModifiedTicks);

    /// <summary><c>.meta</c> を失ったソースの元 guid への繋ぎ直し</summary>
    private void RecoverMovedSources(IReadOnlyList<string> files)
    {
        var unclaimed = files
            .Where(f => SourceOf(f) is { IsReadOnly: false } && !File.Exists(f + MetaExtension))
            .ToList();
        if (unclaimed.Count == 0) return;

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orphanMetas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string meta in EnumerateMetaFiles())
        {
            string? guid = TryReadGuid(meta);
            if (string.IsNullOrWhiteSpace(guid)) continue;
            if (File.Exists(meta[..^MetaExtension.Length])) claimed.Add(guid!);
            else orphanMetas[guid!] = meta;
        }

        IReadOnlyDictionary<string, SourceIdentity> identities = ReadStampIdentities();
        if (identities.Count == 0) return;

        var byFileId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bySignature = new Dictionary<(long Size, long Ticks), List<string>>();
        foreach ((string guid, SourceIdentity id) in identities)
        {
            if (id.FileId.Length > 0) byFileId.TryAdd(id.FileId, guid);

            if (!bySignature.TryGetValue((id.Size, id.ModifiedTicks), out List<string>? sharing))
                bySignature[(id.Size, id.ModifiedTicks)] = sharing = new List<string>();
            sharing.Add(guid);
        }

        var matches = new List<(string File, string Guid)>();
        foreach (string file in unclaimed)
        {
            SourceIdentity? current = ReadIdentity(file);
            if (current is not { } id) continue;

            if (id.FileId.Length > 0 && byFileId.TryGetValue(id.FileId, out string? byId))
                matches.Add((file, byId));
            else if (bySignature.TryGetValue((id.Size, id.ModifiedTicks), out List<string>? sharing) && sharing.Count == 1)
                matches.Add((file, sharing[0]));
        }

        foreach (IGrouping<string, (string File, string Guid)> group in
                 matches.GroupBy(m => m.Guid, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() > 1)
            {
                _logger.LogWarning(
                    "Ambiguous move candidates for guid {Guid}; leaving them to get new guids: {Candidates}",
                    group.Key, string.Join(", ", group.Select(m => RelativeKey(m.File))));
                continue;
            }

            if (claimed.Contains(group.Key)) continue;

            string file = group.Single().File;
            try
            {
                if (orphanMetas.TryGetValue(group.Key, out string? orphan)) File.Move(orphan, file + MetaExtension);
                else WriteMeta(file + MetaExtension, group.Key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to reattach .meta for {Asset}: {Error}", RelativeKey(file), ex.Message);
                continue;
            }

            _logger.LogInformation("Recovered moved asset -> '{Asset}' (guid {Guid})", RelativeKey(file), group.Key);
        }
    }

    /// <summary>書き込める置き場の <c>.meta</c></summary>
    private IEnumerable<string> EnumerateMetaFiles() =>
        _sources.Where(source => !source.IsReadOnly && Directory.Exists(source.Path))
            .SelectMany(source => Directory.EnumerateFiles(source.Path, "*" + MetaExtension, SearchOption.AllDirectories));

    /// <summary>移動復元後にも生きたソースを持たない stamp とその成果物を取り除く</summary>
    private HashSet<string> CleanupDeletedSources(
        IReadOnlySet<string> liveBaseGuids,
        CancellationToken cancellationToken)
    {
        var deletedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_stampRoot)) return deletedKeys;

        foreach (string stampPath in Directory.EnumerateFiles(_stampRoot, "*.stamp"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string baseGuid = Path.GetFileNameWithoutExtension(stampPath);
            if (liveBaseGuids.Contains(baseGuid)) continue;

            int sourceAssetCount = 0;
            bool artifactDeleteFailed = false;
            foreach ((_, string key) in ReadStampEntries(baseGuid))
            {
                if (!deletedKeys.Add(key)) continue;
                sourceAssetCount++;
                try
                {
                    _artifacts?.Delete(new AssetKey(key));
                }
                catch (Exception ex)
                {
                    artifactDeleteFailed = true;
                    _logger.LogWarning("Failed to delete artifact '{Key}': {Error}", key, ex.Message);
                }
            }

            if (artifactDeleteFailed) continue;

            try
            {
                File.Delete(stampPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to delete import stamp '{Path}': {Error}", stampPath, ex.Message);
                continue;
            }

            DeleteOrphanMetas(baseGuid);
            _logger.LogInformation(
                "Removed artifacts for deleted source (guid {Guid}, assets={Assets}).", baseGuid, sourceAssetCount);
        }

        return deletedKeys;
    }

    private void DeleteOrphanMetas(string baseGuid)
    {
        foreach (string metaPath in EnumerateMetaFiles().ToArray())
        {
            if (File.Exists(metaPath[..^MetaExtension.Length])) continue;
            if (!string.Equals(TryReadGuid(metaPath), baseGuid, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                File.Delete(metaPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to delete orphan meta '{Path}': {Error}", metaPath, ex.Message);
            }
        }
    }

    private SourceIdentity? ReadIdentity(string sourceFullPath)
    {
        try
        {
            var info = new FileInfo(sourceFullPath);
            if (!info.Exists) return null;
            return new SourceIdentity(_fileIdentity.TryGet(sourceFullPath) ?? string.Empty, info.Length, info.LastWriteTimeUtc.Ticks);
        }
        catch
        {
            return null;
        }
    }

    private static string FingerprintOf(string relativePath, string key) => relativePath + "\n" + key;

    private static string ComposeKey(string baseGuid, string localId)
    {
        if (string.IsNullOrEmpty(localId)) return baseGuid;
        return baseGuid + "/" + localId;
    }

    private IReadOnlyList<ImportedSource>? RehydrateAll(string sourceFullPath, string relative, string baseGuid, bool isScene)
    {
        IReadOnlyList<(string LocalId, string Key)> entries = ReadStampEntries(baseGuid);
        if (entries.Count == 0) return null;

        var restored = new List<ImportedSource>(entries.Count);
        try
        {
            foreach ((string localId, string key) in entries)
            {
                if (isScene)
                {
                    IReadOnlyList<HierarchyNode>? roots = _artifacts?.LoadScene(new AssetKey(key));
                    if (roots is not { Count: > 0 }) return null;
                    restored.Add(new ImportedScene(relative, roots[0], sourceFullPath) { Key = key, LocalId = localId });
                }
                else
                {
                    if (_artifacts?.LoadAsset(new AssetKey(key)) is not { } value) return null;
                    restored.Add(new ImportedAsset(relative, value, sourceFullPath) { Key = key, LocalId = localId });
                }
            }

            return restored;
        }
        catch
        {
            return null;
        }
    }

    private bool IsArtifactStale(string sourceFullPath, string artifactKey)
    {
        string stampPath = StampPath(artifactKey);
        if (!File.Exists(stampPath)) return true;
        if (ReadStampEntries(artifactKey).Count == 0) return true;
        if (File.GetLastWriteTimeUtc(sourceFullPath) > File.GetLastWriteTimeUtc(stampPath)) return true;

        // 読み取り専用の置き場（パッケージ）は、版を替えると更新時刻がスタンプより古いまま中身が入れ替わる。
        return SourceOf(sourceFullPath) is { IsReadOnly: true } && !MatchesStampedIdentity(sourceFullPath, stampPath);
    }

    private bool MatchesStampedIdentity(string sourceFullPath, string stampPath) =>
        TryReadStampIdentity(stampPath) is { } stamped
        && ReadIdentity(sourceFullPath) is { } current
        && stamped.Size == current.Size
        && stamped.ModifiedTicks == current.ModifiedTicks;

    private const string StampDependencyPrefix = "dep:";

    private const string StampIdentityPrefix = "id:";

    private void WriteStamp(
        string baseGuid,
        IReadOnlyList<(string LocalId, string Key)> entries,
        IReadOnlyList<string> dependencies,
        SourceIdentity? identity)
    {
        string stampPath = StampPath(baseGuid);
        Directory.CreateDirectory(Path.GetDirectoryName(stampPath)!);
        IEnumerable<string> lines = entries.Select(e => e.LocalId + "\t" + e.Key)
            .Concat(dependencies
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(d => StampDependencyPrefix + d));

        if (identity is { } id)
            lines = lines.Append(string.Join('\t', StampIdentityPrefix + id.FileId, id.Size, id.ModifiedTicks));

        File.WriteAllLines(stampPath, lines);
    }

    private IReadOnlyList<(string LocalId, string Key)> ReadStampEntries(string baseGuid)
    {
        string stampPath = StampPath(baseGuid);
        if (!File.Exists(stampPath)) return Array.Empty<(string, string)>();

        var list = new List<(string, string)>();
        foreach (string line in File.ReadAllLines(stampPath))
        {
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith(StampDependencyPrefix, StringComparison.Ordinal)) continue;
            if (line.StartsWith(StampIdentityPrefix, StringComparison.Ordinal)) continue;
            int tab = line.IndexOf('\t');
            if (tab >= 0) list.Add((line[..tab], line[(tab + 1)..]));
        }

        return list;
    }

    /// <summary>全スタンプからの同一性ヒントの読み出し</summary>
    private IReadOnlyDictionary<string, SourceIdentity> ReadStampIdentities()
    {
        var map = new Dictionary<string, SourceIdentity>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_stampRoot)) return map;

        foreach (string stampPath in Directory.EnumerateFiles(_stampRoot, "*.stamp"))
        {
            if (TryReadStampIdentity(stampPath) is { } identity)
                map[Path.GetFileNameWithoutExtension(stampPath)] = identity;
        }

        return map;
    }

    private static SourceIdentity? TryReadStampIdentity(string stampPath)
    {
        try
        {
            string? line = File.ReadLines(stampPath)
                .FirstOrDefault(l => l.StartsWith(StampIdentityPrefix, StringComparison.Ordinal));
            if (line is null) return null;

            string[] parts = line[StampIdentityPrefix.Length..].Split('\t');
            if (parts.Length != 3) return null;
            if (!long.TryParse(parts[1], out long size) || !long.TryParse(parts[2], out long ticks)) return null;

            return new SourceIdentity(parts[0], size, ticks);
        }
        catch
        {
            return null;
        }
    }

    private IReadOnlyList<string> ReadStampDependencies(string baseGuid)
    {
        string stampPath = StampPath(baseGuid);
        if (!File.Exists(stampPath)) return Array.Empty<string>();

        return File.ReadAllLines(stampPath)
            .Where(line => line.StartsWith(StampDependencyPrefix, StringComparison.Ordinal))
            .Select(line => line[StampDependencyPrefix.Length..])
            .ToArray();
    }

    private string StampPath(string baseGuid) =>
        Path.Combine(_stampRoot, NormalizeStampName(baseGuid) + ".stamp");

    private static string NormalizeStampName(string baseGuid) =>
        baseGuid.Replace('\\', '/').TrimStart('/').Replace('/', '_');

    private string ArtifactKeyFor(string sourceFullPath) => ResolveOrCreateGuid(sourceFullPath);

    private static string ResolveOrCreateGuid(string sourceFullPath)
    {
        string metaPath = sourceFullPath + MetaExtension;
        if (File.Exists(metaPath))
        {
            string? existing = TryReadGuid(metaPath);
            if (IsSafeArtifactId(existing)) return existing!;
        }

        string guid = Guid.NewGuid().ToString("N");
        WriteMeta(metaPath, guid);
        return guid;
    }

    private static bool IsSafeArtifactId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value is not "." and not ".."
        && value.IndexOfAny(['/', '\\', '\0']) < 0;

    private static void WriteMeta(string metaPath, string guid)
    {
        try
        {
            File.WriteAllText(metaPath, $"{{\"guid\":\"{guid}\"}}");
        }
        catch
        {
        }
    }

    private static string? TryReadGuid(string metaPath)
    {
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(metaPath));
            return doc.RootElement.TryGetProperty("guid", out System.Text.Json.JsonElement g) ? g.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private string RelativeKey(string fullPath)
    {
        string full = Path.GetFullPath(fullPath);
        if (SourceOf(full) is not { } source) return Path.GetFileName(full);

        string relative = Path.GetRelativePath(source.Path, full).Replace('\\', '/');
        return source.DisplayName.Length == 0 ? relative : source.DisplayName + "/" + relative;
    }

    private bool IsSceneSource(string relativeOrPath)
    {
        string ext = NormalizeExtension(Path.GetExtension(relativeOrPath));
        return _importersByExtension.TryGetValue(ext, out IAssetImporter? importer) && importer is ISceneImporter;
    }

    public static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return string.Empty;
        string ext = extension.StartsWith('.') ? extension : "." + extension;
        return ext.ToLowerInvariant();
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
            return path;
        return path + Path.DirectorySeparatorChar;
    }
}
