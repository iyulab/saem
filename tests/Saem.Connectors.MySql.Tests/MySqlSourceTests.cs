using Xunit;

namespace Saem.Connectors.MySql.Tests;

/// <summary>What the adapter says to a source before it is ever connected to one.</summary>
public class MySqlSourceTests
{
    [Theory]
    [InlineData("Server=erp;Database=erp;User ID=alice")]
    [InlineData("Server=erp;Database=erp;Password=secret")]
    public void A_connection_string_that_names_a_login_is_refused(string connectionString)
    {
        var refused = Assert.Throws<ArgumentException>(() => new MySqlSource(connectionString));
        Assert.Contains("not who logs in", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("saem/0123456789012345678901234567890123456789012345678901234567890")] // longer than 63
    [InlineData("saem/조회")]
    public void An_application_name_longer_or_wider_than_the_other_sources_take_is_refused(string applicationName)
    {
        var source = new MySqlSource("Server=erp;Database=erp");

        Assert.Throws<ArgumentException>(() => source.As("alice", _ => ValueTask.FromResult("x"), applicationName));
    }

    [Fact]
    public void A_scan_selects_every_row_in_key_order_as_text()
    {
        Assert.Equal(
            "SELECT CAST(`item_code` AS CHAR), `item_name` FROM `erp`.`items` ORDER BY 1",
            MySqlSourceSession.SelectAll("erp.items", "item_code", ["item_name"]));
    }

    [Fact]
    public void A_read_selects_the_key_as_text_and_the_asked_columns_by_key()
    {
        Assert.Equal(
            "SELECT CAST(`item_code` AS CHAR), `item_name`, `supplier_id` FROM `visible_items` WHERE CAST(`item_code` AS CHAR) = @key",
            MySqlSourceSession.SelectByKey("visible_items", "item_code", ["item_name", "supplier_id"]));
    }

    [Fact]
    public void A_find_selects_by_a_field_as_text_in_key_order_up_to_the_limit()
    {
        Assert.Equal(
            "SELECT CAST(`supplier_id` AS CHAR), `supplier_name` FROM `visible_suppliers` WHERE CAST(`supplier_name` AS CHAR) = @value ORDER BY 1 LIMIT 51",
            MySqlSourceSession.SelectWhere("visible_suppliers", "supplier_id", "supplier_name", ["supplier_name"], 51));
    }

    [Fact]
    public void Every_name_is_quoted_as_an_identifier_even_a_hostile_one()
    {
        Assert.Equal(
            "SELECT CAST(`id` AS CHAR), `name``; DROP TABLE x; --` FROM `erp`.`items` WHERE CAST(`id` AS CHAR) = @key",
            MySqlSourceSession.SelectByKey("erp.items", "id", ["name`; DROP TABLE x; --"]));
    }
}
