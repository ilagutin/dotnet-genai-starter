CREATE SCHEMA IF NOT EXISTS genai;

CREATE TABLE IF NOT EXISTS genai.documents (
    id uuid PRIMARY KEY,
    tenant_id text NOT NULL,
    owner_user_id text NOT NULL,
    file_name text NOT NULL,
    title text NOT NULL,
    content_type text NULL,
    source_extension text NOT NULL,
    storage_path text NOT NULL,
    size_bytes bigint NOT NULL,
    content_hash text NOT NULL CHECK (content_hash ~ '^[a-f0-9]{64}$'),
    version integer NOT NULL CHECK (version > 0),
    access_level text NOT NULL CHECK (access_level IN ('Private', 'TenantPublic')),
    indexing_status text NOT NULL CHECK (indexing_status IN ('PendingIndexing', 'Indexed', 'Failed')),
    created_at_utc timestamptz NOT NULL,
    updated_at_utc timestamptz NOT NULL,
    failure_reason text NULL,
    CHECK (size_bytes > 0)
);

CREATE INDEX IF NOT EXISTS ix_documents_tenant_owner
    ON genai.documents (tenant_id, owner_user_id);

CREATE TABLE IF NOT EXISTS genai.indexing_jobs (
    id uuid PRIMARY KEY,
    document_id uuid NOT NULL REFERENCES genai.documents(id) ON DELETE CASCADE,
    status text NOT NULL CHECK (status IN ('Pending', 'Processing', 'Completed', 'Failed')),
    attempts integer NOT NULL CHECK (attempts >= 0),
    max_attempts integer NOT NULL CHECK (max_attempts > 0),
    created_at_utc timestamptz NOT NULL,
    updated_at_utc timestamptz NOT NULL,
    available_at_utc timestamptz NOT NULL,
    started_at_utc timestamptz NULL,
    completed_at_utc timestamptz NULL,
    worker_id text NULL,
    failure_reason text NULL,
    CHECK (attempts <= max_attempts)
);

CREATE INDEX IF NOT EXISTS ix_indexing_jobs_pending
    ON genai.indexing_jobs (status, available_at_utc, created_at_utc);

CREATE INDEX IF NOT EXISTS ix_indexing_jobs_document
    ON genai.indexing_jobs (document_id, created_at_utc DESC);

CREATE TABLE IF NOT EXISTS genai.document_chunks (
    id uuid PRIMARY KEY,
    document_id uuid NOT NULL REFERENCES genai.documents(id) ON DELETE CASCADE,
    document_version integer NOT NULL CHECK (document_version > 0),
    position integer NOT NULL CHECK (position >= 0),
    text text NOT NULL,
    text_hash text NOT NULL,
    approximate_token_count integer NOT NULL CHECK (approximate_token_count >= 0),
    chunking_profile text NOT NULL CHECK (length(btrim(chunking_profile)) > 0),
    chunking_profile_version text NOT NULL CHECK (length(btrim(chunking_profile_version)) > 0),
    embedding_model text NOT NULL CHECK (length(btrim(embedding_model)) > 0),
    embedding_provider text NOT NULL CHECK (length(btrim(embedding_provider)) > 0),
    embedding_dimensions integer NOT NULL CHECK (embedding_dimensions > 0),
    embedding_input_tokens integer NULL,
    embedding_values real[] NOT NULL,
    created_at_utc timestamptz NOT NULL,
    UNIQUE (document_id, document_version, position),
    CHECK (text_hash ~ '^[a-f0-9]{64}$'),
    CHECK (embedding_input_tokens IS NULL OR embedding_input_tokens >= 0),
    CHECK (cardinality(embedding_values) = embedding_dimensions)
);

CREATE INDEX IF NOT EXISTS ix_document_chunks_document
    ON genai.document_chunks (document_id, document_version, position);
