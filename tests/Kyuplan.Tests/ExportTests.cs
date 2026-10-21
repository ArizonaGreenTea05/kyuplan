using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Kyuplan.Api.Export;
using Kyuplan.Client.Api;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

public sealed class ExportTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly InMemoryDatabase _database = new();
    private readonly InMemoryDatabase _accounts = new();

    public void Dispose()
    {
        _database.Dispose();
        _accounts.Dispose();
    }

    private static ExportTable Sample() => new("Sheet", ["Name", "Amount", "Day"],
        [["Müller; \"Max\"", 12.5m, new DateOnly(2026, 9, 26)], ["=SUM(A1)", null, null]]);

    [Fact]
    public void Csv_has_bom_semicolons_quotes_and_neutralises_formulas()
    {
        var bytes = TableExport.WriteCsv(Sample());

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("Name;Amount;Day\r\n\"Müller; \"\"Max\"\"\";12.5;2026-09-26\r\n'=SUM(A1);;\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public void Xlsx_is_a_workbook_with_numbers_and_escaped_text()
    {
        using var zip = new ZipArchive(new MemoryStream(TableExport.WriteXlsx(Sample())));
        string Read(string path) => new StreamReader(zip.GetEntry(path)!.Open()).ReadToEnd();

        Assert.Contains("name=\"Sheet\"", Read("xl/workbook.xml"));
        var sheet = Read("xl/worksheets/sheet1.xml");
        Assert.Contains("<c r=\"B2\"><v>12.5</v></c>", sheet);
        Assert.Contains("Müller; &quot;Max&quot;", sheet);
        Assert.Contains("<c r=\"A1\" s=\"1\" t=\"inlineStr\">", sheet);
    }

    [Fact]
    public async Task Exports_download_for_an_exam_and_reject_an_unknown_format()
    {
        var http = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _database.ConnectionString);
            TestAccounts.Configure(builder, _accounts);
        }).CreateSignedInClient();
        var api = new PlannerApiClient(http);
        var ev = (await api.GetEventChoicesAsync("demo-dojo")).Events.First(e => e.Name == "Gürtelprüfung");
        var exam = await api.CreateExamAsync(new ExamInputDto("demo-dojo", ev.Id));

        var payments = await http.GetAsync($"api/exams/{exam.Id}/payments/export");
        Assert.Equal(HttpStatusCode.OK, payments.StatusCode);
        Assert.Equal("text/csv", payments.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith("-payments.csv", payments.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Contains("Name;Belt;General fee", Encoding.UTF8.GetString(await payments.Content.ReadAsByteArrayAsync()));

        var results = await http.GetAsync($"api/exams/{exam.Id}/results/export?format=xlsx");
        Assert.Equal(HttpStatusCode.OK, results.StatusCode);
        Assert.Equal((byte)'P', (await results.Content.ReadAsByteArrayAsync())[0]);

        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync($"api/exams/{exam.Id}/results/export?format=pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("api/exams/99999/payments/export")).StatusCode);
    }
}
