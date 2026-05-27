using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ArchMind.Core.Abstractions;
using ArchMind.Infrastructure.Cloning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArchMind.Infrastructure.Manifests;

/// <summary>
/// Deterministic manifest scanner. Walks the cloned repo working tree (capped
/// to keep work cheap), parses .csproj / package.json / pyproject.toml /
/// Directory.Packages.props, and aggregates package references + a service
/// name + a kind classifier.
///
/// Heuristics for <c>Kind</c>:
/// <list type="bullet">
///   <item>React Native (presence of <c>react-native</c> in package.json deps) → "mobile"</item>
///   <item>React/Next/Vue → "frontend"</item>
///   <item>Microsoft.AspNetCore* or Microsoft.NET.Sdk.Web → "backend"</item>
///   <item>Hangfire / IHostedService project → "worker"</item>
///   <item>SDK type Microsoft.NET.Sdk (no web/worker hints) → "library"</item>
///   <item>else → "unknown"</item>
/// </list>
///
/// "Internal" packages are detected by:
/// <list type="bullet">
///   <item>npm scoped packages whose scope matches the package name's scope</item>
///   <item>NuGet packages prefixed with the same root namespace as the assembly name</item>
/// </list>
/// Initial conservative pass; cross-repo correlation later widens via overlap.
/// </summary>
public sealed class RepoManifestService : IRepoManifestService
{
    private const int MaxFilesScanned = 200;
    private const int MaxPackagesCollected = 500;

    private static readonly string[] FrontendHints =
    {
        "react", "next", "vue", "@angular/core", "svelte"
    };

    private readonly CloningOptions _cloning;
    private readonly ILogger<RepoManifestService> _logger;

    public RepoManifestService(
        IOptions<CloningOptions> cloning, ILogger<RepoManifestService> logger)
    {
        _cloning = cloning.Value;
        _logger = logger;
    }

    public Task<RepoManifest> ReadAsync(Guid workspaceId, Guid repoId, CancellationToken ct = default)
    {
        var root = Path.Combine(
            _cloning.WorkingDirRoot, workspaceId.ToString(), "repos", repoId.ToString());

        if (!Directory.Exists(root))
        {
            _logger.LogDebug(
                "Repo working tree missing: workspace={WorkspaceId} repo={RepoId} root={Root}",
                workspaceId, repoId, root);
            return Task.FromResult(RepoManifest.Empty);
        }

        try
        {
            var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? name = null;
            string language = "unknown";
            string kind = "unknown";

            var scanned = 0;
            foreach (var path in EnumerateManifests(root))
            {
                if (scanned++ >= MaxFilesScanned) break;
                ct.ThrowIfCancellationRequested();

                try
                {
                    var fileName = Path.GetFileName(path);
                    if (string.Equals(fileName, "package.json", StringComparison.OrdinalIgnoreCase))
                    {
                        ReadPackageJson(path, packages, ref name, ref kind, ref language);
                    }
                    else if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(fileName, "Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
                    {
                        ReadCsproj(path, packages, ref name, ref kind, ref language);
                    }
                    else if (string.Equals(fileName, "pyproject.toml", StringComparison.OrdinalIgnoreCase))
                    {
                        ReadPyproject(path, packages, ref name, ref language);
                        kind = kind == "unknown" ? "library" : kind;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to parse manifest {Path}", path);
                }
                if (packages.Count > MaxPackagesCollected) break;
            }

            if (name is null || name.Length == 0)
                name = Path.GetFileName(Path.GetFullPath(root)) ?? "unknown";

            var pkgList = packages.Take(MaxPackagesCollected).ToList();
            var internalPkgs = ClassifyInternal(pkgList, name);

            return Task.FromResult(new RepoManifest(name, kind, language, pkgList, internalPkgs));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RepoManifestService failed: workspace={WorkspaceId} repo={RepoId}",
                workspaceId, repoId);
            return Task.FromResult(RepoManifest.Empty);
        }
    }

    private static IEnumerable<string> EnumerateManifests(string root)
    {
        var patterns = new[]
        {
            "package.json",
            "*.csproj",
            "Directory.Packages.props",
            "pyproject.toml",
        };

        // Skip large vendor/cache directories to keep this cheap on huge repos.
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", ".git", "bin", "obj", "dist", "build",
            "graphify-out", ".next", "out", ".venv", "venv", "__pycache__",
        };

        foreach (var pattern in patterns)
        {
            foreach (var path in SafeEnumerate(root, pattern, skipDirs))
                yield return path;
        }
    }

    private static IEnumerable<string> SafeEnumerate(
        string root, string pattern, HashSet<string> skipDirs)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch { continue; }

            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    var leaf = Path.GetFileName(entry);
                    if (skipDirs.Contains(leaf)) continue;
                    stack.Push(entry);
                }
                else if (PatternMatches(Path.GetFileName(entry), pattern))
                {
                    yield return entry;
                }
            }
        }
    }

    private static bool PatternMatches(string fileName, string pattern)
    {
        if (pattern.StartsWith("*."))
        {
            return fileName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static void ReadPackageJson(
        string path,
        HashSet<string> packages,
        ref string? name,
        ref string kind,
        ref string language)
    {
        var text = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        if (name is null && root.TryGetProperty("name", out var nm)
            && nm.ValueKind == JsonValueKind.String)
        {
            name = nm.GetString();
        }

        if (language == "unknown") language = "javascript";

        foreach (var depKey in new[] { "dependencies", "devDependencies", "peerDependencies" })
        {
            if (!root.TryGetProperty(depKey, out var deps)
                || deps.ValueKind != JsonValueKind.Object) continue;
            foreach (var dep in deps.EnumerateObject())
            {
                packages.Add(dep.Name);
                if (depKey == "dependencies")
                {
                    var lower = dep.Name.ToLowerInvariant();
                    if (lower == "react-native") kind = "mobile";
                    else if (kind == "unknown" && FrontendHints.Any(h => lower.Contains(h)))
                        kind = "frontend";
                }
            }
        }

        if (root.TryGetProperty("scripts", out var scripts)
            && scripts.ValueKind == JsonValueKind.Object
            && scripts.TryGetProperty("start", out var startScript)
            && startScript.ValueKind == JsonValueKind.String
            && (startScript.GetString() ?? "").Contains("node ", StringComparison.OrdinalIgnoreCase)
            && kind == "unknown")
        {
            kind = "backend";
        }
    }

    private static void ReadCsproj(
        string path,
        HashSet<string> packages,
        ref string? name,
        ref string kind,
        ref string language)
    {
        if (language == "unknown") language = "csharp";

        if (name is null)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrWhiteSpace(stem)
                && !string.Equals(stem, "Directory", StringComparison.OrdinalIgnoreCase))
            {
                name = stem;
            }
        }

        XDocument doc;
        try { doc = XDocument.Load(path); }
        catch { return; }

        var sdk = doc.Root?.Attribute("Sdk")?.Value ?? string.Empty;
        if (sdk.Contains("Web", StringComparison.OrdinalIgnoreCase)) kind = "backend";
        else if (sdk.Contains("Worker", StringComparison.OrdinalIgnoreCase)) kind = "worker";
        else if (kind == "unknown" && sdk.Length > 0) kind = "library";

        foreach (var pkg in doc.Descendants("PackageReference"))
        {
            var id = pkg.Attribute("Include")?.Value ?? pkg.Attribute("Update")?.Value;
            if (string.IsNullOrWhiteSpace(id)) continue;
            packages.Add(id);
            if (kind == "unknown" || kind == "library")
            {
                if (id.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase))
                    kind = "backend";
                else if (id.StartsWith("Microsoft.Extensions.Hosting", StringComparison.OrdinalIgnoreCase)
                    && kind == "unknown")
                    kind = "worker";
                else if (id.StartsWith("Hangfire", StringComparison.OrdinalIgnoreCase)
                    && kind == "unknown")
                    kind = "worker";
            }
        }
    }

    private static readonly Regex PyDepLine = new(
        @"^(?<name>[A-Za-z0-9_\-\.\[\]]+)\s*[~=<>!]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void ReadPyproject(
        string path,
        HashSet<string> packages,
        ref string? name,
        ref string language)
    {
        if (language == "unknown") language = "python";

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (name is null && line.StartsWith("name", StringComparison.OrdinalIgnoreCase)
                && line.Contains('='))
            {
                var idx = line.IndexOf('=');
                var v = line[(idx + 1)..].Trim().Trim('"', '\'');
                if (v.Length > 0) name = v;
            }
            var m = PyDepLine.Match(line);
            if (m.Success) packages.Add(m.Groups["name"].Value);
        }
    }

    private static IReadOnlyList<string> ClassifyInternal(
        IReadOnlyList<string> packages, string serviceName)
    {
        if (packages.Count == 0) return Array.Empty<string>();

        // npm scope: @scope/x — flag if scope appears on 2+ packages (likely org scope).
        var scopeCounts = packages
            .Where(p => p.StartsWith('@') && p.Contains('/'))
            .GroupBy(p => p[..p.IndexOf('/')], StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // NuGet: root namespace (first dotted segment) shared with assembly name
        // OR appears on 2+ packages.
        var nuRoots = packages
            .Where(p => !p.StartsWith('@') && p.Contains('.'))
            .GroupBy(p => p[..p.IndexOf('.')], StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Drop well-known public roots from candidate "internal" buckets.
        nuRoots.ExceptWith(new[]
        {
            "Microsoft", "System", "Newtonsoft", "Serilog", "AutoMapper",
            "Hangfire", "MediatR", "FluentValidation", "Polly", "OpenTelemetry",
            "Azure", "Google", "Amazon", "AWSSDK", "Stripe", "Twilio", "Npgsql",
            "EntityFramework", "xunit", "Moq", "NUnit", "Castle", "MongoDB",
            "Confluent", "RabbitMQ", "MassTransit", "StackExchange", "Dapper",
        });

        // Also include packages whose root matches our own service name root.
        var ourRoot = serviceName.Contains('.')
            ? serviceName[..serviceName.IndexOf('.')]
            : serviceName;
        if (!string.IsNullOrWhiteSpace(ourRoot)) nuRoots.Add(ourRoot);

        var result = new List<string>();
        foreach (var p in packages)
        {
            if (p.StartsWith('@') && p.Contains('/'))
            {
                var scope = p[..p.IndexOf('/')];
                if (scopeCounts.Contains(scope)) result.Add(p);
            }
            else if (p.Contains('.'))
            {
                var root = p[..p.IndexOf('.')];
                if (nuRoots.Contains(root)) result.Add(p);
            }
        }
        return result;
    }
}
