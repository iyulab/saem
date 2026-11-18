using System.Text.Json.Nodes;
using Eyu.Core.Primitives;
using Xunit;

namespace Saem.Connectors.Formbase.Tests;

/// <summary>
/// Cited documents read again from the form type's stream: still the current version of the record, corrected since,
/// retired, or not there — judged against the stream, not against the sample the citation was drawn from.
/// </summary>
public class FormbaseConnectorCitationTests
{
    private static readonly SubjectRef WorkOrders = SubjectRef.Create("workorders");

    [Fact]
    public async Task A_document_nothing_has_corrected_stands_and_brings_its_fields()
    {
        var formbase = new FormbaseConnectorSampleDirectionTests.InMemoryFormbase();
        var only = formbase.Append("workorders", new JsonObject { ["wo"] = "WO-1", ["tonnage"] = 12 }, key: "wo-1");

        var read = await new FormbaseConnector(formbase.Client).ReadCitedAsync(WorkOrders, [only.ToString()], TestContext.Current.CancellationToken);

        var reading = Assert.Single(read).Value;
        Assert.Equal(DocumentStanding.Standing, reading.Standing);
        Assert.Equal(("WO-1", "12"), (reading.Fields["wo"], reading.Fields["tonnage"]));
    }

    [Fact]
    public async Task A_document_a_later_one_of_its_record_has_corrected_is_superseded_and_the_correction_stands()
    {
        var formbase = new FormbaseConnectorSampleDirectionTests.InMemoryFormbase();
        var first = formbase.Append("workorders", new JsonObject { ["wo"] = "WO-1" }, key: "wo-1");
        var corrected = formbase.Append("workorders", new JsonObject { ["wo"] = "WO-1", ["crane"] = "C-1" }, key: "wo-1");

        var read = await new FormbaseConnector(formbase.Client).ReadCitedAsync(WorkOrders, [first.ToString(), corrected.ToString()], TestContext.Current.CancellationToken);

        Assert.Equal(DocumentStanding.Superseded, read[first.ToString()].Standing);
        Assert.Equal(DocumentStanding.Standing, read[corrected.ToString()].Standing);
    }

    [Fact]
    public async Task A_record_that_was_retired_after_the_citation_is_retired_whichever_of_its_documents_was_cited()
    {
        var formbase = new FormbaseConnectorSampleDirectionTests.InMemoryFormbase();
        var cited = formbase.Append("workorders", new JsonObject { ["wo"] = "WO-1" }, key: "wo-1");
        formbase.Append("workorders", null, key: "wo-1", retired: true);

        var read = await new FormbaseConnector(formbase.Client).ReadCitedAsync(WorkOrders, [cited.ToString()], TestContext.Current.CancellationToken);

        Assert.Equal(DocumentStanding.Retired, Assert.Single(read).Value.Standing);
    }

    [Fact]
    public async Task An_id_the_stream_does_not_hold_is_missing_and_a_document_without_a_record_key_stands()
    {
        var formbase = new FormbaseConnectorSampleDirectionTests.InMemoryFormbase();
        var keyless = formbase.Append("workorders", new JsonObject { ["wo"] = "WO-9" });
        formbase.Append("otherform", new JsonObject { ["x"] = 1 });
        var elsewhere = Guid.NewGuid().ToString();

        var read = await new FormbaseConnector(formbase.Client).ReadCitedAsync(WorkOrders, [keyless.ToString(), elsewhere], TestContext.Current.CancellationToken);

        Assert.Equal(DocumentStanding.Standing, read[keyless.ToString()].Standing);
        Assert.Equal(DocumentStanding.Missing, read[elsewhere].Standing);
        Assert.Empty(read[elsewhere].Fields);
    }

    [Fact]
    public async Task A_stream_longer_than_a_page_is_read_to_its_end()
    {
        var formbase = new FormbaseConnectorSampleDirectionTests.InMemoryFormbase();
        var cited = formbase.Append("workorders", new JsonObject { ["wo"] = "WO-0" }, key: "k");
        for (var i = 1; i <= 2400; i++)
        {
            formbase.Append("workorders", new JsonObject { ["wo"] = $"WO-{i}" }, key: i == 2400 ? "k" : $"k{i}");
        }

        var read = await new FormbaseConnector(formbase.Client).ReadCitedAsync(WorkOrders, [cited.ToString()], TestContext.Current.CancellationToken);

        Assert.Equal(DocumentStanding.Superseded, Assert.Single(read).Value.Standing);
    }

    [Fact]
    public async Task A_repeated_sections_rows_cannot_be_looked_up_again_by_id_and_nothing_is_read_for_them()
    {
        var formbase = new FormbaseConnectorSampleDirectionTests.InMemoryFormbase();
        formbase.Declare("orderlines", """
            {
              "formType": "orderlines", "tableName": "orderlines", "declarationVersion": 1,
              "fields": [ { "name": "orderNo", "type": "text", "nullable": true } ],
              "relations": [],
              "sectionOf": { "formType": "orders", "relation": "lines", "sectionField": "lines", "keyField": "orderNo" }
            }
            """);

        var read = await new FormbaseConnector(formbase.Client).ReadCitedAsync(SubjectRef.Create("orderlines"), ["d/0"], TestContext.Current.CancellationToken);

        Assert.Equal(DocumentStanding.NotCheckable, Assert.Single(read).Value.Standing);
        Assert.Empty(formbase.StreamReads);
    }

    [Fact]
    public async Task Nothing_asked_about_is_nothing_read()
    {
        var formbase = new FormbaseConnectorSampleDirectionTests.InMemoryFormbase();

        var read = await new FormbaseConnector(formbase.Client).ReadCitedAsync(WorkOrders, [], TestContext.Current.CancellationToken);

        Assert.Empty(read);
        Assert.Empty(formbase.StreamReads);
    }
}
