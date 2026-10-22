using System.Net.Http.Headers;
using System.Net.Http.Json;
using Eyu.Core.Inference.Http;
using Eyu.Core.Judgment;
using Saem.Connectors.Formbase;
using Saem.Ontology;
using Xunit;

namespace Saem.Authoring.Tests.Live;

/// <summary>
/// One authoring pass end to end: three form types of documents in a running Formbase — work orders
/// naming items, items naming suppliers — read by the Formbase connector, proposed on by Eyu against a
/// real model, and drafted into declarations. The draft is held to what a person would have declared:
/// each type on its own table, and the two relations through the fields that carry them. A model's
/// answer varies, so the draft, its notes and what Eyu left out are written out for reading as well.
/// </summary>
public sealed class AuthoringLiveTests
{
    private const int RecordsPerSubject = 20;

    [Fact]
    public async Task A_pass_over_form_documents_drafts_the_types_and_the_relations_a_person_would_declare()
    {
        var ct = TestContext.Current.CancellationToken;
        using var formbase = Formbase();
        var subjects = await FileDocumentsAsync(formbase, ct);
        using var model = ModelHttp();

        var pass = await Authoring(formbase, model).DraftAsync(subjects.Sources, [], RecordsPerSubject, ct);
        await ReportAsync("from-nothing", pass, ct);

        var byTable = pass.Draft.Added.OfType<EntityTypeDeclaration>().ToDictionary(t => t.Table, StringComparer.Ordinal);
        Assert.True(byTable.ContainsKey("work_orders") && byTable.ContainsKey("items") && byTable.ContainsKey("suppliers"),
            $"Expected a type on each of the three tables; drafted on: {string.Join(", ", byTable.Keys)}.");
        AssertRelation(pass.Draft, byTable["work_orders"].Name, "item_code", byTable["items"].Name);
        AssertRelation(pass.Draft, byTable["items"].Name, "supplier_id", byTable["suppliers"].Name);
    }

    /// <summary>
    /// The input the grounding chain actually has: form types Formbase holds declarations for — the
    /// fields, and which field of a form names a record of another. Eyu then reads those fields as
    /// references, not as evidence of a record's own identity.
    /// </summary>
    [Fact]
    public async Task A_pass_over_declared_form_types_drafts_the_same_types_and_relations()
    {
        var ct = TestContext.Current.CancellationToken;
        using var formbase = Formbase();
        var subjects = await FileDocumentsAsync(formbase, ct, declare: true);
        using var model = ModelHttp();

        var pass = await Authoring(formbase, model).DraftAsync(subjects.Sources, [], RecordsPerSubject, ct);
        await ReportAsync("declared-forms", pass, ct);

        var byTable = pass.Draft.Added.OfType<EntityTypeDeclaration>().ToDictionary(t => t.Table, StringComparer.Ordinal);
        Assert.True(byTable.ContainsKey("work_orders") && byTable.ContainsKey("items") && byTable.ContainsKey("suppliers"),
            $"Expected a type on each of the three tables; drafted on: {string.Join(", ", byTable.Keys)}.");
        AssertRelation(pass.Draft, byTable["work_orders"].Name, "item_code", byTable["items"].Name);
        AssertRelation(pass.Draft, byTable["items"].Name, "supplier_id", byTable["suppliers"].Name);
    }

    [Fact]
    public async Task With_the_types_declared_a_pass_adds_only_the_relations_under_the_declared_names()
    {
        var ct = TestContext.Current.CancellationToken;
        using var formbase = Formbase();
        var subjects = await FileDocumentsAsync(formbase, ct);
        using var model = ModelHttp();
        IReadOnlyList<Declaration> declared =
        [
            new EntityTypeDeclaration("WorkOrder", "formbase", "work_orders", "wo_no",
                new Dictionary<string, string> { ["item"] = "item_code", ["quantity"] = "qty", ["note"] = "note" }),
            new EntityTypeDeclaration("Item", "formbase", "items", "item_code",
                new Dictionary<string, string> { ["name"] = "item_name", ["supplier"] = "supplier_id" }),
            new EntityTypeDeclaration("Supplier", "formbase", "suppliers", "supplier_id",
                new Dictionary<string, string> { ["name"] = "supplier_name" }),
        ];

        var pass = await Authoring(formbase, model).DraftAsync(subjects.Sources, declared, RecordsPerSubject, ct);
        await ReportAsync("over-declared-types", pass, ct);

        Assert.Empty(pass.Draft.Added.OfType<EntityTypeDeclaration>());
        AssertRelation(pass.Draft, "WorkOrder", "item", "Item");
        AssertRelation(pass.Draft, "Item", "supplier", "Supplier");
    }

    private static void AssertRelation(DeclarationDraft draft, string from, string via, string to) =>
        Assert.True(draft.Added.OfType<RelationDeclaration>().Any(r => r.From == from && r.Via == via && r.To == to),
            $"Expected a relation {from}.{via} → {to}; drafted: " +
            string.Join("; ", draft.Added.OfType<RelationDeclaration>().Select(r => $"{r.Name} = {r.From}.{r.Via} → {r.To}")) +
            "; notes: " + string.Join("; ", draft.Notes.Select(n => $"{n.About}: {n.Reason}")));

    private static DeclarationAuthoring Authoring(HttpClient formbase, HttpClient model)
    {
        var stream = bool.TryParse(Environment.GetEnvironmentVariable("EYU_LLM_STREAM"), out var streamed) && streamed;
        var proposer = new SinglePassOntologyProposer(new HttpModelClient(model, Require("EYU_LLM_MODEL"), stream: stream));
        var connector = new FormbaseConnector(formbase);
        return new DeclarationAuthoring(proposer, connector, connector);
    }

    /// <summary>
    /// Files the documents under three fresh form types and returns which subject stands on which table.
    /// Nothing is declared in Formbase: the fields reach Eyu as records only.
    /// </summary>
    private static async Task<(Dictionary<string, SubjectSource> Sources, string Suffix)> FileDocumentsAsync(HttpClient http, CancellationToken ct, bool declare = false)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var workOrders = $"workorders-{suffix}";
        var items = $"items-{suffix}";
        var suppliers = $"suppliers-{suffix}";

        object[] supplierDocs =
        [
            new { supplier_id = "S-10", supplier_name = "Daehan Fasteners" },
            new { supplier_id = "S-20", supplier_name = "Hanil Bearings" },
        ];
        object[] itemDocs =
        [
            new { item_code = "I-100", item_name = "Hex bolt M8", supplier_id = "S-10" },
            new { item_code = "I-200", item_name = "Hex nut M8", supplier_id = "S-10" },
            new { item_code = "I-300", item_name = "Ball bearing 6204", supplier_id = "S-20" },
        ];
        object[] workOrderDocs =
        [
            new { wo_no = "WO-2401", item_code = "I-100", qty = "500", note = "press line 2" },
            new { wo_no = "WO-2402", item_code = "I-300", qty = "40", note = "spindle rebuild" },
            new { wo_no = "WO-2403", item_code = "I-200", qty = "500", note = "press line 2" },
            new { wo_no = "WO-2404", item_code = "I-300", qty = "12", note = "conveyor" },
        ];

        foreach (var (type, docs) in new[] { (suppliers, supplierDocs), (items, itemDocs), (workOrders, workOrderDocs) })
        {
            foreach (var doc in docs)
            {
                (await http.PostAsJsonAsync($"formtypes/{type}/documents", doc, ct)).EnsureSuccessStatusCode();
            }
        }

        if (declare)
        {
            await DeclareAsync(http, suppliers, "suppliers", [Field("supplier_id", nullable: false), Field("supplier_name")], [], ct);
            await DeclareAsync(http, items, "items", [Field("item_code", nullable: false), Field("item_name"), Field("supplier_id")],
                [new { name = "supplier", kind = "reference", target = suppliers, keyField = "supplier_id" }], ct);
            await DeclareAsync(http, workOrders, "work_orders", [Field("wo_no", nullable: false), Field("item_code"), Field("qty", "integer"), Field("note")],
                [new { name = "item", kind = "reference", target = items, keyField = "item_code" }], ct);
        }

        return (new Dictionary<string, SubjectSource>
        {
            [workOrders] = new("formbase", "work_orders", "wo_no"),
            [items] = new("formbase", "items", "item_code"),
            [suppliers] = new("formbase", "suppliers", "supplier_id"),
        }, suffix);
    }

    private static object Field(string name, string type = "text", bool nullable = true) => new { name, type, nullable };

    private static async Task DeclareAsync(HttpClient http, string type, string table, object[] fields, object[] relations, CancellationToken ct)
    {
        using var response = await http.PutAsJsonAsync($"formtypes/{type}/declaration", new { tableName = $"{table}_{type[^8..]}", declarationVersion = 1, fields, relations }, ct);
        Assert.True(response.IsSuccessStatusCode, $"Declaring {type} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
    }

    /// <summary>Writes the pass's review where <c>SAEM_AUTHORING_REPORT_DIR</c> points, and to the test output.</summary>
    private static async Task ReportAsync(string name, AuthoringPass pass, CancellationToken ct)
    {
        var report = $"<!-- pass: {name} -->\n" + pass.ToReview();

        TestContext.Current.SendDiagnosticMessage(report);
        if (Environment.GetEnvironmentVariable("SAEM_AUTHORING_REPORT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, $"{name}.md"), report, ct);
        }
    }

    private static HttpClient Formbase() => new()
    {
        BaseAddress = new Uri(Require("SAEM_FORMBASE_URL")),
    };

    private static HttpClient ModelHttp()
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(Require("EYU_LLM_ENDPOINT").TrimEnd('/') + "/v1/"),
            Timeout = TimeSpan.FromMinutes(10),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Require("EYU_LLM_API_KEY"));
        return http;
    }

    private static string Require(string variable) =>
        Environment.GetEnvironmentVariable(variable) ?? throw new InvalidOperationException($"{variable} is required for the authoring live test.");
}
