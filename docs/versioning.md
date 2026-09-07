# Versioning

This project versions more than releases. GenAI behavior depends on code, prompts, models, retrieval settings, embeddings, evaluations and pricing.

## Project Releases

- Use SemVer.
- Stay in `0.x` until extension points and public contracts stabilize.
- Treat `v0.1.0` as the first public reference release.
- Reserve `v1.0.0` for a future stable starter kit.

## Public Release Flow

The public repository treats `main` as release-ready history. A public pull request should be ready to become a GitHub Release as soon as it is merged.

- Public changes land through pull requests into protected `main`.
- Never push directly to `main`. An automated agent may push a feature or release branch and open a pull request only after explicit maintainer approval; agents do not push tags.
- Before opening a pull request, run the applicable local gate, stage only intended paths explicitly, never use `git add -A`, and keep commits atomic and green: one logical change per commit, each passing its gate.
- Squash merge is the expected public merge strategy: one merged PR becomes one public release commit.
- Release PRs update `VERSION` with SemVer without a leading `v`, for example `0.2.0`.
- Release PRs also update `CHANGELOG.md` and add `docs/release-notes-v<version>.md`, for example `docs/release-notes-v0.2.0.md`.
- After a release PR that changes `VERSION` is merged to `main`, the `publish-release` workflow runs automatically on that `main` push.
- The workflow reads `VERSION`, builds the tag name (`v0.2.0`), verifies the matching release-notes file, reruns the release gate, tags the current `main` commit and creates the GitHub Release.
- The release gate includes locked restore, a release build, format, code-organization, vulnerability and mandatory Docker-backed full-solution tests.
- Non-release PRs must not change `VERSION`; their merges do not run `publish-release`.
- If the tag already exists at the same `main` commit, the workflow can resume publishing. If the tag exists at another commit, the workflow fails instead of moving history.
- Normal releases do not require pressing `Run workflow`; the `VERSION`-changing merge to `main` is the release trigger.
- `workflow_dispatch` from `main` is the sole recovery path and its version must match the current `VERSION` file.
- Direct tag pushes do not trigger publishing. Agents must not create or push release tags.

Do not auto-increment release versions in CI. The version is part of the reviewed release PR so maintainers can choose patch, minor or major intentionally.

## API

- Use `/api/v1/...` from the start.
- Avoid breaking documented `v1` response contracts once examples depend on them.

## Database

- Keep schema changes in source control.
- Document ingestion and retrieval currently use explicit raw SQL/init scripts and small Npgsql adapters while the persistence surface is still stabilizing.
- If EF Core is introduced later for broader persistence, use migrations and name them after the use case or schema change.

## Prompts

- Unique identity: `(templateName, version)`.
- Prompt versions are immutable after activation.
- Store content hash.
- Log template name, version and content hash for every AI request.

## Documents, Chunks and Embeddings

- Documents have versions.
- Chunks are tied to document version, chunking profile version, position and text hash.
- Embeddings store provider, model, dimensions, chunking profile version and created timestamp.
- Retrieval stores both relational `real[]` values and pgvector values so historical metadata remains inspectable while search can use pgvector operators.
- Default RAG retrieval uses the current `documents.version` and excludes older chunk versions unless a future explicit historical retrieval mode is added.
- RAG retrieval filters by embedding provider and model. After an embedding provider/model change, re-index documents before expecting those new embeddings to retrieve older content.
- Re-indexing creates new records instead of silently changing old retrieval history.

## Evaluations

Evaluation runs record:

- dataset version;
- runner version;
- model and settings;
- prompt version;
- retrieval configuration.

## Pricing

Pricing records include effective dates so historical cost calculations remain reproducible.

## Tool Calls

- tool name;
- tool schema version;
- tool policy version.

These fields keep proposed, approved, rejected and executed tool calls
reproducible after a tool schema or backend policy changes.
