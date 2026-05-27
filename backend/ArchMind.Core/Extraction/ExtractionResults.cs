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

// ---- Integration contracts (cross-repo connection signal) -----------------

/// <summary>
/// Captures the surface a file exposes/consumes for integration across services
/// (which may live in different repos). Filled by the
/// <c>ExtractIntegrationContracts</c> prompt. Each list is empty when the file
/// has no signal of that kind. Cross-repo wiring happens automatically when
/// caller and callee land on the same deterministic node id.
/// </summary>
public sealed record IntegrationContractsExtraction(
    IReadOnlyList<HttpClientCall> HttpClientCalls,
    IReadOnlyList<GrpcCall> GrpcClientCalls,
    IReadOnlyList<GrpcCall> GrpcServerImpls,
    IReadOnlyList<MessagingChannel> MessagingPublishes,
    IReadOnlyList<MessagingChannel> MessagingConsumes,
    IReadOnlyList<string> SharedPackageImports,
    // SignalR is service-and-method shaped like gRPC, so we reuse GrpcCall.
    // SignalRHubMethods: server-side Hub class methods (callable from clients).
    // SignalRClientInvokes: client-side invoke/send/stream calls into a hub.
    // Endpoint identity: "signalr:{Service}.{Method}" — same id collapses
    // backend Hub method node and frontend invoke target into one vertex.
    IReadOnlyList<GrpcCall>? SignalRHubMethods = null,
    IReadOnlyList<GrpcCall>? SignalRClientInvokes = null);

/// <summary>
/// Outbound HTTP call. <c>BaseUrl</c> is optional and only used as a property
/// hint — the Endpoint node id is derived from <c>Method</c>+<c>Path</c> so
/// any backend exposing the same <c>POST /api/foo</c> de-duplicates onto the
/// same vertex regardless of repo.
/// </summary>
public sealed record HttpClientCall(
    string Method,
    string Path,
    string? BaseUrl,
    string? Evidence);

/// <summary>
/// gRPC client (outbound) or server (inbound) usage. Identified by
/// <c>Service.Method</c> tuple, which is the canonical wire name.
/// </summary>
public sealed record GrpcCall(string Service, string Method, string? Evidence);

/// <summary>
/// Async-messaging channel reference. <c>Kind</c> is one of "rabbitmq",
/// "kafka", "sns", "sqs", "azure-service-bus", "nats", etc. Exactly one of
/// <c>Exchange</c>/<c>Topic</c>/<c>Queue</c> usually carries the routable
/// identity; the others are optional context. <c>RoutingKey</c> applies to
/// AMQP exchange-bound traffic. Cross-repo dedupe uses the normalized
/// channel identity, so a publisher in repo A and a consumer in repo B
/// converge on the same Event/Queue node when names match.
/// </summary>
public sealed record MessagingChannel(
    string Kind,
    string? Exchange,
    string? RoutingKey,
    string? Topic,
    string? Queue,
    string? MessageType);

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
    ConventionExtraction? Conventions,
    IntegrationContractsExtraction? IntegrationContracts = null);
