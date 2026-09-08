# Safe Tools

Tool execution is controlled by backend policy. The model can propose actions,
but deterministic backend code decides whether they run.

## Flow

```text
LLM proposes tool call
-> backend validates the frozen declared schema as Draft 2020-12
-> backend runs tool-specific semantic normalization
-> policy layer evaluates registered metadata and risk
-> allowed tool executes
-> approval-required tool executes only with the request's simulated approval flag
-> forbidden or unknown tool is rejected
-> pipeline invokes the audit writer
```

## Implemented Demo Tools

- `GetCurrentUserProfile` (`v1`): read-only current demo user profile.
- `CreateSupportTicket` (`v1`): creates an idempotent demo ticket payload.
- `DraftEmail` (`v1`): creates a draft payload only; it never sends email.

Each registered tool owns model-facing definition and backend policy metadata.
Only a tool returned by the registry is available to the execution request;
unregistered names fail closed. The name denylist is defense in depth on top
of registration, so known destructive name fragments remain forbidden even if
a wrapper supplies approval-only metadata.

## Policy And Simulated Approval

- `GetCurrentUserProfile` and `CreateSupportTicket` are allowed.
- `DraftEmail` is risky and requires simulated approval; it still creates only
  a draft payload.
- `SendEmail`, `DeleteDocument` and `RunSqlQuery` are denied by the defense-in-
  depth name denylist and are not implemented as demo tools.
- Configured external MCP tools are all approval-gated before execution.

`approveRiskyTools` is a request-scoped, demo-only simulated approval flag.
It is supplied by the same request caller, has no second approving principal,
and is not bound to one tool call or one argument set. When true, it
pre-approves every later model-selected risky call in that request, subject to
backend validation and policy. When false, an approval-required call returns
`ApprovalRequired` without execution. A successful approval-required execution
writes `SimulatedApproved` as its audit approval state. This is not a durable,
single-use, second-principal approval workflow.

## Validation And Schemas

Tool definitions declare versioned JSON Schema for model planning and runtime
enforcement. One Application validator serves governed execution and audit of
skipped registered calls. It builds every schema explicitly as Draft 2020-12
with fresh registries and no network fetch callback, then uses list-form
evaluation. Schema validation runs before tool-specific semantic normalization,
policy outcome, simulated approval and execution. Forbidden and unknown policy
outcomes still fail closed with their existing public results; for a registered
approval-required tool, an invalid payload returns `schema_invalid` before any
approval check.

The validator rejects untrusted or unbounded schema definitions before
evaluation. Schemas and argument payloads are limited to 64 KiB of UTF-8 JSON,
JSON depth is limited to 32, and schemas are limited to 256 JSON nodes.
Non-fragment references, `pattern` and `patternProperties` are unsupported;
local fragment references such as `#/$defs/value` remain available. A declared
non-2020-12 dialect, malformed schema or unsupported schema definition fails
closed as `schema_definition_invalid`. A payload that does not match an
accepted schema fails as `schema_invalid`.

Validation errors contain only the first failing schema `EvaluationPath` in
ordinal order. The complete stored message is printable/control-normalized and
limited to 256 characters. Library error text, argument instance paths and raw
argument names or values are not included.

## Audit Log

Tool audit records are stored in `genai.tool_audit_logs`. The current audit
projection persists identity and correlation fields, tool/schema and policy
metadata, validation status, approval state, execution status, optional error
code and its timestamp. Built-in tools retain their compatible content audit:
sanitized arguments, output and optional error message. External MCP wrappers
explicitly select a metadata-only Application policy. Their arguments JSONB is
exactly an omission marker plus the source UTF-8 byte count; output JSONB is
null when absent or contains only an omission marker, source and returned
UTF-8 byte counts, and the truncation flag. Their durable error message is null.
External provenance is never inferred from the tool name.

External execution output is separately limited to 32 KiB of provider-neutral
JSON by default, configurable from 23 bytes through 1 MiB. Exact-limit content
passes. Oversized content becomes a small, valid omission object rather than a
partial JSON prefix. This limit is not redaction: permitted content within the
bound still reaches the response/model path, but never the metadata-only durable
projection. Full rendered prompt logging remains disabled by default. Historical
audit rows are not rewritten and may contain external arguments or output
recorded by an older release.

Schema-invalid built-in rows are a narrower safety exception: they store
`Invalid` validation, `ValidationFailed` execution, `schema_invalid`, `{}`
sanitized arguments, null output and only the bounded schema-owned error path
described above. Registered external rows instead use the same metadata-only
argument projection as every other external outcome and keep the durable error
message null.

## Requirements

- The LLM receives no infrastructure credentials.
- The model cannot execute tools directly; backend validation and policy decide
  execution.
- Unknown and forbidden tool names fail closed.
- Demo tools are side-effect-limited; `DraftEmail` creates a draft payload only
  and never sends email.

## Claim-To-Code

| Claim | File | Symbol |
| --- | --- | --- |
| Backend execution starts only in the governed executor after validation and policy evaluation. | `src/GenAIPlatform.Application.Agentic/Tools/Execution/GovernedAgentToolExecutor.cs` | `GovernedAgentToolExecutor.ExecuteCoreAsync` |
| Available tools come from the built-in registry plus external sources. | `src/GenAIPlatform.Application.Agentic/Tools/CompositeAgentToolRegistry.cs` | `CompositeAgentToolRegistry.GetAvailableTools` |
| A registered tool supplies both a model-facing definition and policy metadata. | `src/GenAIPlatform.Application.Agentic/Tools/IAgentTool.cs` | `IAgentTool.Definition`, `IAgentTool.Policy` |
| An unregistered tool is classified as forbidden, and destructive name fragments are denied before metadata is evaluated. | `src/GenAIPlatform.Domain/Agentic/ToolPolicy.cs` | `ToolPolicy.Decide` |
| `GetCurrentUserProfile` is an allowed read-only profile lookup. | `src/GenAIPlatform.Application.Agentic/Tools/GetCurrentUserProfileTool.cs` | `GetCurrentUserProfileTool.Policy`, `GetCurrentUserProfileTool.ExecuteAsync` |
| `CreateSupportTicket` is allowed and returns a deterministic demo ticket result. | `src/GenAIPlatform.Application.Agentic/Tools/CreateSupportTicketTool.cs` | `CreateSupportTicketTool.Policy`, `CreateSupportTicketTool.ExecuteAsync` |
| `DraftEmail` is approval-required and returns a local draft result with `sent = false`. | `src/GenAIPlatform.Application.Agentic/Tools/DraftEmailTool.cs` | `DraftEmailTool.Policy`, `DraftEmailTool.ExecuteAsync` |
| Built-in tools declare model-facing JSON Schema and retain semantic normalization after common schema validation. | `src/GenAIPlatform.Application.Agentic/Tools/DraftEmailTool.cs` | `DraftEmailTool.Definition`, `DraftEmailTool.Validate` |
| Built-in support-ticket validation trims nonblank text and supplies the default `normal` priority after schema enforcement. | `src/GenAIPlatform.Application.Agentic/Tools/CreateSupportTicketTool.cs` | `CreateSupportTicketTool.Validate` |
| Common Draft 2020-12 validation enforces bounded schemas and arguments without remote resolution and returns bounded schema-owned errors. | `src/GenAIPlatform.Application.Agentic/Validation/AgentToolArgumentValidator.cs` | `AgentToolArgumentValidator.Validate` |
| Governed execution applies common schema validation before semantic validation, policy outcome, approval and execution. | `src/GenAIPlatform.Application.Agentic/Tools/Execution/GovernedAgentToolExecutor.cs` | `GovernedAgentToolExecutor.ExecuteAsync` |
| Skipped registered calls use the same common schema and semantic validation path before audit. | `src/GenAIPlatform.Application.Agentic/Chat/Tools/AgentToolAuditWriter.cs` | `AgentToolAuditWriter.AuditSkippedToolCallsAsync` |
| External MCP execution uses the frozen snapshot schema exposed by its wrapper definition. | `src/GenAIPlatform.Infrastructure/Mcp/ExternalMcpAgentTool.cs` | `ExternalMcpAgentTool.Definition` |
| Every available external MCP tool is assigned approval-required policy. | `src/GenAIPlatform.Infrastructure/Mcp/ExternalMcpAgentTool.cs` | `ExternalMcpAgentTool.Policy` |
| The API maps `approveRiskyTools` to one Boolean command field, which the handler retains in the session and executor context. | `src/GenAIPlatform.Api/Endpoints/V1/Chat/ChatEndpoints.cs` | `ChatEndpoints.CreateAgenticChatCompletion` |
| The command, session and execution context each carry `ApproveRiskyTools` as a Boolean rather than a per-call approval object. | `src/GenAIPlatform.Application.Agentic/Chat/Command.cs` | `AgenticChatCommand.ApproveRiskyTools` |
| The session carries only the Boolean approval flag to each governed execution; it carries no per-call approval artifact. | `src/GenAIPlatform.Application.Agentic/Chat/Loop/AgenticChatSession.cs` | `AgenticChatSession.ApproveRiskyTools` |
| The execution context has one caller `UserId` and the Boolean `ApproveRiskyTools`; it has no separate approver field. | `src/GenAIPlatform.Application.Agentic/Tools/Execution/AgentToolExecutionContext.cs` | `AgentToolExecutionContext` |
| An approval-required call without the flag stops with `ApprovalRequired`; validation and policy run first. | `src/GenAIPlatform.Application.Agentic/Tools/Execution/GovernedAgentToolExecutor.cs` | `GovernedAgentToolExecutor.ExecuteCoreAsync` |
| A successful approval-required execution receives `SimulatedApproved`. | `src/GenAIPlatform.Application.Agentic/Tools/Execution/AgentToolExecutionOutcome.cs` | `AgentToolExecutionOutcome.Executed` |
| The governed executor invokes the audit writer after it creates an execution result. | `src/GenAIPlatform.Application.Agentic/Tools/Execution/GovernedAgentToolExecutor.cs` | `GovernedAgentToolExecutor.ExecuteAsync` |
| One Application projector applies content-compatible built-in audit and metadata-only external audit to executed and skipped calls. | `src/GenAIPlatform.Application.Agentic/Tools/Execution/AgentToolAuditProjection.cs` | `AgentToolAuditProjection.Create` |
| The audit entry records approval state, but has no separate approver identity or approval token field. | `src/GenAIPlatform.Domain/Agentic/ToolAuditLogEntry.cs` | `ToolAuditLogEntry` |
| The PostgreSQL repository inserts the Application-owned projection into the existing `genai.tool_audit_logs` columns. | `src/GenAIPlatform.Infrastructure/Agentic/PostgresToolAuditLogRepository.cs` | `PostgresToolAuditLogRepository.AddAsync` |
