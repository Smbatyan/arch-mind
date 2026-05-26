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
    public const string CurrentVersion = "2026-05-25/v1";

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
            You are a microservices architecture analyzer. Read a single source file
            and decide whether it belongs to an identifiable microservice / deployable
            unit, and if so describe that service.

            Rules:
            - Emit a single tool call with the structured result.
            - If the file is not relevant (README, image, lock file, generated code,
              vendored third-party library), emit an empty/negative result
              (IsPartOfService=false, empty TechStack), NOT a refusal.
            - Be precise. Prefer null/empty over guessing.
            - Service identity hints: Program.cs / main.go / app.py / manage.py /
              package.json "name", Dockerfile, deployment manifests, README service
              headings, namespace/package names, csproj/pom.xml/build.gradle.
            - TechStack should be concrete and short (e.g. ["aspnet-core", "ef-core",
              "postgres"], ["express", "node"], ["fastapi", "python"]).
            - RootPath is the directory that looks like the service root relative to
              the repo (e.g. "services/billing", "backend"). Null if unknown.
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
            You are a microservices architecture analyzer. Read a single source file
            and extract HTTP endpoints (routes) that this file declares or registers.

            Rules:
            - Emit a single tool call with the structured result.
            - If the file is not relevant (README, image, infra config without
              routes), emit an empty Endpoints array, NOT a refusal.
            - Look for ASP.NET Core (app.MapGet/MapPost, [HttpGet("...")],
              [Route("...")]), Express (app.get/post/put/delete/use("...", ...)),
              Flask/FastAPI (@app.route, @router.get), Spring (@GetMapping,
              @RequestMapping), Gin/Echo/Chi (router.GET, e.GET), Rails routes,
              and similar.
            - Method is uppercased HTTP verb: GET, POST, PUT, PATCH, DELETE.
              If a single registration covers multiple verbs, emit one endpoint per verb.
            - Path is the route template exactly as written (e.g. "/api/users/{id}").
              Preserve placeholder syntax.
            - HandlerSymbol is the function/method/class name that handles the route
              (e.g. "UsersController.GetById", "handleCreate", "users#show").
              If the handler is an inline lambda with no name, use "<lambda>".
            - Do NOT invent endpoints. If unsure, omit.
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
            You are a microservices architecture analyzer. Read a single source file
            and extract events / messages this file PUBLISHES (produces / sends /
            emits) to a message bus or event stream.

            Rules:
            - Emit a single tool call with the structured result.
            - If the file is not relevant, emit an empty Publishes array, NOT a refusal.
            - Look for Kafka producers (producer.Send, ProduceAsync),
              RabbitMQ / MassTransit (IBus.Publish, channel.BasicPublish),
              AWS SNS/SQS (sns.publish, sqs.sendMessage),
              Azure Service Bus (ServiceBusSender.SendMessageAsync),
              Google Pub/Sub (publisher.publish), NATS (nc.Publish),
              Redis Streams (XADD), Kinesis (PutRecord), and similar.
            - Name is the event/message type or topic-as-event name
              (e.g. "OrderCreated", "user.signup.v1").
            - Version is the explicit version if present in the type/topic/header
              (e.g. "v1", "2"); otherwise null.
            - SchemaSummary is a one-line description of the payload fields if you
              can see them in the file; otherwise null.
            - Topic is the destination topic / queue / channel name if visible;
              otherwise null.
            - Only emit events that this file ACTIVELY publishes. Do NOT include
              consumer handlers or test fixtures.
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
            You are a microservices architecture analyzer. Read a single source file
            and extract events / messages this file CONSUMES (subscribes / handles /
            receives) from a message bus or event stream.

            Rules:
            - Emit a single tool call with the structured result.
            - If the file is not relevant, emit an empty Consumes array, NOT a refusal.
            - Look for Kafka consumers (Consume, AddConsumer<T>),
              RabbitMQ / MassTransit (IConsumer<T>, channel.BasicConsume),
              AWS SQS handlers, Azure Service Bus (ServiceBusProcessor / [ServiceBusTrigger]),
              Google Pub/Sub subscribers, NATS (nc.Subscribe),
              Redis Streams (XREADGROUP), Kinesis (GetRecords), and similar.
            - Name is the event/message type being consumed
              (e.g. "OrderCreated", "user.signup.v1").
            - Version is the explicit version if present; otherwise null.
            - SchemaSummary is a one-line description of the payload fields if
              visible; otherwise null.
            - Topic is the source topic / queue / channel if visible; otherwise null.
            - Only emit events that this file ACTIVELY consumes. Do NOT include
              producers or shared DTO definitions without a handler.
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
            You are a microservices architecture analyzer. Read a single source file
            and extract storage dependencies (databases, caches, blob stores, search
            indexes, queues-as-storage) referenced by this file.

            Rules:
            - Emit a single tool call with the structured result.
            - If the file is not relevant, emit an empty Storages array, NOT a refusal.
            - Look for: connection strings, EF Core / Dapper / SQLAlchemy / TypeORM /
              GORM / ActiveRecord setup, MongoDB clients, Redis clients (StackExchange.Redis,
              ioredis), Elasticsearch / OpenSearch clients, S3 / Azure Blob / GCS SDK
              calls, schema migrations (Flyway, Alembic, EF migrations).
            - Type values (concrete, lowercase): "postgres", "mysql", "sqlserver",
              "sqlite", "mongodb", "redis", "elasticsearch", "opensearch", "s3",
              "azure-blob", "gcs", "dynamodb", "cassandra", "other".
            - Access is "owns" if the file/service appears to define the schema,
              run migrations, or be the system of record. Use "reads" if it only
              queries / fetches without owning the schema. When uncertain, use "reads".
            - Name is the database name, bucket name, index name, or logical
              identifier (e.g. "billing_db", "user-avatars", "orders-index").
              Use the most specific name visible.
            - ConnectionHint is the connection-string variable, config key, or host
              snippet if visible (e.g. "ConnectionStrings:BillingDb",
              "DATABASE_URL"); otherwise null. Do NOT include secret values.
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
            You are a microservices architecture analyzer. Read a single source file
            and identify architectural or coding conventions evident in it that
            might be reusable knowledge for the team.

            Rules:
            - Emit a single tool call with the structured result.
            - If the file is not relevant, emit an empty Conventions array, NOT a refusal.
            - Only report conventions that are CLEARLY VISIBLE in this file. Do not
              speculate about repo-wide standards from a single file.
            - Examples of valid conventions:
              * Naming: "Controllers suffixed with 'Controller'", "Async methods end with 'Async'".
              * Error handling: "All endpoints return ProblemDetails on error".
              * Logging: "Uses Serilog with structured properties".
              * Security: "All endpoints require [Authorize] by default".
              * Layering: "Repository pattern via I<Entity>Repository".
              * Testing: "xUnit + FluentAssertions".
              * Config: "Options pattern via IOptions<T>".
            - Category is a short lowercase tag: "naming", "error-handling",
              "logging", "security", "layering", "testing", "config", "other".
            - Name is a 2-6 word title.
            - Description is one sentence, concrete and specific.
            - Prefer FEWER, higher-signal conventions over many weak ones.
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
