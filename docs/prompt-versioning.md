# Prompt Versioning

Prompts are application assets. Changes to prompts can change product behavior and must be traceable.

## Prompt Template Fields

- template name;
- version;
- status: Draft, Active, Archived;
- content hash;
- system message;
- user message template;
- variables;
- created timestamp;
- optional description.

Example templates:

- `rag-answer:v1`
- `summarizer:v1`
- `tool-planner:v1`
- `evaluation-judge:v1`

## Rules

- Prompt versions are immutable after activation.
- A prompt edit creates a new version.
- How a template variable's value is built in code is not a prompt edit: the retrieved-context framing described in `docs/rag-pipeline.md` changes the string bound to the `context` variable, so `rag-chat:v1` is unchanged and no new template version is created.
- At most one version should be active per template and environment.
- Changing the active version creates an audit event.
- Prompt variables should have a clear schema/contract.

## Logging

Every AI request log records:

- `promptTemplateName`
- `promptTemplateVersion`
- `promptTemplateContentHash`

Full rendered prompt logging remains disabled by default.

## Current Implementation

Prompt templates are versioned JSON seed files embedded into the Application assembly and loaded into an in-memory provider. This keeps the local model gateway, direct chat, RAG, evaluation and agentic chat flows deterministic.

A database-backed runtime provider is a future adapter. It should preserve the same metadata, enforce one active version per template and environment, and record activation audit events.
