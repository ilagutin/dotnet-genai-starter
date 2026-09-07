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

The loop stops with a bounded status such as `StepLimitExceeded`,
`TimedOut`, `ToolLimitExceeded`, `BudgetExceeded`, `ToolRejected` or
`ApprovalRequired`.

## Non-goals

- No production-grade autonomous multi-agent platform.
- No real email sending in the starter-kit sample.
- No arbitrary SQL execution.
- No privileged infrastructure credentials exposed to the model.
- No durable, second-principal, single-use approval workflow.

## Relationship To Safe Tools

`safe-tools.md` defines tool contracts and policy. Agentic chat uses those tools inside a bounded loop.
