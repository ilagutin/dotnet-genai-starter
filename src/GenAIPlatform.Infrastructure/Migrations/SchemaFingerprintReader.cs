using Npgsql;

namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// Reads a canonical, ordered fingerprint of the application schema: tables, columns with their
/// formatted types and nullability, constraint names by kind, and index names. Journal tables are
/// excluded so the fingerprint describes only what the migrations own. Expression text and column
/// defaults are deliberately left out: they render differently across PostgreSQL versions and
/// would turn a supported upgrade into a false mismatch.
/// </summary>
internal sealed class SchemaFingerprintReader
{
    private const string FingerprintSql = """
        WITH excluded_tables AS (
            SELECT unnest(ARRAY['schema_migrations', 'schema_migration_attempts']) AS relname
        ),
        owned_tables AS (
            SELECT class.oid, class.relname
            FROM pg_class class
            INNER JOIN pg_namespace namespace
                ON namespace.oid = class.relnamespace
            WHERE namespace.nspname = 'genai'
              AND class.relkind = 'r'
              AND class.relname NOT IN (SELECT relname FROM excluded_tables)
        ),
        fingerprint_lines AS (
            SELECT format('table:%s', owned.relname) AS line
            FROM owned_tables owned
            UNION ALL
            SELECT format(
                'column:%s.%s:%s:%s',
                owned.relname,
                attribute.attname,
                format_type(attribute.atttypid, attribute.atttypmod),
                CASE WHEN attribute.attnotnull THEN 'not-null' ELSE 'nullable' END)
            FROM owned_tables owned
            INNER JOIN pg_attribute attribute
                ON attribute.attrelid = owned.oid
            WHERE attribute.attnum > 0
              AND attribute.attisdropped = false
            UNION ALL
            SELECT format('constraint:%s:%s:%s', owned.relname, constraint_row.contype, constraint_row.conname)
            FROM owned_tables owned
            INNER JOIN pg_constraint constraint_row
                ON constraint_row.conrelid = owned.oid
            UNION ALL
            SELECT format('index:%s:%s', owned.relname, index_row.indexname)
            FROM owned_tables owned
            INNER JOIN pg_indexes index_row
                ON index_row.schemaname = 'genai'
               AND index_row.tablename = owned.relname
        )
        SELECT line
        FROM fingerprint_lines
        ORDER BY line COLLATE "C";
        """;

    public async Task<IReadOnlyList<string>> ReadAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(FingerprintSql, connection);
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(reader.GetString(0));
        }

        return lines;
    }
}
