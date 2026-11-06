using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;

namespace MedulaPrecheck.Oracle;

/// <summary>
/// ODP.NET array binding: one INSERT statement, one round trip per chunk, regardless of the number of rows.
/// Columns are declared in the order of the positional binds (:1, :2, ...).
/// </summary>
internal sealed class ArrayInsert<T>(IReadOnlyList<T> rows)
{
    // Large enough to amortise round trips, small enough to keep bind buffers modest.
    private const int ChunkSize = 20_000;

    private readonly List<(OracleDbType Type, Func<T, object?> Value)> _columns = [];

    public ArrayInsert<T> Text(Func<T, string?> value) => Add(OracleDbType.Varchar2, value);

    public ArrayInsert<T> Int(Func<T, int?> value) => Add(OracleDbType.Int32, t => value(t));

    public ArrayInsert<T> Money(Func<T, decimal> value) => Add(OracleDbType.Decimal, t => value(t));

    public ArrayInsert<T> Date(Func<T, DateOnly?> value) =>
        Add(OracleDbType.Date, t => value(t) is { } d ? d.ToDateTime(TimeOnly.MinValue) : null);

    public async Task<int> ExecuteAsync(OracleConnection connection, string sql, CancellationToken cancellationToken = default)
    {
        var total = 0;
        for (var offset = 0; offset < rows.Count; offset += ChunkSize)
        {
            var count = Math.Min(ChunkSize, rows.Count - offset);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ArrayBindCount = count;

            foreach (var (type, value) in _columns)
            {
                var values = new object[count];
                for (var i = 0; i < count; i++)
                {
                    values[i] = value(rows[offset + i]) ?? DBNull.Value;
                }

                command.Parameters.Add(new OracleParameter { OracleDbType = type, Value = values });
            }

            total += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return total;
    }

    private ArrayInsert<T> Add(OracleDbType type, Func<T, object?> value)
    {
        _columns.Add((type, value));
        return this;
    }
}

internal static class ArrayInsert
{
    public static ArrayInsert<T> Of<T>(IReadOnlyList<T> rows) => new(rows);
}

/// <summary>Oracle identifiers that end up in SQL text are validated, never concatenated raw.</summary>
public static partial class OracleNames
{
    public static string Identifier(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var upper = name.ToUpperInvariant();
        return SafeIdentifier().IsMatch(upper)
            ? upper
            : throw new ArgumentException($"'{name}' is not a plain Oracle identifier.", nameof(name));
    }

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifier();
}
