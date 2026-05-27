using System.Collections.ObjectModel;
using ArchMind.Core.Extraction;

namespace ArchMind.Infrastructure.Extraction;

/// <summary>
/// Static catalogue of <see cref="ExtractionPrompt"/> definitions, one per
/// <see cref="ExtractionPromptId"/>. The dictionary is exposed via DI so the
/// Hangfire extraction job can iterate all prompts uniformly.
///
/// Prompt versions are date-stamped. Bumping a version invalidates cache rows
/// for that prompt because the version is folded into the cache key.
/// </summary>
public static class ExtractionPromptLibrary
{
    public const string CurrentVersion = "2026-05-27/v3-route-combine";

    public static IReadOnlyDictionary<ExtractionPromptId, ExtractionPrompt> All { get; } =
        BuildAll();

    private static IReadOnlyDictionary<ExtractionPromptId, ExtractionPrompt> BuildAll()
    {
        var map = new Dictionary<ExtractionPromptId, ExtractionPrompt>
        {
            [ExtractionPromptId.IdentifyService] = BuildIdentifyService(),
            [ExtractionPromptId.ExtractHttpEndpoints] = BuildExtractHttpEndpoints(),
            [ExtractionPromptId.ExtractEventPublishers] = BuildExtractEventPublishers(),
            [ExtractionPromptId.ExtractEventConsumers] = BuildExtractEventConsumers(),
            [ExtractionPromptId.ExtractStorageOwnership] = BuildExtractStorageOwnership(),
            [ExtractionPromptId.InferConventions] = BuildInferConventions(),
            [ExtractionPromptId.ExtractIntegrationContracts] = BuildExtractIntegrationContracts(),
            [ExtractionPromptId.QuestionGeneration] = BuildQuestionGeneration(),
        };
        return new ReadOnlyDictionary<ExtractionPromptId, ExtractionPrompt>(map);
    }

    // -----------------------------------------------------------------------
    // IdentifyService
    // -----------------------------------------------------------------------
    private static ExtractionPrompt BuildIdentifyService() => new(
        Id: ExtractionPromptId.IdentifyService,
        Version: CurrentVersion,
        ToolName: "report_service_identity",
        ToolDescription: "Report whether the source file belongs to a microservice and, if so, its name, purpose, and tech stack.",
        SystemPrompt: """
            Decide if file part of microservice. Output structured.

            Rules:
            - One tool call.
            - Irrelevant file (README, image, lockfile, generated, vendored) → IsPartOfService=false, empty TechStack. NOT refusal.
            - Prefer null/empty over guess.
            - Identity hints: Program.cs, main.go, app.py, manage.py, package.json "name", Dockerfile, deploy manifests, README headings, namespace/package, csproj/pom.xml/build.gradle.
            - TechStack: concrete short list. e.g. ["aspnet-core","ef-core","postgres"], ["express","node"], ["fastapi","python"].
            - RootPath: service-root dir relative to repo. e.g. "services/billing", "backend". Null if unknown.
            """,
        UserPromptTemplate: """
            File path: {file_path}

            File content:
            ```
            {file_content}
            ```

            Identify whether this file belongs to a microservice and describe it.
            """,
        OutputJsonSchema: """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "additionalProperties": false,
              "required": ["IsPartOfService", "TechStack"],
              "properties": {
                "IsPartOfService": { "type": "boolean" },
                "ServiceName": { "type": ["string", "null"] },
                "ServicePurpose": { "type": ["string", "null"] },
                "RootPath": { "type": ["string", "null"] },
                "TechStack": { "type": "array", "items": { "type": "string" } }
              }
            }
            """);

    // -----------------------------------------------------------------------
    // ExtractHttpEndpoints
    // -----------------------------------------------------------------------
    private static ExtractionPrompt BuildExtractHttpEndpoints() => new(
        Id: ExtractionPromptId.ExtractHttpEndpoints,
        Version: CurrentVersion,
        ToolName: "report_http_endpoints",
        ToolDescription: "Report HTTP endpoints (routes) declared or registered in this file.",
        SystemPrompt: """
            Extract HTTP routes declared/registered in file. Output structured.

            Rules:
            - One tool call.
            - Irrelevant file → empty Endpoints array. NOT refusal.
            - Frameworks: ASP.NET (MapGet/MapPost, [HttpGet("...")], [Route]), Express (app.get/post/put/delete), Flask/FastAPI (@app.route, @router.get), Spring (@GetMapping/@RequestMapping), Gin/Echo/Chi (router.GET, e.GET), Rails routes.
            - Method: uppercase HTTP verb. Multi-verb registration → one row per verb.
            - Path: route template verbatim e.g. "/api/users/{id}". Preserve placeholders.
            - HandlerSymbol: function/method/class name. e.g. "UsersController.GetById", "handleCreate". Unnamed lambda → "<lambda>".
            - No invented endpoints. Unsure → omit.
            - ASP.NET ROUTE COMBINING: When a class has [Route("prefix")] and a method has [HttpGet], [HttpPost] etc. with NO path argument, the full path is just the class prefix. e.g. [Route("api/random")] + [HttpGet] → GET /api/random. [Route("api/users")] + [HttpPost] → POST /api/users. Always combine class-level [Route] with method-level HTTP verb attributes even when the verb attribute has no path string.
            """,
        UserPromptTemplate: """
            File path: {file_path}

            File content:
            ```
            {file_content}
            ```

            Extract HTTP endpoints declared or registered in this file.
            """,
        OutputJsonSchema: """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "additionalProperties": false,
              "required": ["Endpoints"],
              "properties": {
                "Endpoints": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["Method", "Path", "HandlerSymbol"],
                    "properties": {
                      "Method": { "type": "string" },
                      "Path": { "type": "string" },
                      "HandlerSymbol": { "type": "string" }
                    }
                  }
                }
              }
            }
            """);

    // -----------------------------------------------------------------------
    // ExtractEventPublishers
    // -----------------------------------------------------------------------
    private static ExtractionPrompt BuildExtractEventPublishers() => new(
        Id: ExtractionPromptId.ExtractEventPublishers,
        Version: CurrentVersion,
        ToolName: "report_event_publishers",
        ToolDescription: "Report message-bus / event-stream events that this file PUBLISHES.",
        SystemPrompt: """
            Extract events/messages this file PUBLISHES (produces/sends/emits) to bus or stream. Output structured.

            Rules:
            - One tool call.
            - Irrelevant → empty Publishes array. NOT refusal.
            - Look for: Kafka producers (Send/ProduceAsync), RabbitMQ/MassTransit (IBus.Publish, channel.BasicPublish), AWS SNS/SQS (sns.publish, sqs.sendMessage), Azure Service Bus (ServiceBusSender.SendMessageAsync), Google Pub/Sub (publisher.publish), NATS (nc.Publish), Redis Streams (XADD), Kinesis (PutRecord).
            - Name: event/message type or topic-as-event. e.g. "OrderCreated", "user.signup.v1".
            - Version: explicit version in type/topic/header e.g. "v1","2"; else null.
            - SchemaSummary: one-line payload field description if visible; else null.
            - Topic: destination topic/queue/channel name if visible; else null.
            - Only ACTIVELY published. No consumer handlers or test fixtures.
            """,
        UserPromptTemplate: """
            File path: {file_path}

            File content:
            ```
            {file_content}
            ```

            Extract events / messages PUBLISHED by this file.
            """,
        OutputJsonSchema: """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "additionalProperties": false,
              "required": ["Publishes"],
              "properties": {
                "Publishes": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["Name"],
                    "properties": {
                      "Name": { "type": "string" },
                      "Version": { "type": ["string", "null"] },
                      "SchemaSummary": { "type": ["string", "null"] },
                      "Topic": { "type": ["string", "null"] }
                    }
                  }
                }
              }
            }
            """);

    // -----------------------------------------------------------------------
    // ExtractEventConsumers
    // -----------------------------------------------------------------------
    private static ExtractionPrompt BuildExtractEventConsumers() => new(
        Id: ExtractionPromptId.ExtractEventConsumers,
        Version: CurrentVersion,
        ToolName: "report_event_consumers",
        ToolDescription: "Report message-bus / event-stream events that this file CONSUMES.",
        SystemPrompt: """
            Extract events/messages this file CONSUMES (subscribes/handles/receives) from bus or stream. Output structured.

            Rules:
            - One tool call.
            - Irrelevant → empty Consumes array. NOT refusal.
            - Look for: Kafka consumers (Consume, AddConsumer<T>), RabbitMQ/MassTransit (IConsumer<T>, channel.BasicConsume), AWS SQS handlers, Azure Service Bus (ServiceBusProcessor, [ServiceBusTrigger]), Google Pub/Sub subscribers, NATS (nc.Subscribe), Redis Streams (XREADGROUP), Kinesis (GetRecords).
            - Name: event/message type consumed. e.g. "OrderCreated", "user.signup.v1".
            - Version: explicit version if present; else null.
            - SchemaSummary: one-line payload field description if visible; else null.
            - Topic: source topic/queue/channel if visible; else null.
            - Only ACTIVELY consumed. No producers or shared DTO defs without handler.
            """,
        UserPromptTemplate: """
            File path: {file_path}

            File content:
            ```
            {file_content}
            ```

            Extract events / messages CONSUMED by this file.
            """,
        OutputJsonSchema: """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "additionalProperties": false,
              "required": ["Consumes"],
              "properties": {
                "Consumes": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["Name"],
                    "properties": {
                      "Name": { "type": "string" },
                      "Version": { "type": ["string", "null"] },
                      "SchemaSummary": { "type": ["string", "null"] },
                      "Topic": { "type": ["string", "null"] }
                    }
                  }
                }
              }
            }
            """);

    // -----------------------------------------------------------------------
    // ExtractStorageOwnership
    // -----------------------------------------------------------------------
    private static ExtractionPrompt BuildExtractStorageOwnership() => new(
        Id: ExtractionPromptId.ExtractStorageOwnership,
        Version: CurrentVersion,
        ToolName: "report_storage_dependencies",
        ToolDescription: "Report storage systems (databases, caches, blob stores, search indexes) referenced by this file and whether the file's service OWNS or READS them.",
        SystemPrompt: """
            Extract storage deps (DB/cache/blob/search/queues-as-storage). Output structured.

            Rules:
            - One tool call.
            - Irrelevant → empty Storages array. NOT refusal.
            - Look for: connection strings, EF Core/Dapper/SQLAlchemy/TypeORM/GORM/ActiveRecord setup, MongoDB clients, Redis clients (StackExchange.Redis, ioredis), Elasticsearch/OpenSearch clients, S3/Azure Blob/GCS SDK calls, migrations (Flyway, Alembic, EF).
            - Type lowercase: "postgres","mysql","sqlserver","sqlite","mongodb","redis","elasticsearch","opensearch","s3","azure-blob","gcs","dynamodb","cassandra","other".
            - Access "owns" if defines schema, runs migrations, or system-of-record. "reads" if query-only. Uncertain → "reads".
            - Name: DB/bucket/index/logical id. e.g. "billing_db","user-avatars","orders-index". Most specific visible.
            - ConnectionHint: env var/config key/host snippet if visible. e.g. "ConnectionStrings:BillingDb","DATABASE_URL". Else null. No secret values.
            """,
        UserPromptTemplate: """
            File path: {file_path}

            File content:
            ```
            {file_content}
            ```

            Extract storage systems referenced by this file and classify each as
            owned or read.
            """,
        OutputJsonSchema: """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "additionalProperties": false,
              "required": ["Storages"],
              "properties": {
                "Storages": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["Name", "Type", "Access"],
                    "properties": {
                      "Name": { "type": "string" },
                      "Type": { "type": "string" },
                      "Access": { "type": "string", "enum": ["owns", "reads"] },
                      "ConnectionHint": { "type": ["string", "null"] }
                    }
                  }
                }
              }
            }
            """);

    // -----------------------------------------------------------------------
    // InferConventions
    // -----------------------------------------------------------------------
    private static ExtractionPrompt BuildInferConventions() => new(
        Id: ExtractionPromptId.InferConventions,
        Version: CurrentVersion,
        ToolName: "report_conventions",
        ToolDescription: "Report architectural / coding conventions evident in this file (naming, error handling, logging, security, layering).",
        SystemPrompt: """
            Identify architectural/coding conventions clearly visible in file. Output structured.

            Rules:
            - One tool call.
            - Irrelevant → empty Conventions array. NOT refusal.
            - Only CLEARLY VISIBLE conventions. No speculating repo-wide standards from one file.
            - Examples:
              * naming: "Controllers suffixed 'Controller'", "Async methods end 'Async'".
              * error-handling: "All endpoints return ProblemDetails on error".
              * logging: "Serilog with structured properties".
              * security: "All endpoints [Authorize] by default".
              * layering: "Repository pattern via I<Entity>Repository".
              * testing: "xUnit + FluentAssertions".
              * config: "Options pattern via IOptions<T>".
            - Category lowercase tag: "naming","error-handling","logging","security","layering","testing","config","other".
            - Name: 2-6 word title.
            - Description: one concrete sentence.
            - FEWER higher-signal > many weak.
            """,
        UserPromptTemplate: """
            File path: {file_path}

            File content:
            ```
            {file_content}
            ```

            Identify architectural / coding conventions evident in this file.
            """,
        OutputJsonSchema: """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "additionalProperties": false,
              "required": ["Conventions"],
              "properties": {
                "Conventions": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["Category", "Name", "Description"],
                    "properties": {
                      "Category": { "type": "string" },
                      "Name": { "type": "string" },
                      "Description": { "type": "string" }
                    }
                  }
                }
              }
            }
            """);

    // -----------------------------------------------------------------------
    // ExtractIntegrationContracts
    //
    // Cross-repo wiring relies on caller and callee landing on the SAME
    // deterministic node id. The fields here (HTTP method+path, gRPC
    // service+method, queue/topic/exchange names) are exactly what gets MD5'd
    // into the Endpoint/Event/Queue Guid scheme by the projector. Output must
    // therefore be normalised: uppercase HTTP method, leading slash on path,
    // no query string, no host. The prompt enforces this in plain English
    // because we don't post-process the LLM output (yet).
    // -----------------------------------------------------------------------
    private static ExtractionPrompt BuildExtractIntegrationContracts() => new(
        Id: ExtractionPromptId.ExtractIntegrationContracts,
        Version: CurrentVersion,
        ToolName: "report_integration_contracts",
        ToolDescription: "Report the outbound + reciprocal integration surface of this file: HTTP client calls, gRPC client/server impls, async messaging publishes/consumes, and shared internal-package imports.",
        SystemPrompt: """
            Extract integration surface for cross-repo wiring. NOT inbound endpoints (other prompt handles). Output structured.

            Capture:
              1. HTTP CLIENT calls to OTHER services (fetch/axios/HttpClient/requests). Method UPPERCASE (GET/POST). Path MUST start "/", no scheme/host, no query, no fragment.
              2. gRPC CLIENT calls. Service+Method verbatim from proto ("UserService","GetUser").
              3. gRPC SERVER impls. Same shape.
              4. Async PUBLISH sites (RabbitMQ/Kafka/SQS/SNS/ServiceBus/NATS/Redis). Capture "kind"; whichever of exchange/routing_key/topic/queue/message_type present. Names verbatim.
              5. Async CONSUME sites. Same shape. SignalR server-pushed events too: backend `Clients.X.SendAsync("EventName",...)` → MessagingPublishes kind="signalr" topic="EventName". Frontend `connection.on("EventName",...)` → MessagingConsumes kind="signalr" topic="EventName".
              6. SharedPackageImports: import statements for internal libs (e.g. "Company.Shared.*", "@org/contracts","myorg-protos"). Skip framework/stdlib.
              7. SignalRHubMethods: server-side ASP.NET SignalR Hub methods. A class that inherits `Hub` or `Hub<T>` exposes its public methods as RPC entry points. Service=HubClassName, Method=PublicMethodName. One row per public method.
              8. SignalRClientInvokes: client-side `connection.invoke("X",...)` / `connection.send("X",...)` / `connection.stream("X",...)` (@microsoft/signalr or HubConnection.SendAsync/InvokeAsync C# client). Service=hub-url-tail-or-HubClass (e.g. "GameHub" when known, else last URL segment), Method=invoked method name. Be conservative — only when you can identify the hub.

            Rules:
            - One tool call.
            - Empty arrays — NEVER null — for sections with no signal.
            - Conservative. URL string literal never invoked → omit.
            - Each call = one item. No file-level dedup.
            """,
        UserPromptTemplate: """
            File path: {file_path}

            File content:
            ```
            {file_content}
            ```

            Extract all outbound HTTP/gRPC calls, gRPC server impls, messaging
            publishes/consumes, and internal-package imports found in this file.
            """,
        OutputJsonSchema: """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "additionalProperties": false,
              "required": [
                "HttpClientCalls", "GrpcClientCalls", "GrpcServerImpls",
                "MessagingPublishes", "MessagingConsumes", "SharedPackageImports",
                "SignalRHubMethods", "SignalRClientInvokes"
              ],
              "properties": {
                "HttpClientCalls": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["Method", "Path"],
                    "properties": {
                      "Method": { "type": "string" },
                      "Path": { "type": "string" },
                      "BaseUrl": { "type": ["string", "null"] },
                      "Evidence": { "type": ["string", "null"] }
                    }
                  }
                },
                "GrpcClientCalls": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["Service", "Method"],
                    "properties": {
                      "Service": { "type": "string" },
                      "Method": { "type": "string" },
                      "Evidence": { "type": ["string", "null"] }
                    }
                  }
                },
                "GrpcServerImpls": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["Service", "Method"],
                    "properties": {
                      "Service": { "type": "string" },
                      "Method": { "type": "string" },
                      "Evidence": { "type": ["string", "null"] }
                    }
                  }
                },
                "MessagingPublishes": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["Kind"],
                    "properties": {
                      "Kind": { "type": "string" },
                      "Exchange": { "type": ["string", "null"] },
                      "RoutingKey": { "type": ["string", "null"] },
                      "Topic": { "type": ["string", "null"] },
                      "Queue": { "type": ["string", "null"] },
                      "MessageType": { "type": ["string", "null"] }
                    }
                  }
                },
                "MessagingConsumes": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["Kind"],
                    "properties": {
                      "Kind": { "type": "string" },
                      "Exchange": { "type": ["string", "null"] },
                      "RoutingKey": { "type": ["string", "null"] },
                      "Topic": { "type": ["string", "null"] },
                      "Queue": { "type": ["string", "null"] },
                      "MessageType": { "type": ["string", "null"] }
                    }
                  }
                },
                "SharedPackageImports": {
                  "type": "array",
                  "items": { "type": "string" }
                },
                "SignalRHubMethods": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["Service", "Method"],
                    "properties": {
                      "Service": { "type": "string" },
                      "Method": { "type": "string" },
                      "Evidence": { "type": ["string", "null"] }
                    }
                  }
                },
                "SignalRClientInvokes": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["Service", "Method"],
                    "properties": {
                      "Service": { "type": "string" },
                      "Method": { "type": "string" },
                      "Evidence": { "type": ["string", "null"] }
                    }
                  }
                }
              }
            }
            """);

    // -----------------------------------------------------------------------
    // QuestionGeneration (BE-036, Sprint 5)
    // -----------------------------------------------------------------------
    private static ExtractionPrompt BuildQuestionGeneration() => new(
        Id: ExtractionPromptId.QuestionGeneration,
        Version: CurrentVersion,
        ToolName: "record_questions",
        ToolDescription: "Record the minimum set of clarifying questions a human owner can answer to resolve real ambiguity in the supplied evidence.",
        SystemPrompt: """
            You are a senior engineer reviewing extracted architectural metadata.
            Your job: ask the *minimum* set of clarifying questions a human owner
            could answer to resolve real ambiguity. NEVER ask cosmetic questions.
            Output JSON via the provided tool.

            Rules:
            - Prefer FEWER, higher-signal questions. Zero questions is a valid
              answer when the evidence is unambiguous.
            - Each question must be answerable by a human owner without reading
              the entire repository — cite concrete file paths or node names.
            - Each question should be a single concrete question, <= 200 chars.
            - Provide multiple-choice `choices` (2-5 entries) only when the
              ambiguity is genuinely categorical (e.g. service ownership). Skip
              `choices` for open-ended questions.
            - `severity` reflects blast radius: "high" = architectural decision
              affecting multiple services; "medium" = single-service ownership /
              boundary; "low" = naming / docs.
            - `related_files` / `related_nodes` MUST be a subset of the evidence.
            - Never emit a question whose answer is already obvious in evidence.
            """,
        UserPromptTemplate: """
            Subject: {subject}

            Evidence:
            {evidence_markdown}

            Related files (for grounding):
            {related_files}

            Related nodes (for grounding):
            {related_nodes}

            Emit the minimum set of clarifying questions. Zero is acceptable.
            """,
        OutputJsonSchema: """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "additionalProperties": false,
              "required": ["questions"],
              "properties": {
                "questions": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["topic", "question", "severity"],
                    "properties": {
                      "topic": { "type": "string", "description": "Short subject label, e.g. 'OrdersService.publishes'" },
                      "question": { "type": "string", "description": "Single concrete question, <=200 chars" },
                      "choices": { "type": "array", "items": { "type": "string" }, "description": "Optional. 2-5 candidate answers if multiple-choice helps." },
                      "severity": { "type": "string", "enum": ["low", "medium", "high"] },
                      "related_files": { "type": "array", "items": { "type": "string" } },
                      "related_nodes": { "type": "array", "items": { "type": "string" } }
                    }
                  }
                }
              }
            }
            """);
}
