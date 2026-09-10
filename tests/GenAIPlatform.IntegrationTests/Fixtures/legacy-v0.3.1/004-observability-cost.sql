CREATE TABLE IF NOT EXISTS genai.ai_model_pricing (
    id uuid PRIMARY KEY,
    provider text NOT NULL CHECK (length(btrim(provider)) > 0),
    model text NOT NULL CHECK (length(btrim(model)) > 0),
    currency text NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
    input_token_price_per_million numeric(18, 8) NOT NULL CHECK (input_token_price_per_million >= 0),
    output_token_price_per_million numeric(18, 8) NOT NULL CHECK (output_token_price_per_million >= 0),
    embedding_token_price_per_million numeric(18, 8) NULL CHECK (embedding_token_price_per_million IS NULL OR embedding_token_price_per_million >= 0),
    effective_from_utc timestamptz NOT NULL,
    effective_to_utc timestamptz NULL,
    created_at_utc timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK (effective_to_utc IS NULL OR effective_to_utc > effective_from_utc)
);

CREATE INDEX IF NOT EXISTS ix_ai_model_pricing_effective
    ON genai.ai_model_pricing (provider, model, effective_from_utc DESC, effective_to_utc);

CREATE TABLE IF NOT EXISTS genai.ai_request_logs (
    request_id uuid PRIMARY KEY,
    api_version text NOT NULL CHECK (length(btrim(api_version)) > 0),
    user_id text NULL,
    tenant_id text NULL,
    correlation_id text NOT NULL CHECK (length(btrim(correlation_id)) > 0),
    provider text NOT NULL CHECK (length(btrim(provider)) > 0),
    model text NOT NULL CHECK (length(btrim(model)) > 0),
    status text NOT NULL CHECK (status IN ('Succeeded', 'Failed')),
    error_code text NULL,
    latency_ms integer NOT NULL CHECK (latency_ms >= 0),
    input_tokens integer NULL CHECK (input_tokens IS NULL OR input_tokens >= 0),
    output_tokens integer NULL CHECK (output_tokens IS NULL OR output_tokens >= 0),
    total_tokens integer NULL CHECK (total_tokens IS NULL OR total_tokens >= 0),
    embedding_tokens integer NULL CHECK (embedding_tokens IS NULL OR embedding_tokens >= 0),
    estimated_cost numeric(18, 8) NULL CHECK (estimated_cost IS NULL OR estimated_cost >= 0),
    cost_currency text NULL CHECK (cost_currency IS NULL OR cost_currency ~ '^[A-Z]{3}$'),
    prompt_template_name text NULL,
    prompt_template_version text NULL,
    prompt_template_content_hash text NULL CHECK (prompt_template_content_hash IS NULL OR prompt_template_content_hash ~ '^[a-f0-9]{64}$'),
    retrieval_latency_ms integer NULL CHECK (retrieval_latency_ms IS NULL OR retrieval_latency_ms >= 0),
    retrieved_document_ids uuid[] NOT NULL DEFAULT ARRAY[]::uuid[],
    citation_references text[] NOT NULL DEFAULT ARRAY[]::text[],
    created_at_utc timestamptz NOT NULL,
    CHECK ((estimated_cost IS NULL AND cost_currency IS NULL) OR (estimated_cost IS NOT NULL AND cost_currency IS NOT NULL))
);

CREATE INDEX IF NOT EXISTS ix_ai_request_logs_created
    ON genai.ai_request_logs (created_at_utc);

CREATE INDEX IF NOT EXISTS ix_ai_request_logs_usage_filters
    ON genai.ai_request_logs (tenant_id, user_id, model, created_at_utc);

CREATE INDEX IF NOT EXISTS ix_ai_request_logs_correlation
    ON genai.ai_request_logs (correlation_id);

INSERT INTO genai.ai_model_pricing (
    id, provider, model, currency, input_token_price_per_million,
    output_token_price_per_million, embedding_token_price_per_million,
    effective_from_utc, effective_to_utc)
VALUES
    ('00000000-0000-0000-0000-000000000501', 'mock', 'mock-chat', 'USD', 0, 0, NULL, '2026-01-01T00:00:00Z', NULL),
    ('00000000-0000-0000-0000-000000000502', 'mock', 'mock-cheap', 'USD', 0, 0, NULL, '2026-01-01T00:00:00Z', NULL),
    ('00000000-0000-0000-0000-000000000503', 'mock', 'mock-strong', 'USD', 0, 0, NULL, '2026-01-01T00:00:00Z', NULL),
    ('00000000-0000-0000-0000-000000000504', 'mock', 'mock-evaluation', 'USD', 0, 0, NULL, '2026-01-01T00:00:00Z', NULL),
    ('00000000-0000-0000-0000-000000000505', 'mock', 'mock-chat-evaluation', 'USD', 0, 0, NULL, '2026-01-01T00:00:00Z', NULL)
ON CONFLICT (id) DO NOTHING;
