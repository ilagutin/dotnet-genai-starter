CREATE TABLE IF NOT EXISTS genai.tool_audit_logs (
    id uuid PRIMARY KEY,
    conversation_id uuid NOT NULL,
    tenant_id text NOT NULL CHECK (length(btrim(tenant_id)) > 0),
    user_id text NOT NULL CHECK (length(btrim(user_id)) > 0),
    correlation_id text NOT NULL CHECK (length(btrim(correlation_id)) > 0),
    tool_call_id text NOT NULL CHECK (length(btrim(tool_call_id)) > 0),
    tool_name text NOT NULL CHECK (length(btrim(tool_name)) > 0),
    schema_version text NOT NULL CHECK (length(btrim(schema_version)) > 0),
    policy_version text NOT NULL CHECK (length(btrim(policy_version)) > 0),
    validation_status text NOT NULL CHECK (validation_status IN ('Valid', 'Invalid')),
    policy_decision text NOT NULL CHECK (policy_decision IN ('Allowed', 'RequiresApproval', 'Forbidden', 'UnknownTool')),
    approval_state text NOT NULL CHECK (approval_state IN ('NotRequired', 'Required', 'SimulatedApproved')),
    execution_status text NOT NULL CHECK (execution_status IN ('NotExecuted', 'ValidationFailed', 'Rejected', 'ApprovalRequired', 'Succeeded', 'Failed')),
    arguments jsonb NOT NULL,
    output jsonb NULL,
    error_code text NULL,
    error_message text NULL,
    created_at_utc timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_tool_audit_logs_conversation
    ON genai.tool_audit_logs (conversation_id, created_at_utc);

CREATE INDEX IF NOT EXISTS ix_tool_audit_logs_tenant_created
    ON genai.tool_audit_logs (tenant_id, created_at_utc DESC);
