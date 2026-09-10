ALTER TABLE genai.document_chunks
    ADD COLUMN IF NOT EXISTS embedding_vector vector NULL;

WITH backfillable_chunks AS (
    SELECT chunk.id
    FROM genai.document_chunks chunk
    WHERE chunk.embedding_vector IS NULL
      AND cardinality(chunk.embedding_values) = chunk.embedding_dimensions
      AND EXISTS (
          SELECT 1
          FROM unnest(chunk.embedding_values) AS embedding_value(value)
          WHERE embedding_value.value <> 0::real
      )
      AND NOT EXISTS (
          SELECT 1
          FROM unnest(chunk.embedding_values) AS embedding_value(value)
          WHERE embedding_value.value IS NULL
             OR embedding_value.value::text IN ('NaN', 'Infinity', '-Infinity')
      )
)
UPDATE genai.document_chunks chunk
SET embedding_vector = chunk.embedding_values::vector
FROM backfillable_chunks
WHERE chunk.id = backfillable_chunks.id;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_document_chunks_embedding_vector_dimensions'
          AND conrelid = 'genai.document_chunks'::regclass
    ) THEN
        ALTER TABLE genai.document_chunks
            ADD CONSTRAINT ck_document_chunks_embedding_vector_dimensions
            CHECK (
                embedding_vector IS NULL
                OR vector_dims(embedding_vector) = embedding_dimensions
            );
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_document_chunks_embedding_dimensions
    ON genai.document_chunks (embedding_dimensions)
    WHERE embedding_vector IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_document_chunks_embedding_vector_16_hnsw
    ON genai.document_chunks
    USING hnsw ((embedding_vector::vector(16)) vector_cosine_ops)
    WHERE embedding_dimensions = 16
      AND embedding_vector IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_document_chunks_embedding_vector_1536_hnsw
    ON genai.document_chunks
    USING hnsw ((embedding_vector::vector(1536)) vector_cosine_ops)
    WHERE embedding_dimensions = 1536
      AND embedding_vector IS NOT NULL;
