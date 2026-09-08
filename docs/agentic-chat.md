# Agentic Chat

The goal is not a broad autonomous agent platform. The goal is a controlled agentic chat loop that demonstrates safe tool use.

## Requirements

- Model Gateway can represent proposed tool calls.
- Agent loop has max steps.
- Agent loop has timeout.
- Agent loop has token/cost budget.
- Backend policy decides which tools are available for the current user/request.
- Safe tools may execute automatically.
- Risky tools require simulated approval.
- Forbidden tools are rejected.
- Every tool execution outcome is written to the tool audit log.

## Endpoint

```http
POST /api/v1/chat/agentic
```

Example body:

```json
{
  "message": "Use my profile to create a support ticket.",
  "correlationId": "demo-agentic-1",
  "approveRiskyTools": false
}
```

`approveRiskyTools` is a request-scoped, demo-only simulated approval flag. It
is supplied by the same request caller, has no second approving principal, and
is not bound to one tool call or one argument set. Without it, risky tools
return `ApprovalRequired` and do not execute. With it, backend validation and
policy still apply, but every later model-selected risky call in that request
is pre-approved to execute. A successful execution that required this flag is
audited with approval state `SimulatedApproved`. `DraftEmail` still creates a
draft only and never sends email.

## Limits

Default local limits:

- max steps: 4;
- timeout: 15 seconds;
- max tool calls: 8;
- max total tokens: 4096;
- max estimated cost: 0.05 USD-equivalent demo budget.

Agentic budget checks use the same effective provider/model pricing records as
AI request logging when a matching pricing row exists. If pricing is unavailable
or no matching record is configured, the loop falls back to the local
`EstimatedCostPerThousandTokens` demo estimate so the starter kit still enforces
a bounded cost budget in mock/local setups.

The first fallback in each conversation emits warning event 4002,
`AgenticBudgetFallbackUsed`. A missing estimate uses `pricing_unavailable`;
an estimator exception uses `estimator_failed` plus its type. Later steps in the
same conversation do not repeat the warning, while another conversation in the
same dependency-injection scope has its own warning state. Structured
`ConversationId` and the session's validated `CorrelationId` attribute each
warning to its conversation and request. Successful estimates
and cancellation do not emit it. No response content or exception message is
logged in this event. This diagnostic does not change the demo arithmetic:
missing total-token usage still contributes zero, and budget checks currently
stop only when a total is greater than its configured limit.

The loop stops with a bounded status such as `StepLimitExceeded`,
`TimedOut`, `ToolLimitExceeded`, `BudgetExceeded`, `ToolRejected`, `ToolFailed` or
`ApprovalRequired`.

`ToolFailed` means that backend execution did not confirm successful completion.
For external MCP tools, a lost response, timeout or shutdown cancellation after
dispatch produces `mcp_tool_outcome_unknown`: the remote operation may have
completed. The loop stops, audits remaining proposals as not executed and makes
no further model call. The connection manager never replays the uncertain call;
reconnecting for a later independent request does not resolve its outcome.

Caller cancellation after external MCP dispatch first writes a metadata-only
audit with `mcp_tool_outcome_unknown`, null durable output and null error message,
then propagates cancellation. Cancellation before dispatch executes no tool.
`Failed` audit status does not prove that the remote operation failed or rolled
back. Reconcile with the remote system using the audit identity and correlation
fields before retrying. There is no exactly-once or remote idempotency guarantee.
See [MCP external tool governance](mcp.md#external-tool-governance-guarantees).

## Non-goals

- No production-grade autonomous multi-agent platform.
- No real email sending in the starter-kit sample.
- No arbitrary SQL execution.
- No privileged infrastructure credentials exposed to the model.
- No durable, second-principal, single-use approval workflow.

## Relationship To Safe Tools

`safe-tools.md` defines tool contracts and policy. Agentic chat uses those tools inside a bounded loop.
