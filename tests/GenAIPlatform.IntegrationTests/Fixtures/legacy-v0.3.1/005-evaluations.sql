CREATE TABLE IF NOT EXISTS genai.evaluation_runs (
    run_id uuid PRIMARY KEY,
    tenant_id text NOT NULL CHECK (length(btrim(tenant_id)) > 0),
    user_id text NOT NULL CHECK (length(btrim(user_id)) > 0),
    dataset_version text NOT NULL CHECK (length(btrim(dataset_version)) > 0),
    runner_version text NOT NULL CHECK (length(btrim(runner_version)) > 0),
    prompt_version text NOT NULL CHECK (length(btrim(prompt_version)) > 0),
    model text NOT NULL CHECK (length(btrim(model)) > 0),
    model_settings jsonb NOT NULL,
    retrieval_configuration jsonb NOT NULL,
    status text NOT NULL CHECK (status IN ('Running', 'Succeeded', 'Failed', 'Canceled')),
    started_at_utc timestamptz NOT NULL,
    completed_at_utc timestamptz NULL
);

ALTER TABLE genai.evaluation_runs
    ADD COLUMN IF NOT EXISTS tenant_id text,
    ADD COLUMN IF NOT EXISTS user_id text;

UPDATE genai.evaluation_runs
SET tenant_id = COALESCE(NULLIF(btrim(tenant_id), ''), 'legacy-tenant'),
    user_id = COALESCE(NULLIF(btrim(user_id), ''), 'legacy-user')
WHERE tenant_id IS NULL
   OR user_id IS NULL
   OR length(btrim(tenant_id)) = 0
   OR length(btrim(user_id)) = 0;

ALTER TABLE genai.evaluation_runs
    ALTER COLUMN tenant_id SET NOT NULL,
    ALTER COLUMN user_id SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_evaluation_runs_tenant_id_not_blank'
          AND conrelid = 'genai.evaluation_runs'::regclass
    ) THEN
        ALTER TABLE genai.evaluation_runs
            ADD CONSTRAINT ck_evaluation_runs_tenant_id_not_blank
            CHECK (length(btrim(tenant_id)) > 0);
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_evaluation_runs_user_id_not_blank'
          AND conrelid = 'genai.evaluation_runs'::regclass
    ) THEN
        ALTER TABLE genai.evaluation_runs
            ADD CONSTRAINT ck_evaluation_runs_user_id_not_blank
            CHECK (length(btrim(user_id)) > 0);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS genai.evaluation_case_results (
    run_id uuid NOT NULL REFERENCES genai.evaluation_runs(run_id) ON DELETE CASCADE,
    case_id text NOT NULL CHECK (length(btrim(case_id)) > 0),
    name text NOT NULL CHECK (length(btrim(name)) > 0),
    status text NOT NULL CHECK (status IN ('Passed', 'Failed')),
    answer text NULL,
    retrieved_count integer NOT NULL CHECK (retrieved_count >= 0),
    retrieval_hit boolean NOT NULL,
    latency_ms integer NOT NULL CHECK (latency_ms >= 0),
    estimated_cost numeric(18, 8) NOT NULL CHECK (estimated_cost >= 0),
    cost_currency text NOT NULL CHECK (cost_currency ~ '^[A-Z]{3}$'),
    error_code text NULL,
    error_message text NULL,
    checks jsonb NOT NULL,
    PRIMARY KEY (run_id, case_id)
);

CREATE INDEX IF NOT EXISTS ix_evaluation_runs_started
    ON genai.evaluation_runs (started_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_evaluation_runs_tenant_run
    ON genai.evaluation_runs (tenant_id, run_id);
