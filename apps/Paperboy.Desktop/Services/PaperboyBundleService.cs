using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Paperboy.Desktop.Services;

public sealed record BundleProgress(int CompletedFiles, int TotalFiles, string CurrentFile);

public sealed record BundleResult(
    string BundlePath,
    int TotalFiles,
    long SourceBytes,
    long BundleBytes,
    double CompressionRatio,
    string? TossedTo);

public sealed record ExtractResult(string Destination, int TotalFiles);

public sealed class PaperboyManifest
{
    public string Schema { get; init; } = "https://darbotlabs.github.io/yumlog/paperboy-bundle/v1";
    public string Id { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; }
    public string CompressionLevel { get; init; } = "";
    public int TotalFiles { get; init; }
    public long TotalBytes { get; init; }
    public List<PaperboyManifestEntry> Entries { get; init; } = [];
}

public sealed class PaperboyManifestEntry
{
    public string BundlePath { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public long Length { get; init; }
    public DateTime LastWriteTimeUtc { get; init; }
    public string Sha256 { get; init; } = "";
}

public sealed class PaperboyBundleService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<BundleResult> CreateBundleAsync(
        IEnumerable<string> sourcePaths,
        string outputPath,
        CompressionLevel compressionLevel,
        bool includeHidden,
        bool overwrite,
        string? tossDestination,
        IProgress<BundleProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var sources = sourcePaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sources.Length == 0)
        {
            throw new InvalidOperationException("No source paths were provided.");
        }

        var outputFullPath = Path.GetFullPath(outputPath);
        if (File.Exists(outputFullPath) && !overwrite)
        {
            throw new IOException($"Output already exists: {outputFullPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath)!);

        var files = EnumerateFiles(sources, includeHidden)
            .Where(f => !string.Equals(f.FullName, outputFullPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (files.Length == 0)
        {
            throw new InvalidOperationException("No files were found to bundle.");
        }

        var entries = new List<PaperboyManifestEntry>(files.Length);
        var usedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long sourceBytes = 0;

        if (File.Exists(outputFullPath))
        {
            File.Delete(outputFullPath);
        }

        await using (var outputStream = new FileStream(outputFullPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 128, useAsync: true))
        using (var archive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: false))
        {
            for (var i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var file = files[i];
                var entryName = MakeEntryName(file, sources, usedNames);
                progress?.Report(new BundleProgress(i + 1, files.Length, file.Name));

                var entry = archive.CreateEntry(entryName, compressionLevel);
                entry.LastWriteTime = file.LastWriteTimeUtc;

                var hash = await CopyWithHashAsync(file.FullName, entry, cancellationToken);
                sourceBytes += file.Length;

                entries.Add(new PaperboyManifestEntry
                {
                    BundlePath = entryName,
                    SourcePath = file.FullName,
                    Length = file.Length,
                    LastWriteTimeUtc = file.LastWriteTimeUtc,
                    Sha256 = hash
                });
            }

            var manifest = new PaperboyManifest
            {
                Id = $"pb-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompressionLevel = compressionLevel.ToString(),
                TotalFiles = entries.Count,
                TotalBytes = sourceBytes,
                Entries = entries
            };

            var manifestEntry = archive.CreateEntry("paperboy-manifest.json", CompressionLevel.Optimal);
            await using var manifestStream = manifestEntry.Open();
            await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, cancellationToken);
        }

        var bundleBytes = new FileInfo(outputFullPath).Length;
        string? tossedTo = null;
        if (!string.IsNullOrWhiteSpace(tossDestination))
        {
            tossedTo = await TossBundleAsync(outputFullPath, tossDestination, overwrite, cancellationToken);
        }

        return new BundleResult(
            outputFullPath,
            entries.Count,
            sourceBytes,
            bundleBytes,
            sourceBytes == 0 ? 0 : (double)bundleBytes / sourceBytes,
            tossedTo);
    }

    public async Task<PaperboyManifest> ReadManifestAsync(string bundlePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 64, useAsync: true);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry("paperboy-manifest.json")
            ?? throw new InvalidDataException("Bundle does not contain paperboy-manifest.json.");

        await using var manifestStream = entry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<PaperboyManifest>(manifestStream, JsonOptions, cancellationToken);
        return manifest ?? throw new InvalidDataException("Bundle manifest could not be parsed.");
    }

    public async Task<ExtractResult> ExtractBundleAsync(string bundlePath, string destination, bool overwrite, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var manifest = await ReadManifestAsync(bundlePath, cancellationToken);
        Directory.CreateDirectory(destination);

        if (!overwrite && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new IOException($"Destination is not empty: {destination}");
        }

        ZipFile.ExtractToDirectory(bundlePath, destination, overwriteFiles: overwrite);
        return new ExtractResult(Path.GetFullPath(destination), manifest.TotalFiles);
    }

    public async Task<string> TossBundleAsync(string bundlePath, string destination, bool overwrite, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var source = new FileInfo(bundlePath);
        if (!source.Exists)
        {
            throw new FileNotFoundException("Bundle not found.", bundlePath);
        }

        var destinationPath = ResolveTossDestination(source, destination);
        if (File.Exists(destinationPath) && !overwrite)
        {
            throw new IOException($"Destination already exists: {destinationPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        await using var sourceStream = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
        await using var destinationStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken);

        return destinationPath;
    }

    private static IEnumerable<FileInfo> EnumerateFiles(IEnumerable<string> sourcePaths, bool includeHidden)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = includeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System
        };

        foreach (var sourcePath in sourcePaths)
        {
            if (File.Exists(sourcePath))
            {
                var file = new FileInfo(sourcePath);
                if (includeHidden || !file.Attributes.HasFlag(FileAttributes.Hidden) && !file.Attributes.HasFlag(FileAttributes.System))
                {
                    yield return file;
                }
            }
            else if (Directory.Exists(sourcePath))
            {
                foreach (var file in Directory.EnumerateFiles(sourcePath, "*", options))
                {
                    yield return new FileInfo(file);
                }
            }
            else
            {
                throw new FileNotFoundException("Source path not found.", sourcePath);
            }
        }
    }

    private static string MakeEntryName(FileInfo file, IReadOnlyCollection<string> roots, Dictionary<string, int> usedNames)
    {
        var root = roots
            .Where(r => Directory.Exists(r) && file.FullName.StartsWith(Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Length)
            .FirstOrDefault();

        string entryName;
        if (root is not null)
        {
            var rootInfo = new DirectoryInfo(root);
            var relative = Path.GetRelativePath(rootInfo.FullName, file.FullName);
            entryName = Path.Combine(rootInfo.Name, relative);
        }
        else
        {
            entryName = file.Name;
        }

        entryName = "payload/" + entryName.Replace(Path.DirectorySeparatorChar, '/');
        if (!usedNames.TryGetValue(entryName, out var count))
        {
            usedNames[entryName] = 1;
            return entryName;
        }

        usedNames[entryName] = count + 1;
        var directory = Path.GetDirectoryName(entryName)?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
        var name = Path.GetFileNameWithoutExtension(entryName);
        var extension = Path.GetExtension(entryName);
        return string.IsNullOrWhiteSpace(directory)
            ? $"{name}-{count + 1}{extension}"
            : $"{directory}/{name}-{count + 1}{extension}";
    }

    private static async Task<string> CopyWithHashAsync(string filePath, ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        using var sha256 = SHA256.Create();
        await using var sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
        await using var entryStream = entry.Open();
        var buffer = new byte[1024 * 128];
        int read;

        while ((read = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await entryStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            sha256.TransformBlock(buffer, 0, read, null, 0);
        }

        sha256.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
    }

    private static string ResolveTossDestination(FileInfo source, string destination)
    {
        if (Directory.Exists(destination))
        {
            return Path.Combine(Path.GetFullPath(destination), source.Name);
        }

        if (destination.EndsWith(Path.DirectorySeparatorChar) || destination.EndsWith(Path.AltDirectorySeparatorChar))
        {
            Directory.CreateDirectory(destination);
            return Path.Combine(Path.GetFullPath(destination), source.Name);
        }

        if (TryLooksLikeDirectory(destination))
        {
            Directory.CreateDirectory(destination);
            return Path.Combine(Path.GetFullPath(destination), source.Name);
        }

        return Path.GetFullPath(destination);
    }

    private static bool TryLooksLikeDirectory([NotNullWhen(true)] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return string.IsNullOrWhiteSpace(extension);
    }
}
