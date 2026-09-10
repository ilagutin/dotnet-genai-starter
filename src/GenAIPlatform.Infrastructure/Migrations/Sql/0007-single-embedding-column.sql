-- Stores one embedding representation per chunk. The relational real[] copy is removed only
-- after every surviving row has been proven to carry an equivalent pgvector value, so retrieval
-- and inspection read the same column and the two can no longer drift apart.
--
-- The whole script is one PL/pgSQL block whose statements run through EXECUTE. Column references
-- inside a plain SQL statement are resolved when that statement is analyzed, so a replay after a
-- lost commit acknowledgement would fail to analyze once embedding_values is gone; deferring the
-- text to EXECUTE keeps the replay a no-op instead.
--
-- A precondition failure is raised with SQLSTATE GN001. The runner surfaces the message text of
-- that SQLSTATE to the operator, so every message here must stay sanitized: counts only, never
-- chunk text, document ids or vectors.
DO $migration$
DECLARE
    legacy_column_present boolean;
    unvectorized_chunk_count bigint;
    unvectorized_document_count bigint;
    inconsistent_chunk_count bigint;
    inconsistent_document_count bigint;
    dependent_constraint_name text;
BEGIN
    SELECT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'genai'
          AND table_name = 'document_chunks'
          AND column_name = 'embedding_values')
    INTO legacy_column_present;

    IF legacy_column_present THEN
        -- Re-runs the 0002 backfill with an identical predicate, so a database that was adopted
        -- from v0.3.1 (where 0002 was journalled but never executed here) still gets every
        -- finite, non-zero array of the declared cardinality turned into a vector.
        EXECUTE $backfill$
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
            WHERE chunk.id = backfillable_chunks.id
        $backfill$;

        -- Fails closed rather than dropping the only readable copy of an embedding that never
        -- became a vector. 0002 deliberately left those rows behind; this migration cannot.
        EXECUTE $unvectorized$
            SELECT count(*), count(DISTINCT chunk.document_id)
            FROM genai.document_chunks chunk
            WHERE chunk.embedding_vector IS NULL
        $unvectorized$ INTO unvectorized_chunk_count, unvectorized_document_count;

        IF unvectorized_chunk_count > 0 THEN
            RAISE EXCEPTION USING
                ERRCODE = 'GN001',
                MESSAGE = format(
                    'migration 0007 stopped because %s document chunk(s) across %s document(s) '
                    || 'still have no embedding_vector, so removing the relational '
                    || 'embedding_values copy would discard their only embedding. Re-index those '
                    || 'documents to regenerate valid embeddings, or delete those chunks '
                    || 'explicitly, then run the migration again. The diagnostic query that '
                    || 'lists the affected document ids is in docs/quickstart.md.',
                    unvectorized_chunk_count,
                    unvectorized_document_count);
        END IF;

        -- Proves the two representations agree before one of them is dropped. A historical row
        -- whose vector was written independently of its array is not silently resolved in favor
        -- of either copy.
        EXECUTE $inconsistent$
            SELECT count(*), count(DISTINCT chunk.document_id)
            FROM genai.document_chunks chunk
            WHERE vector_dims(chunk.embedding_vector) <> chunk.embedding_dimensions
               OR chunk.embedding_vector::real[] IS DISTINCT FROM chunk.embedding_values
        $inconsistent$ INTO inconsistent_chunk_count, inconsistent_document_count;

        IF inconsistent_chunk_count > 0 THEN
            RAISE EXCEPTION USING
                ERRCODE = 'GN001',
                MESSAGE = format(
                    'migration 0007 stopped because %s document chunk(s) across %s document(s) '
                    || 'are inconsistent historical rows whose embedding_vector does not match '
                    || 'embedding_values. Re-index those documents to regenerate valid '
                    || 'embeddings, or delete those chunks explicitly, then run the migration '
                    || 'again. The diagnostic query that lists the affected document ids is in '
                    || 'docs/quickstart.md.',
                    inconsistent_chunk_count,
                    inconsistent_document_count);
        END IF;

        -- The cardinality CHECK from 0001 is an unnamed table constraint, so it is found by the
        -- column it depends on instead of by a generated name.
        FOR dependent_constraint_name IN
            SELECT constraint_row.conname
            FROM pg_constraint constraint_row
            WHERE constraint_row.conrelid = 'genai.document_chunks'::regclass
              AND constraint_row.contype = 'c'
              AND (
                  SELECT attribute.attnum
                  FROM pg_attribute attribute
                  WHERE attribute.attrelid = 'genai.document_chunks'::regclass
                    AND attribute.attname = 'embedding_values'
              ) = ANY (constraint_row.conkey)
            ORDER BY constraint_row.conname
        LOOP
            EXECUTE format(
                'ALTER TABLE genai.document_chunks DROP CONSTRAINT %I',
                dependent_constraint_name);
        END LOOP;

        EXECUTE 'ALTER TABLE genai.document_chunks DROP COLUMN IF EXISTS embedding_values';
    END IF;

    -- Idempotent: PostgreSQL accepts SET NOT NULL on a column that already has it. The dimension
    -- CHECK, the dimension index and both partial HNSW indexes are left exactly as 0002 made them.
    EXECUTE 'ALTER TABLE genai.document_chunks ALTER COLUMN embedding_vector SET NOT NULL';
END $migration$;
