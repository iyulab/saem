using System.Text;
using System.Text.Json;

namespace Saem.Authoring.Shadow;

/// <summary>
/// The file the shadow rows are kept in: JSON Lines, one row to a line, only ever appended to. Nothing that authors, reviews or
/// settles reads it — deleting it takes the shadow away and leaves everything else as it was.
/// </summary>
public static class ShadowLedger
{
    /// <summary>The ledger's row layout version.</summary>
    public const int Layout = 1;

    /// <summary>Appends <paramref name="rows"/> to the ledger at <paramref name="path"/>, creating it when it is not there.</summary>
    public static async Task AppendAsync(string path, IEnumerable<ShadowRow> rows, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(rows);

        var text = new StringBuilder();
        foreach (var row in rows)
        {
            text.Append(Write(row)).Append('\n');
        }

        if (text.Length > 0)
        {
            await File.AppendAllTextAsync(path, text.ToString(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads every row of the ledger at <paramref name="path"/>; a ledger that is not there holds none.</summary>
    /// <exception cref="JsonException">A line is not a row of this layout.</exception>
    public static async Task<IReadOnlyList<ShadowRow>> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return [];
        }

        var rows = new List<ShadowRow>();
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (line.Length > 0)
            {
                rows.Add(Parse(line));
            }
        }

        return rows;
    }

    /// <summary>One row as its line, without the line end.</summary>
    public static string Write(ShadowRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Layout);
            writer.WriteString("pass", row.Pass);
            writer.WriteStartObject("item");
            writer.WriteString("id", row.ItemId);
            writer.WriteString("kind", row.Kind);
            writer.WriteString("name", row.Name);
            writer.WriteEndObject();
            writer.WriteStartObject("judge");
            writer.WriteString("layer", row.Layer);
            writer.WriteNumber("version", row.LayerVersion);
            writer.WriteEndObject();
            writer.WriteString("verdict", row.Verdict);
            if (row.Field is not null)
            {
                writer.WriteString("field", row.Field);
            }

            if (row.FieldCarried is { } carried)
            {
                writer.WriteBoolean("fieldCarried", carried);
            }

            writer.WriteStartArray("cites");
            foreach (var cite in row.Cites)
            {
                writer.WriteStartObject();
                writer.WriteString("record", cite.Record);
                if (cite.Subject is not null)
                {
                    writer.WriteString("subject", cite.Subject);
                }

                writer.WriteString("standing", Name(cite.Standing));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (row.Note is not null)
            {
                writer.WriteString("note", row.Note);
            }

            writer.WriteString("at", row.At.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Reads one line written by <see cref="Write"/>.</summary>
    /// <exception cref="JsonException">The line is not a row of this layout.</exception>
    public static ShadowRow Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        try
        {
            if (root.GetProperty("v").GetInt32() != Layout)
            {
                throw new JsonException($"A ledger row of layout {root.GetProperty("v").GetInt32()} is not one this reader knows.");
            }

            var item = root.GetProperty("item");
            var judge = root.GetProperty("judge");
            return new ShadowRow(
                root.GetProperty("pass").GetString()!,
                item.GetProperty("id").GetString()!,
                item.GetProperty("kind").GetString()!,
                item.GetProperty("name").GetString()!,
                judge.GetProperty("layer").GetString()!,
                judge.GetProperty("version").GetInt32(),
                root.GetProperty("verdict").GetString()!,
                root.TryGetProperty("field", out var field) ? field.GetString() : null,
                root.TryGetProperty("fieldCarried", out var carried) ? carried.GetBoolean() : null,
                [.. root.GetProperty("cites").EnumerateArray().Select(c => new ShadowCite(
                    c.GetProperty("record").GetString()!,
                    c.TryGetProperty("subject", out var subject) ? subject.GetString() : null,
                    Standing(c.GetProperty("standing").GetString()!)))],
                root.TryGetProperty("note", out var note) ? note.GetString() : null,
                DateTimeOffset.Parse(root.GetProperty("at").GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal));
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new JsonException("A ledger row is an object with pass, item, judge, verdict, cites and at.", e);
        }
    }

    private static string Name(CitedStanding standing) => standing switch
    {
        CitedStanding.Standing => "standing",
        CitedStanding.Superseded => "superseded",
        CitedStanding.Retired => "retired",
        CitedStanding.Missing => "missing",
        CitedStanding.NotCheckable => "notCheckable",
        CitedStanding.Unreadable => "unreadable",
        _ => throw new ArgumentOutOfRangeException(nameof(standing)),
    };

    private static CitedStanding Standing(string name) => name switch
    {
        "standing" => CitedStanding.Standing,
        "superseded" => CitedStanding.Superseded,
        "retired" => CitedStanding.Retired,
        "missing" => CitedStanding.Missing,
        "notCheckable" => CitedStanding.NotCheckable,
        "unreadable" => CitedStanding.Unreadable,
        _ => throw new JsonException($"'{name}' is not a standing a ledger row names."),
    };
}
