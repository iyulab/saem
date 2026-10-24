using Xunit;

namespace Saem.Connectors.Postgres.Tests;

/// <summary>What the adapter says to a source before it is ever connected to one.</summary>
public class PostgresSourceTests
{
    [Theory]
    [InlineData("Host=mes;Database=postgres;Username=alice")]
    [InlineData("Host=mes;Database=postgres;Password=secret")]
    public void A_connection_string_that_names_a_login_is_refused(string connectionString)
    {
        var refused = Assert.Throws<ArgumentException>(() => new PostgresSource(connectionString));
        Assert.Contains("not who logs in", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("saem/0123456789012345678901234567890123456789012345678901234567890")] // longer than 63
    [InlineData("saem/조회")]
    [InlineData("saem/\n")]
    public void An_application_name_the_source_would_not_log_as_given_is_refused(string applicationName)
    {
        var source = new PostgresSource("Host=mes;Database=postgres");

        Assert.Throws<ArgumentException>(() => source.As("alice", _ => ValueTask.FromResult("x"), applicationName));
    }

    [Fact]
    public void A_read_selects_the_key_as_text_and_the_asked_columns_by_key()
    {
        var sql = PostgresSourceSession.SelectByKey("work_orders", "wo_no", ["item_code", "qty"]);

        Assert.Equal("""SELECT "wo_no"::text, "item_code", "qty" FROM "work_orders" WHERE "wo_no"::text = @key""", sql);
    }

    [Fact]
    public void A_find_selects_by_a_field_as_text_in_key_order_up_to_the_limit()
    {
        Assert.Equal(
            """SELECT "item_code"::text, "item_name" FROM "erp"."items" WHERE "item_name"::text = @value ORDER BY 1 LIMIT 51""",
            PostgresSourceSession.SelectWhere("erp.items", "item_code", "item_name", ["item_name"], 51));
        Assert.Equal(
            """SELECT "id"::text FROM "t" WHERE "x""; --"::text = @value ORDER BY 1 LIMIT 2""",
            PostgresSourceSession.SelectWhere("t", "id", "x\"; --", [], 2));
    }

    [Fact]
    public void A_scan_selects_every_row_in_key_order_as_text()
    {
        Assert.Equal(
            """SELECT "wo_no"::text, "item_code" FROM "mes"."work_orders" ORDER BY 1""",
            PostgresSourceSession.SelectAll("mes.work_orders", "wo_no", ["item_code"]));
        Assert.Equal("""SELECT "wo_no"::text FROM "work_orders" ORDER BY 1""", PostgresSourceSession.SelectAll("work_orders", "wo_no", []));
    }

    /// <summary>SD-08: a pass narrowed to changed rows asks the latest change in the source's spelling, and compares by it at the source.</summary>
    [Fact]
    public void A_pass_over_changed_rows_asks_the_latest_change_and_reads_from_it_in_key_order()
    {
        Assert.Equal("SELECT max(\"updated_at\")::text FROM \"mes\".\"work_orders\"", PostgresSourceSession.SelectLatest("mes.work_orders", "updated_at"));
        Assert.Equal(
            """SELECT "wo_no"::text, "item_code" FROM "mes"."work_orders" WHERE "updated_at" >= @since ORDER BY 1""",
            PostgresSourceSession.SelectChanged("mes.work_orders", "wo_no", ["item_code"], "updated_at"));
    }

    [Fact]
    public void Every_name_is_quoted_as_an_identifier_even_a_hostile_one()
    {
        var sql = PostgresSourceSession.SelectByKey("erp.items", "id", ["name\"; DROP TABLE x; --"]);

        Assert.Equal("""SELECT "id"::text, "name""; DROP TABLE x; --" FROM "erp"."items" WHERE "id"::text = @key""", sql);
    }

    [Fact]
    public void A_read_of_no_columns_reads_the_key_alone()
    {
        Assert.Equal("""SELECT "k"::text FROM "t" WHERE "k"::text = @key""", PostgresSourceSession.SelectByKey("t", "k", []));
    }
}
