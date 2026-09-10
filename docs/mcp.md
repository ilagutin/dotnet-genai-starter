# MCP Host And External Tools

`GenAIPlatform.Mcp` is a local stdio Model Context Protocol host for the starter kit. It gives AI clients such as Claude Desktop and Claude Code a fourth consumption surface over the same backend modules used by REST, Worker and the Evaluations CLI.

The host composes explicit Application modules and Infrastructure adapters. MCP tools call Application use cases through `IApplicationDispatcher`, so tenant, user, retrieval and tool-policy behavior stays in the backend. The host is consumer-only: it does not upload documents, mutate document state or expose arbitrary tool execution.

## Service Identity

The local stdio host runs as a configured service identity:

```json
{
  "GenAIPlatform": {
    "Mcp": {
      "Identity": {
        "UserId": "mcp-user",
        "TenantId": "local",
        "Roles": [ "developer" ],
        "Groups": []
      }
    }
  }
}
```

Application handlers see this identity through `IUserContext` and `IBackgroundUserContext`. Listing tools is not the security boundary; handlers still enforce authorization, retrieval filters and tool policy. Per-caller remote MCP authentication is future scope and is not part of the local v0.2.0 host.

## Tools

- `server_info`: returns host version and active service identity details.
- `rag_answer`: answers a question with existing permission-aware RAG retrieval. It preserves the normal no-context fallback, citation behavior and access filters before prompt construction.
- `get_usage`: returns AI request usage totals with the same Application usage rules as other hosts.
- `get_current_user_profile`: calls the governed Agentic tool use case for the built-in safe profile tool and writes a row to `genai.tool_audit_logs`.

There is no generic `execute_tool_by_name`, `run_tool` or registry executor exposed through MCP. `list_documents` is not exposed because the starter kit does not currently have a read-only user document list use case.

`get_usage` authenticates the active service identity before checking roles or the date range.
Non-admin identities require a nonblank user and tenant. Omitted or blank user/tenant filters use
that identity; explicit mismatches are forbidden using case-sensitive comparison. Authenticated
admins (case-insensitive role name) may query aggregate or cross-scope totals. Reversed date ranges
remain validation errors and equal dates are allowed. Denials never query usage storage and become
`McpException` errors with the `get_usage failed:` prefix. REST maps the same policy to 401 for
unauthenticated/incomplete non-admin identity and 403 for scope mismatch. The local configured
identity is shared by callers of that host; this is not per-caller remote authentication.

## Approval Limitation

The v0.2.0 MCP host surface supports only safe tools. Approval-required tools are intentionally outside the host surface because there is not yet a broadly supported standard interactive approval flow for MCP clients. If a `RequiresApproval = true` tool is invoked through the direct Application governed-tool use case without approval, it fails closed with `approval_required` and writes an audit row; the MCP host does not expose a path to successfully execute it.

Future protocol support for interactive elicitation or approvals may change this, but the current host keeps approval-required tools out of the MCP tool list.

## External MCP Tools In Agentic Chat

`v0.3.0` adds MCP client support for consuming external stdio MCP servers from the platform's agentic loop. This is intentionally different from exposing the local MCP host as a generic executor: external MCP tools are adapted inside Infrastructure, registered as Agentic tools through the Application port and then evaluated by the same backend validation, policy, approval, budget and audit path as built-in tools.

No external servers are configured by default. When a server is configured and enabled, the Infrastructure adapter connects to it, lists its tools and creates backend-owned tool wrappers. The final model-facing names are sanitized, provider-safe and prefixed as `mcp_<server>_<tool>`, so an external server cannot shadow a built-in tool name or bypass blacklist policy by choosing a conflicting name.

Configuration lives under `GenAIPlatform:ExternalMcp:Servers`:

```json
{
  "GenAIPlatform": {
    "ExternalMcp": {
      "ConnectOnStartup": true,
      "MaxParallelConnects": 4,
      "RefreshInterval": "00:01:00",
      "Servers": [
        {
          "Name": "local-tools",
          "Enabled": true,
          "Command": "npx",
          "Arguments": [ "-y", "@modelcontextprotocol/server-everything" ],
          "WorkingDirectory": null,
          "AllowedTools": [ "echo" ],
          "SchemalessTools": [],
          "ConnectTimeoutSeconds": 10,
          "ToolCallTimeoutSeconds": 30
        }
      ]
    }
  }
}
```

The `Servers` list is the server allow-list: only configured, enabled servers are considered. `AllowedTools` is an optional per-server tool allow-list. When `AllowedTools` is empty, all tools reported by that enabled server are considered; when it is populated, only exact case-sensitive matching original external tool names can be exposed to the agentic registry.

External tools without an input schema are omitted by default. A server may opt in an exact case-sensitive original name through `SchemalessTools`; that narrow exception synthesizes an object-only planning schema, remains subject to `AllowedTools`, and is still approval-required. Blank or duplicate schemaless entries fail configuration validation. When `AllowedTools` is non-empty, every schemaless entry must be an exact member of it, so case mismatches and invalid subsets fail closed. A present malformed or unsupported schema is never converted into a schemaless exception.

Connection lifecycle is controlled at the `ExternalMcp` level. `ConnectOnStartup` (default `true`) runs a startup warmup; set it to `false` to connect on demand instead. `MaxParallelConnects` (default `4`) bounds how many servers connect concurrently so one slow or hung server cannot delay the others, while connect order never changes the deterministic tool listing. `RefreshInterval` (default one minute, `00:00:00` to disable) is a background pass that re-attempts servers which are not currently available and lists their tools; already-available servers are left untouched. Startup is non-blocking: the warmup and recovery run in the background, so an unreachable server never delays host startup. One hosted wrapper is the shutdown owner for the single non-disposable manager. Shutdown rejects new work, lifetime-cancels background/connect/call work, observes cleanup and disposes racing connections once. A canceled host stop token bounds only its wait; later async disposal still observes completion. Lifecycle logs use a separate deterministic, sanitized server-identity projection capped at 64 characters; this does not change canonical snapshot or configured server identity.

External tool definitions are treated as untrusted input. Descriptions are sanitized and length-limited before they can enter a model prompt. Present schemas are preserved rather than replaced, and the Application validator evaluates the captured schema before semantic normalization, approval or an MCP client call. It uses explicit Draft 2020-12, 64 KiB schema and argument limits, depth 32, at most 256 schema nodes, no non-fragment references, and no `pattern` or `patternProperties` at schema locations. Property and definition names remain ordinary names, and literal `const`, `enum` and `examples` data remains opaque to keyword restrictions. Local fragment references, including `$defs` references, remain supported and their reached schema targets are inspected. No network resolver is installed. Tool argument payloads that pass validation are then passed through JSON round-trip conversion at the adapter boundary so nested objects and arrays are preserved.

## External Tool Governance Guarantees

The external MCP adapter keeps governance in the platform:

- Snapshot at connect: tool name, description and input schema are captured when the server connects.
- Snapshot provenance: the snapshot hash covers the schema plus whether it was synthesized by the schemaless exception. It becomes the backend-owned tool schema version and is written to tool audit provenance. The model-proposed schema version does not control external-tool audit provenance.
- Prefixed names: external tools are exposed as provider-safe `mcp_<server>_<tool>` names after ASCII sanitization and length limiting.
- Approval by default: every external MCP tool is registered as approval-required, regardless of how the external server describes itself.
- Backend allow-list: only configured servers and allowed tools can appear in the agentic registry.
- Fail closed/degrade: unavailable servers produce no available tools or failed tool results. Connection failure before dispatch returns `mcp_server_unavailable`; caller cancellation before dispatch propagates without calling the client. After dispatch starts, a transport exception, timeout or shutdown cancellation returns `mcp_tool_outcome_unknown`, because the remote operation may already have completed. A server that is unavailable at startup recovers on the next background refresh pass (or an explicit refresh).
- No automatic replay: an uncertain call is never repeated by the connection manager. Its broken connection is removed and disposed. A later independent request can reconnect and execute its own call; background or explicit refresh can also recover availability, but never replay the pending operation. Failure to reconnect remains `mcp_server_unavailable` with no tool dispatch.
- Audit path: executed, approval-required, rejected and failed external tool calls go through the same tool audit mechanism as built-in Agentic tools.

`MaxToolResultBytes` defaults to 32768 bytes (32 KiB) and must be configured from 23 bytes through 1048576 bytes (1 MiB). The upper bound keeps the result contract bounded even under configuration mistakes, while the lower bound ensures the authored omission object always fits. The adapter maps only MCP `Content` and `StructuredContent` to provider-neutral JSON; SDK root and content-block metadata are excluded while fields inside structured business data, including a field named `meta`, are preserved. Exact-limit JSON remains available to the execution response and next model step. Oversized JSON is replaced by a small valid omission object with exact source/returned UTF-8 byte counts and a truncation marker, never a partial JSON prefix. Size limiting is not content redaction.

External MCP durable audit is a separate metadata-only boundary selected explicitly by the wrapper, not inferred from the `mcp_` name. Argument values, returned text/structured content and raw exception messages are omitted for success, remote error and non-execution paths. Existing identity, schema, validation, policy, approval, execution and error-code fields remain. Built-in audit content is unchanged. Historical rows are not rewritten and may contain content recorded before this boundary. Full rendered prompt logging is disabled by default; MCP SDK wire logging is suppressed, and lifecycle diagnostics contain only bounded sanitized server identity and exception type.

Caller cancellation after dispatch follows the same uncertainty rule. The governed executor first awaits the metadata-only audit write with `error_code=mcp_tool_outcome_unknown` and then propagates cancellation. Unknown outcomes have null durable `output` and `error_message`, because no confirmed response or measured response size exists. Audit failure is not reported as successful execution.

`Failed` tool execution and `ToolFailed` chat status describe a locally unconfirmed completion; they do not prove remote failure or rollback. Without caller cancellation, an unknown outcome stops the loop, skips remaining proposed tools and prevents another model step. Operators should use the audit identity, tool-call id and correlation id to reconcile the operation with the remote system before authorizing another request. This starter kit provides neither exactly-once execution nor remote idempotency or automatic reconciliation. Retrying an operation without checking its remote state can duplicate its effects.

The wrapper and its validation schema are frozen for an agentic chat session. A later mutation of a listed descriptor cannot change that session's validation contract or audit snapshot hash. This is not remote attestation: reconnecting to a server does not re-list or prove that its implementation still matches the captured schema.

Exceptions swallowed by the tool wrapper emit warning 4001,
`ExternalMcpToolExecutionFailed`, including cancellation not requested by the
caller. Its only data fields are sanitized `ServerName` and `ToolName` (each
capped at 64 ASCII characters) and `ExceptionType`. It receives no exception
object, raw exception message, arguments, result or schema. Caller cancellation
preserves the propagation and post-dispatch audit rules above without this
warning. Transport failures already converted to unknown outcomes remain covered
by connection-manager lifecycle diagnostics. Logging does not trigger replay.

This starter-kit release does not claim production-ready remote MCP authentication, secret storage or enterprise connector management. External stdio server configuration is a local/sample adapter pattern; production credential handling and remote multi-tenant MCP are future work.

The main gate uses fake external tool sources and adapter-level fakes for deterministic coverage. It does not rely on real child-process MCP servers such as `npx` during automated tests.

## Run The Built Host

Build the host first. Do not use `dotnet run` from an MCP client because build output can pollute stdout; MCP stdio stdout must contain protocol frames only. Host logs are configured for stderr.

```powershell
dotnet build src\GenAIPlatform.Mcp\GenAIPlatform.Mcp.csproj
$env:ConnectionStrings__GenAIPlatform = "Host=localhost;Port=5432;Database=genai_platform;Username=genai;Password=genai_dev_password"
dotnet .\src\GenAIPlatform.Mcp\bin\Debug\net10.0\GenAIPlatform.Mcp.dll
```

The RAG and usage tools need the same PostgreSQL database used by the API and Worker. For the local demo, start PostgreSQL first:

```powershell
docker compose up -d postgres
```

## Claude Desktop Configuration

Example `claude_desktop_config.json` entry:

```json
{
  "mcpServers": {
    "genai-platform": {
      "command": "dotnet",
      "args": [
        "C:\\path\\to\\your\\checkout\\src\\GenAIPlatform.Mcp\\bin\\Debug\\net10.0\\GenAIPlatform.Mcp.dll"
      ],
      "env": {
        "ConnectionStrings__GenAIPlatform": "Host=localhost;Port=5432;Database=genai_platform;Username=genai;Password=genai_dev_password"
      }
    }
  }
}
```

Use the built DLL path from your local checkout. Keep provider API keys out of this file unless you intentionally override the default mock providers for a local experiment.
