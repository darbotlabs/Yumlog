using Paperboy.Desktop.Cards;
using Paperboy.Desktop.Mcp;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Paperboy.Desktop.Capabilities;

/// <summary>
/// Discovers <c>*.extension.json</c> packages from well-known roots and merges the
/// declared capabilities with Paperboy's built-in MCP tool surface into a single
/// capability index that agents, apps, and connectors can enumerate.
/// </summary>
public sealed partial class PaperboyCapabilityRegistry
{
    public const string ManifestSearchPattern = "*.extension.json";
    public const string AppRootRelativePath = @"Assets\Extensions";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private PaperboyCapabilityIndex? _index;

    /// <summary>Shared registry used by the WPF shell and the MCP host.</summary>
    public static PaperboyCapabilityRegistry Shared { get; } = new();

    /// <summary>Cached index; discovery runs on first access.</summary>
    public PaperboyCapabilityIndex Index
    {
        get
        {
            lock (_gate)
            {
                return _index ??= Build(DefaultRoots());
            }
        }
    }

    /// <summary>Re-runs discovery and replaces the cached index.</summary>
    public PaperboyCapabilityIndex Refresh()
    {
        var index = Build(DefaultRoots());
        lock (_gate)
        {
            _index = index;
        }

        return index;
    }

    public string IndexJson() => JsonSerializer.Serialize(Index, WriteOptions);

    public string ExtensionsJson() => JsonSerializer.Serialize(new
    {
        Index.SchemaVersion,
        Index.Schema,
        Index.GeneratedAtUtc,
        Index.Roots,
        Index.Policy,
        Index.Extensions,
        Index.Diagnostics
    }, WriteOptions);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, WriteOptions);

    public PaperboyExtensionSummary? FindExtension(string id) =>
        Index.Extensions.FirstOrDefault(extension => string.Equals(extension.Id, id, StringComparison.OrdinalIgnoreCase));

    public PaperboyCapabilityEntry? FindCapability(string id) =>
        Index.Capabilities.FirstOrDefault(capability => string.Equals(capability.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<PaperboyCapabilityEntry> Query(string? kind, string? tag, bool includeBuiltIn)
    {
        return Index.Capabilities
            .Where(capability => includeBuiltIn || !string.Equals(capability.Source, PaperboyCapabilityTransports.BuiltIn, StringComparison.OrdinalIgnoreCase))
            .Where(capability => string.IsNullOrWhiteSpace(kind) || string.Equals(capability.Kind, kind, StringComparison.OrdinalIgnoreCase))
            .Where(capability => string.IsNullOrWhiteSpace(tag) || capability.Tags.Contains(tag!, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <summary>Discovery roots in probe order. Existing and missing roots are both reported.</summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        var roots = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "Extensions"),
            Path.Combine(AppContext.BaseDirectory, "extensions")
        };

        AddProfileRoot(roots, Environment.SpecialFolder.CommonApplicationData);
        AddProfileRoot(roots, Environment.SpecialFolder.ApplicationData);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            roots.Add(Path.Combine(userProfile, ".paperboy", "extensions"));
        }

        var configured = Environment.GetEnvironmentVariable(PaperboyCapabilityEnvironment.RootsVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            foreach (var entry in configured.Split([Path.PathSeparator, ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                roots.Add(entry);
            }
        }

        return roots
            .Select(NormalizeRoot)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool ExternalInvocationAllowed()
    {
        var value = Environment.GetEnvironmentVariable(PaperboyCapabilityEnvironment.AllowInvokeVariable);
        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Builds an index from an explicit root list. Exposed for tests and hosts.</summary>
    public static PaperboyCapabilityIndex Build(IReadOnlyList<string> roots)
    {
        var allowExternal = ExternalInvocationAllowed();
        var diagnostics = new List<PaperboyCapabilityDiagnostic>();
        var extensions = new List<PaperboyExtensionSummary>();
        var capabilities = new List<PaperboyCapabilityEntry>(BuiltInCapabilities());
        var extensionCards = new List<AgentCardDefinition>();
        var seenExtensionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenCapabilityIds = new HashSet<string>(capabilities.Select(capability => capability.Id), StringComparer.OrdinalIgnoreCase);

        var appRoots = new HashSet<string>(
            [
                NormalizeRoot(Path.Combine(AppContext.BaseDirectory, "Assets", "Extensions")),
                NormalizeRoot(Path.Combine(AppContext.BaseDirectory, "extensions"))
            ],
            StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var trust = appRoots.Contains(root) ? PaperboyExtensionTrust.App : PaperboyExtensionTrust.External;

            foreach (var manifestPath in EnumerateManifests(root, diagnostics))
            {
                var manifest = ReadManifest(manifestPath, diagnostics);
                if (manifest is null)
                {
                    continue;
                }

                if (!seenExtensionIds.Add(manifest.Id))
                {
                    diagnostics.Add(new PaperboyCapabilityDiagnostic(manifestPath, "warning", $"Duplicate extension id '{manifest.Id}' ignored; the first discovered manifest wins."));
                    continue;
                }

                var invocationAllowed = manifest.Enabled
                    && (trust == PaperboyExtensionTrust.App || allowExternal);

                var manifestDirectory = Path.GetDirectoryName(manifestPath) ?? root;
                var accepted = new List<PaperboyCapabilityDescriptor>();

                foreach (var descriptor in manifest.Capabilities)
                {
                    if (!ValidateCapability(manifest, descriptor, manifestPath, diagnostics))
                    {
                        continue;
                    }

                    if (!seenCapabilityIds.Add(descriptor.Id))
                    {
                        diagnostics.Add(new PaperboyCapabilityDiagnostic(manifestPath, "warning", $"Capability id '{descriptor.Id}' is already registered; skipped."));
                        continue;
                    }

                    accepted.Add(descriptor);
                    capabilities.Add(ToEntry(manifest, descriptor, manifestDirectory, trust, invocationAllowed, allowExternal));
                }

                foreach (var card in manifest.Cards)
                {
                    if (card is null || string.IsNullOrWhiteSpace(card.Id))
                    {
                        continue;
                    }

                    extensionCards.Add(NormalizeCard(card));
                }

                extensions.Add(new PaperboyExtensionSummary
                {
                    Id = manifest.Id,
                    Name = string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name,
                    Version = manifest.Version,
                    Kind = manifest.Kind,
                    Publisher = manifest.Publisher,
                    Description = manifest.Description,
                    Enabled = manifest.Enabled,
                    Trust = trust,
                    InvocationAllowed = invocationAllowed,
                    ManifestPath = manifestPath,
                    Root = root,
                    Tags = manifest.Tags,
                    CapabilityIds = accepted.Select(descriptor => descriptor.Id).ToArray(),
                    CardIds = manifest.Cards.Where(card => card is not null).Select(card => card.Id).ToArray(),
                    Resources = manifest.Resources,
                    Requires = manifest.Requires
                });
            }
        }

        return new PaperboyCapabilityIndex
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Roots = roots.ToArray(),
            Policy = new PaperboyCapabilityPolicy
            {
                AllowExternalInvocation = allowExternal,
                Notes = allowExternal
                    ? $"External capability invocation is enabled by {PaperboyCapabilityEnvironment.AllowInvokeVariable}."
                    : $"Only app-trusted extensions under '{AppRootRelativePath}' may be invoked. Set {PaperboyCapabilityEnvironment.AllowInvokeVariable}=1 to allow user/machine roots."
            },
            Extensions = extensions.ToArray(),
            Capabilities = capabilities.ToArray(),
            ExtensionCards = extensionCards.ToArray(),
            Diagnostics = diagnostics.ToArray()
        };
    }

    /// <summary>Paperboy's own MCP tools projected into the capability contract.</summary>
    public static IReadOnlyList<PaperboyCapabilityEntry> BuiltInCapabilities()
    {
        return PaperboyMcpServer.BuiltInToolDescriptors()
            .Select(tool => new PaperboyCapabilityEntry
            {
                Id = tool.Name,
                Kind = PaperboyCapabilityKinds.Tool,
                Title = tool.Name,
                Description = tool.Description,
                Transport = PaperboyCapabilityTransports.BuiltIn,
                Source = PaperboyCapabilityTransports.BuiltIn,
                ExtensionId = "paperboy",
                Trust = PaperboyExtensionTrust.App,
                Invocable = true,
                InvocationNote = "Call through MCP tools/call.",
                Tags = ["paperboy", "mcp"],
                Cards = ["paperboy.mcp"]
            })
            .ToArray();
    }

    private static PaperboyCapabilityEntry ToEntry(
        PaperboyExtensionManifest manifest,
        PaperboyCapabilityDescriptor descriptor,
        string manifestDirectory,
        string trust,
        bool invocationAllowed,
        bool allowExternal)
    {
        var transportInvocable = PaperboyCapabilityTransports.IsInvocable(descriptor.Transport);
        var invocable = transportInvocable && invocationAllowed;

        var note = !manifest.Enabled
            ? "Extension manifest sets enabled=false."
            : !transportInvocable
                ? $"Transport '{descriptor.Transport}' is descriptive only; Paperboy publishes the contract without executing it."
                : invocable
                    ? "Invoke through paperboy.extensions.invoke."
                    : $"External extension invocation is blocked. Set {PaperboyCapabilityEnvironment.AllowInvokeVariable}=1 to allow it.";

        return new PaperboyCapabilityEntry
        {
            Id = descriptor.Id,
            Kind = string.IsNullOrWhiteSpace(descriptor.Kind) ? PaperboyCapabilityKinds.Tool : descriptor.Kind,
            Title = string.IsNullOrWhiteSpace(descriptor.Title) ? descriptor.Id : descriptor.Title,
            Description = descriptor.Description,
            Transport = descriptor.Transport,
            Source = manifest.Id,
            ExtensionId = manifest.Id,
            Trust = trust,
            Invocable = invocable,
            InvocationNote = note,
            Tags = descriptor.Tags,
            Cards = descriptor.Cards,
            Descriptor = descriptor,
            ManifestDirectory = manifestDirectory
        };
    }

    private static IEnumerable<string> EnumerateManifests(string root, List<PaperboyCapabilityDiagnostic> diagnostics)
    {
        try
        {
            return Directory
                .EnumerateFiles(root, ManifestSearchPattern, new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 4,
                    IgnoreInaccessible = true,
                    MatchCasing = MatchCasing.CaseInsensitive
                })
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Where(path => !Path.GetFileName(path).EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new PaperboyCapabilityDiagnostic(root, "error", $"Could not enumerate extension root: {ex.Message}"));
            return [];
        }
    }

    private static PaperboyExtensionManifest? ReadManifest(string manifestPath, List<PaperboyCapabilityDiagnostic> diagnostics)
    {
        PaperboyExtensionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PaperboyExtensionManifest>(File.ReadAllText(manifestPath), ReadOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new PaperboyCapabilityDiagnostic(manifestPath, "error", $"Manifest could not be read: {ex.Message}"));
            return null;
        }

        if (manifest is null)
        {
            diagnostics.Add(new PaperboyCapabilityDiagnostic(manifestPath, "error", "Manifest deserialized to null."));
            return null;
        }

        manifest = Normalize(manifest);

        if (string.IsNullOrWhiteSpace(manifest.Id) || !IdPattern().IsMatch(manifest.Id))
        {
            diagnostics.Add(new PaperboyCapabilityDiagnostic(manifestPath, "error", "Manifest 'id' is required and must match ^[A-Za-z0-9][A-Za-z0-9._-]*$."));
            return null;
        }

        if (!TryGetMajorVersion(manifest.SchemaVersion, out var major))
        {
            diagnostics.Add(new PaperboyCapabilityDiagnostic(manifestPath, "error", $"Manifest 'schemaVersion' '{manifest.SchemaVersion}' is not a recognized version."));
            return null;
        }

        if (major > 1)
        {
            diagnostics.Add(new PaperboyCapabilityDiagnostic(manifestPath, "warning", $"Manifest 'schemaVersion' {manifest.SchemaVersion} is newer than the supported {PaperboyExtensionManifest.CurrentSchemaVersion} contract; unknown fields are ignored."));
        }

        if (!PaperboyExtensionKinds.Known.Contains(manifest.Kind, StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Add(new PaperboyCapabilityDiagnostic(manifestPath, "warning", $"Unknown extension kind '{manifest.Kind}'; the manifest still loads."));
        }

        return manifest;
    }

    private static PaperboyExtensionManifest Normalize(PaperboyExtensionManifest manifest) => manifest with
    {
        SchemaVersion = string.IsNullOrWhiteSpace(manifest.SchemaVersion) ? PaperboyExtensionManifest.CurrentSchemaVersion : manifest.SchemaVersion.Trim(),
        Id = manifest.Id?.Trim() ?? "",
        Kind = string.IsNullOrWhiteSpace(manifest.Kind) ? PaperboyExtensionKinds.Extension : manifest.Kind.Trim(),
        Version = string.IsNullOrWhiteSpace(manifest.Version) ? "0.0.0" : manifest.Version.Trim(),
        Tags = manifest.Tags ?? [],
        Capabilities = (manifest.Capabilities ?? []).Where(capability => capability is not null).Select(NormalizeCapability).ToArray(),
        Cards = (manifest.Cards ?? []).Where(card => card is not null).ToArray(),
        Resources = manifest.Resources ?? []
    };

    private static PaperboyCapabilityDescriptor NormalizeCapability(PaperboyCapabilityDescriptor descriptor) => descriptor with
    {
        Id = descriptor.Id?.Trim() ?? "",
        Kind = string.IsNullOrWhiteSpace(descriptor.Kind) ? PaperboyCapabilityKinds.Tool : descriptor.Kind.Trim(),
        Transport = string.IsNullOrWhiteSpace(descriptor.Transport) ? PaperboyCapabilityTransports.Manual : descriptor.Transport.Trim().ToLowerInvariant(),
        Args = descriptor.Args ?? [],
        Headers = descriptor.Headers ?? [],
        Tags = descriptor.Tags ?? [],
        Cards = descriptor.Cards ?? [],
        TimeoutSeconds = descriptor.TimeoutSeconds is > 0 and <= 900 ? descriptor.TimeoutSeconds : 60
    };

    private static AgentCardDefinition NormalizeCard(AgentCardDefinition card) => card with
    {
        Inputs = card.Inputs ?? [],
        Outputs = card.Outputs ?? [],
        Actions = card.Actions ?? [],
        LiveTileState = card.LiveTileState ?? ""
    };

    private static bool ValidateCapability(
        PaperboyExtensionManifest manifest,
        PaperboyCapabilityDescriptor descriptor,
        string manifestPath,
        List<PaperboyCapabilityDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Id) || !IdPattern().IsMatch(descriptor.Id))
        {
            diagnostics.Add(new PaperboyCapabilityDiagnostic(manifestPath, "error", $"Extension '{manifest.Id}' declares a capability without a valid 'id'."));
            return false;
        }

        if (!PaperboyCapabilityTransports.Known.Contains(descriptor.Transport, StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Add(new PaperboyCapabilityDiagnostic(manifestPath, "error", $"Capability '{descriptor.Id}' uses unsupported transport '{descriptor.Transport}'."));
            return false;
        }

        if (string.Equals(descriptor.Transport, PaperboyCapabilityTransports.BuiltIn, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new PaperboyCapabilityDiagnostic(manifestPath, "error", $"Capability '{descriptor.Id}' may not claim the reserved 'builtin' transport."));
            return false;
        }

        if (string.Equals(descriptor.Transport, PaperboyCapabilityTransports.Process, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(descriptor.Command))
        {
            diagnostics.Add(new PaperboyCapabilityDiagnostic(manifestPath, "error", $"Capability '{descriptor.Id}' uses the process transport without a 'command'."));
            return false;
        }

        if (string.Equals(descriptor.Transport, PaperboyCapabilityTransports.Http, StringComparison.OrdinalIgnoreCase)
            && !Uri.TryCreate(descriptor.Url, UriKind.Absolute, out _))
        {
            diagnostics.Add(new PaperboyCapabilityDiagnostic(manifestPath, "error", $"Capability '{descriptor.Id}' uses the http transport without an absolute 'url'."));
            return false;
        }

        if (!PaperboyCapabilityKinds.Known.Contains(descriptor.Kind, StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Add(new PaperboyCapabilityDiagnostic(manifestPath, "warning", $"Capability '{descriptor.Id}' uses unknown kind '{descriptor.Kind}'; it is still indexed."));
        }

        return true;
    }

    private static bool TryGetMajorVersion(string schemaVersion, out int major)
    {
        major = 0;
        var head = schemaVersion.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return head is not null && int.TryParse(head, out major);
    }

    private static void AddProfileRoot(List<string> roots, Environment.SpecialFolder folder)
    {
        var basePath = Environment.GetFolderPath(folder);
        if (!string.IsNullOrWhiteSpace(basePath))
        {
            roots.Add(Path.Combine(basePath, "Paperboy", "extensions"));
        }
    }

    private static string NormalizeRoot(string root)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
