namespace EmptyEngine.Storage;

/// <summary>Artifact key normalization and filesystem containment.</summary>
public static class AssetPath
{
    /// <summary>Normalizes a hierarchical artifact key without allowing traversal segments.</summary>
    public static string NormalizeKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        string normalized = key.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(static segment => segment is "." or ".."))
        {
            throw new ArgumentException("Artifact keys cannot contain '.' or '..' path segments.", nameof(key));
        }

        return string.Join('/', segments);
    }

    /// <summary>Resolves an artifact key and verifies that it remains below the supplied root.</summary>
    public static string ResolveUnderRoot(string root, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        string fullRoot = Path.GetFullPath(root);
        string normalized = NormalizeKey(key);
        string candidate = Path.GetFullPath(Path.Combine(
            fullRoot,
            normalized.Replace('/', Path.DirectorySeparatorChar)));

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string rootPrefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        if (!candidate.Equals(fullRoot, comparison) && !candidate.StartsWith(rootPrefix, comparison))
        {
            throw new ArgumentException($"Artifact key resolves outside the storage root: '{key}'.", nameof(key));
        }

        string physicalRoot = ResolveLinks(fullRoot);
        string physicalCandidate = ResolveLinks(candidate);
        string physicalRootPrefix = Path.TrimEndingDirectorySeparator(physicalRoot) + Path.DirectorySeparatorChar;
        if (!physicalCandidate.Equals(physicalRoot, comparison)
            && !physicalCandidate.StartsWith(physicalRootPrefix, comparison))
        {
            throw new ArgumentException($"Artifact key resolves through a link outside the storage root: '{key}'.", nameof(key));
        }

        return candidate;
    }

    private static string ResolveLinks(string fullPath)
    {
        string pathRoot = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException($"Path has no filesystem root: '{fullPath}'.", nameof(fullPath));
        string current = pathRoot;
        string[] segments = fullPath[pathRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            FileSystemInfo? entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current)
                    ? new FileInfo(current)
                    : null;
            if (entry?.LinkTarget is null)
            {
                continue;
            }

            current = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? throw new IOException($"Cannot resolve filesystem link '{entry.FullName}'.");
        }

        return Path.GetFullPath(current);
    }
}
