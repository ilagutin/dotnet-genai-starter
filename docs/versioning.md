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

## Dependabot Lock Files

Central package updates can affect lock files in projects that Dependabot did not edit directly. For a
same-repository Dependabot NuGet pull request targeting `main`, the `dependabot-lockfiles` workflow
re-evaluates the whole solution and sends a lock-only follow-up commit to the Dependabot branch when
additional lock-file drift exists.

The workflow uses two fresh, read-only jobs. The prepare job checks out the exact pull request head
without persisted credentials, rejects unexpected pull request paths, runs force-evaluate and locked
restores, and publishes only validated members of the repository's 14-file lock allowlist. The push
job does not run .NET, MSBuild or repository code. It downloads the same-run artifact as untrusted
input, validates its paths, file types, JSON and size bounds again, explicitly stages only those lock
files, and uses a non-force push. A concurrent branch advance therefore fails safely.

Dependabot pull request workflows receive a read-only `GITHUB_TOKEN`; the workflow does not fall back
to that token and does not request or approve a permission upgrade. Hands-off updates require an
optional fine-grained personal access token stored as the repository Dependabot secret
`DEPENDABOT_LOCKFILE_TOKEN`. Restrict the token to `ilagutin/dotnet-genai-starter` and grant only
repository **Contents: Read and write**. Do not grant Actions, Workflows, Pull requests or
administration permissions. If the secret is absent, unavailable or cannot update the exact current
Dependabot branch head, the final step fails closed and a maintainer must apply the lock update
manually.

Run the manual remediation from the Dependabot branch after reviewing its package changes:

```powershell
dotnet restore GenAIPlatform.slnx --force-evaluate
git diff --name-status -- ':(glob)**/packages.lock.json'
dotnet restore GenAIPlatform.slnx --locked-mode
git add -- src/GenAIPlatform.Api/packages.lock.json src/GenAIPlatform.Application.Agentic/packages.lock.json src/GenAIPlatform.Application.Core/packages.lock.json src/GenAIPlatform.Application.Evaluations/packages.lock.json src/GenAIPlatform.Application.Generation/packages.lock.json src/GenAIPlatform.Application.Knowledge/packages.lock.json src/GenAIPlatform.Application.Usage/packages.lock.json src/GenAIPlatform.Domain/packages.lock.json src/GenAIPlatform.Evaluations/packages.lock.json src/GenAIPlatform.Infrastructure/packages.lock.json src/GenAIPlatform.Mcp/packages.lock.json src/GenAIPlatform.Worker/packages.lock.json tests/GenAIPlatform.IntegrationTests/packages.lock.json tests/GenAIPlatform.UnitTests/packages.lock.json
git diff --cached --check
git commit -m "chore(deps): update dependency lock files"
git push
```

Before committing, confirm that the unstaged and staged diffs contain only the intended existing lock
files and that no lock file is deleted, renamed, linked or invalid JSON. The automatic hosted path is
fully proven only after this workflow is present on the default branch and a real Dependabot pull
request exercises it. The repository operator must create the Dependabot secret separately; local
development and this repository configuration do not create or modify secrets.

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
