namespace ArchMind.Core.Extraction;

/// <summary>
/// Result of the IdentifyService prompt. When <see cref="IsPartOfService"/> is
/// false, all other fields should be null/empty.
/// </summary>
public sealed record ServiceExtraction(
    bool IsPartOfService,
    string? ServiceName,
    string? ServicePurpose,
    string? RootPath,
    IReadOnlyList<string> TechStack);

public sealed record EndpointExtraction(IReadOnlyList<HttpEndpoint> Endpoints);

public sealed record HttpEndpoint(string Method, string Path, string HandlerSymbol);

public sealed record EventPublisherExtraction(IReadOnlyList<EventDecl> Publishes);

public sealed record EventConsumerExtraction(IReadOnlyList<EventDecl> Consumes);

public sealed record EventDecl(string Name, string? Version, string? SchemaSummary, string? Topic);

public sealed record StorageOwnershipExtraction(IReadOnlyList<StorageRef> Storages);

/// <summary>
/// A storage dependency referenced by the file. <see cref="Access"/> is one of
/// "owns" (this service owns the schema / lifecycle) or "reads" (consumes only).
/// </summary>
public sealed record StorageRef(string Name, string Type, string Access, string? ConnectionHint);

public sealed record ConventionExtraction(IReadOnlyList<Convention> Conventions);

public sealed record Convention(string Category, string Name, string Description);

/// <summary>
/// Aggregated extraction output for one source file across all prompts.
/// Persisted as JSONB in <c>file_extractions.extraction_payload</c>.
/// </summary>
public sealed record FileExtractionRecord(
    ServiceExtraction? Service,
    EndpointExtraction? Endpoints,
    EventPublisherExtraction? EventsPublished,
    EventConsumerExtraction? EventsConsumed,
    StorageOwnershipExtraction? Storage,
    ConventionExtraction? Conventions);
