# Safe Tools

Tool execution is controlled by backend policy. The model can propose actions,
but deterministic backend code decides whether they run.

## Flow

```text
LLM proposes tool call
-> backend runs the tool's manual shape and semantic validation
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

Tool definitions declare versioned JSON Schema for model planning. Current
built-in `Validate` methods enforce manual shape and semantic checks before
execution and return sanitized arguments to the executor. The declared JSON
Schema is not yet applied as a common runtime enforcement mechanism. In
particular, `ExternalMcpAgentTool` accepts a missing/null value as an empty
object and otherwise accepts any JSON object; it does not validate the payload
against `ExternalMcpToolSnapshot.InputSchema`. General JSON Schema enforcement,
including external snapshot-schema validation, is deferred to BL-019.

## Audit Log

Tool audit records are stored in `genai.tool_audit_logs`. The current audit
projection persists identity and correlation fields, tool/schema and policy
metadata, validation status, approval state, execution status, the validation
result's sanitized arguments, output, optional error information and its
timestamp. This release does not change that projection: it is not a
metadata-only or redaction-only audit boundary because the repository persists
the validation result's sanitized arguments and output as JSONB. That
audit-boundary redesign is
deferred to BL-020.

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
| Built-in tools declare model-facing JSON Schema and perform manual validation. | `src/GenAIPlatform.Application.Agentic/Tools/DraftEmailTool.cs` | `DraftEmailTool.Definition`, `DraftEmailTool.Validate` |
| Built-in support-ticket validation includes semantic priority validation. | `src/GenAIPlatform.Application.Agentic/Tools/CreateSupportTicketTool.cs` | `CreateSupportTicketTool.Validate` |
| No common declared-JSON-Schema runtime enforcement exists in this execution path; it invokes the selected tool's `Validate` method. | `src/GenAIPlatform.Application.Agentic/Tools/Execution/GovernedAgentToolExecutor.cs` | `GovernedAgentToolExecutor.ExecuteAsync` |
| External MCP payloads are only constrained to an object and are not checked against the snapshot schema. | `src/GenAIPlatform.Infrastructure/Mcp/ExternalMcpAgentTool.cs` | `ExternalMcpAgentTool.Validate` |
| Every available external MCP tool is assigned approval-required policy. | `src/GenAIPlatform.Infrastructure/Mcp/ExternalMcpAgentTool.cs` | `ExternalMcpAgentTool.Policy` |
| The API maps `approveRiskyTools` to one Boolean command field, which the handler retains in the session and executor context. | `src/GenAIPlatform.Api/Endpoints/V1/Chat/ChatEndpoints.cs` | `ChatEndpoints.CreateAgenticChatCompletion` |
| The command, session and execution context each carry `ApproveRiskyTools` as a Boolean rather than a per-call approval object. | `src/GenAIPlatform.Application.Agentic/Chat/Command.cs` | `AgenticChatCommand.ApproveRiskyTools` |
| The session carries only the Boolean approval flag to each governed execution; it carries no per-call approval artifact. | `src/GenAIPlatform.Application.Agentic/Chat/Loop/AgenticChatSession.cs` | `AgenticChatSession.ApproveRiskyTools` |
| The execution context has one caller `UserId` and the Boolean `ApproveRiskyTools`; it has no separate approver field. | `src/GenAIPlatform.Application.Agentic/Tools/Execution/AgentToolExecutionContext.cs` | `AgentToolExecutionContext` |
| An approval-required call without the flag stops with `ApprovalRequired`; validation and policy run first. | `src/GenAIPlatform.Application.Agentic/Tools/Execution/GovernedAgentToolExecutor.cs` | `GovernedAgentToolExecutor.ExecuteCoreAsync` |
| A successful approval-required execution receives `SimulatedApproved`. | `src/GenAIPlatform.Application.Agentic/Tools/Execution/AgentToolExecutionOutcome.cs` | `AgentToolExecutionOutcome.Executed` |
| The governed executor invokes the audit writer after it creates an execution result. | `src/GenAIPlatform.Application.Agentic/Tools/Execution/GovernedAgentToolExecutor.cs` | `GovernedAgentToolExecutor.ExecuteAsync` |
| The audit writer creates the current tool-audit entry projection. | `src/GenAIPlatform.Application.Agentic/Tools/Execution/AgentToolAuditLogWriter.cs` | `AgentToolAuditLogWriter.WriteAsync` |
| The audit entry records approval state, but has no separate approver identity or approval token field. | `src/GenAIPlatform.Domain/Agentic/ToolAuditLogEntry.cs` | `ToolAuditLogEntry` |
| The PostgreSQL repository inserts the current projection into `genai.tool_audit_logs`, including the validation result's sanitized arguments and output as JSONB. | `src/GenAIPlatform.Infrastructure/Agentic/PostgresToolAuditLogRepository.cs` | `PostgresToolAuditLogRepository.AddAsync` |
