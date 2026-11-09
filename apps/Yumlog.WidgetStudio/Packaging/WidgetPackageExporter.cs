using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Yumlog.WidgetStudio.Domain;
using Yumlog.WidgetStudio.Rendering;
using Yumlog.WidgetStudio.Validation;

namespace Yumlog.WidgetStudio.Packaging;

public sealed record WidgetPackageResult(string PackagePath, string WidgetId, string PackageId, long ByteLength, string[] Entries);

public static class WidgetPackageExporter
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static Task<WidgetPackageResult> ExportAsync(
        WidgetStudioProject project,
        string sourceManifestPath,
        string schemaPath,
        string packagePath,
        CancellationToken cancellationToken = default)
        => ExportAsync(project, sourceManifestPath, [schemaPath], packagePath, cancellationToken);

    public static async Task<WidgetPackageResult> ExportAsync(
        WidgetStudioProject project,
        string sourceManifestPath,
        IReadOnlyList<string> schemaPaths,
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        var report = WidgetStudioValidator.Validate(project);
        if (!report.IsValid)
        {
            throw new InvalidOperationException("Cannot package an invalid Widget Studio project.");
        }

        var packageDirectory = Path.GetDirectoryName(packagePath);
        if (!string.IsNullOrWhiteSpace(packageDirectory))
        {
            Directory.CreateDirectory(packageDirectory);
        }

        if (File.Exists(packagePath))
        {
            File.Delete(packagePath);
        }

        var entries = new List<string>();
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddFile(archive, sourceManifestPath, "widget.json", entries);

            if (project.Package.IncludeSchemas)
            {
                var declaresCapabilities = project.Widget.Capabilities.Length > 0;

                var applicableSchemas = schemaPaths
                    .Where(File.Exists)
                    .Where(path => declaresCapabilities
                        || !string.Equals(Path.GetFileName(path), WidgetCapabilityContract.ContractSchemaFileName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal);

                foreach (var schemaPath in applicableSchemas)
                {
                    AddFile(archive, schemaPath, $"schemas/{Path.GetFileName(schemaPath)}", entries);
                }
            }

            if (project.Package.IncludePreview)
            {
                AddText(archive, "preview/index.html", WidgetPreviewRenderer.Render(project, report), entries);
            }

            if (project.Package.IncludeCapabilityIndex && project.Widget.Capabilities.Length > 0)
            {
                var index = WidgetCapabilityIndex.Create(project);
                AddText(archive, "capabilities/index.json", JsonSerializer.Serialize(index, WidgetStudioJson.Options), entries);
            }

            AddText(archive, "validation-report.json", JsonSerializer.Serialize(report, WidgetStudioJson.Options), entries);

            var manifest = new
            {
                project.ProjectId,
                project.Name,
                project.Version,
                project.SchemaVersion,
                WidgetId = project.Widget.Id,
                project.Package.PackageId,
                project.Package.Format,
                CapabilityCount = project.Widget.Capabilities.Length,
                CapabilityContractSchemaId = project.Widget.Capabilities.Length > 0 ? WidgetCapabilityContract.ContractSchemaId : null,
                CreatedUtc = DateTimeOffset.UtcNow,
                Entries = entries.ToArray()
            };
            AddText(archive, "package-manifest.json", JsonSerializer.Serialize(manifest, WidgetStudioJson.Options), entries);
        }

        await Task.CompletedTask;
        var packageInfo = new FileInfo(packagePath);
        return new WidgetPackageResult(packageInfo.FullName, project.Widget.Id, project.Package.PackageId, packageInfo.Length, entries.ToArray());
    }

    private static void AddFile(ZipArchive archive, string filePath, string entryName, List<string> entries)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = DateTimeOffset.UtcNow;
        using var entryStream = entry.Open();
        using var fileStream = File.OpenRead(filePath);
        fileStream.CopyTo(entryStream);
        entries.Add(entryName);
    }

    private static void AddText(ZipArchive archive, string entryName, string content, List<string> entries)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = DateTimeOffset.UtcNow;
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, Utf8NoBom);
        writer.Write(content);
        entries.Add(entryName);
    }
}
