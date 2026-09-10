CREATE SCHEMA IF NOT EXISTS genai;

CREATE TABLE IF NOT EXISTS genai.document_storage_cleanup_requests (
    document_id uuid PRIMARY KEY,
    storage_path text NOT NULL CHECK (length(btrim(storage_path)) > 0),
    staged_storage_path text NULL CHECK (
        staged_storage_path IS NULL OR length(btrim(staged_storage_path)) > 0
    ),
    content_hash text NOT NULL CHECK (content_hash ~ '^[a-f0-9]{64}$'),
    size_bytes bigint NOT NULL CHECK (size_bytes > 0),
    metadata_absence_proof text NOT NULL CHECK (length(btrim(metadata_absence_proof)) > 0),
    metadata_absence_verified_at_utc timestamptz NOT NULL,
    delete_failure_reason text NOT NULL CHECK (length(btrim(delete_failure_reason)) > 0),
    status text NOT NULL CHECK (status IN ('Pending', 'Processing', 'Completed', 'Failed', 'Deferred')),
    attempts integer NOT NULL CHECK (attempts >= 0),
    available_at_utc timestamptz NOT NULL,
    created_at_utc timestamptz NOT NULL,
    updated_at_utc timestamptz NOT NULL,
    worker_id text NULL,
    failure_reason text NULL,
    CHECK (updated_at_utc >= created_at_utc)
);

CREATE INDEX IF NOT EXISTS ix_document_storage_cleanup_requests_available
    ON genai.document_storage_cleanup_requests (status, available_at_utc, created_at_utc)
    WHERE status IN ('Pending', 'Processing', 'Deferred');
