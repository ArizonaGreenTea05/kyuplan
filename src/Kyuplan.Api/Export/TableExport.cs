using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Kyuplan.Api.Export;

/// <summary>A table to hand out as a file: a header row and rows of cells (<c>string</c>, numbers, <see cref="DateOnly"/>, <c>bool</c> or <c>null</c>).</summary>
public sealed record ExportTable(string SheetName, IReadOnlyList<string> Headers, IReadOnlyList<object?[]> Rows);

public enum ExportFormat { Csv, Xlsx }

public static class TableExport
{
    public const string CsvContentType = "text/csv; charset=utf-8";
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static bool TryParseFormat(string? text, out ExportFormat format)
    {
        format = ExportFormat.Csv;
        if (string.IsNullOrEmpty(text) || text.Equals("csv", StringComparison.OrdinalIgnoreCase)) return true;
        if (!text.Equals("xlsx", StringComparison.OrdinalIgnoreCase)) return false;
        format = ExportFormat.Xlsx;
        return true;
    }

    public static string ContentType(ExportFormat format) => format == ExportFormat.Csv ? CsvContentType : XlsxContentType;

    public static string Extension(ExportFormat format) => format == ExportFormat.Csv ? "csv" : "xlsx";

    public static byte[] Write(ExportTable table, ExportFormat format) => format == ExportFormat.Csv ? WriteCsv(table) : WriteXlsx(table);

    /// <summary>
    /// UTF-8 with a byte order mark and <c>;</c> as separator, so that Excel in a German locale opens it as it is. Numbers use a point.
    /// Text that a spreadsheet would read as a formula gets a leading apostrophe.
    /// </summary>
    public static byte[] WriteCsv(ExportTable table)
    {
        var text = new StringBuilder();
        AppendCsvLine(text, table.Headers);
        foreach (var row in table.Rows) AppendCsvLine(text, row.Select(CsvCell));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    private static void AppendCsvLine(StringBuilder text, IEnumerable<string> cells) =>
        text.Append(string.Join(';', cells.Select(Quote))).Append("\r\n");

    private static string Quote(string cell) =>
        cell.IndexOfAny([';', '"', '\r', '\n']) < 0 ? cell : "\"" + cell.Replace("\"", "\"\"") + "\"";

    private static string CsvCell(object? value) => value switch
    {
        null => "",
        string s => IsFormula(s) ? "'" + s : s,
        _ => Format(value),
    };

    private static bool IsFormula(string text) => text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r';

    private static string Format(object value) => value switch
    {
        bool b => b ? "1" : "0",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>A workbook with one sheet; numbers are numbers, the header is bold.</summary>
    public static byte[] WriteXlsx(ExportTable table)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
            Add(zip, "_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Add(zip, "xl/workbook.xml",
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{Escape(SheetName(table.SheetName))}" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Add(zip, "xl/_rels/workbook.xml.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            Add(zip, "xl/styles.xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs></styleSheet>""");
            Add(zip, "xl/worksheets/sheet1.xml", Sheet(table));
        }

        return stream.ToArray();
    }

    private static string Sheet(ExportTable table)
    {
        var xml = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        AppendRow(xml, 1, table.Headers.Cast<object?>(), bold: true);
        for (var i = 0; i < table.Rows.Count; i++) AppendRow(xml, i + 2, table.Rows[i], bold: false);
        return xml.Append("</sheetData></worksheet>").ToString();
    }

    private static void AppendRow(StringBuilder xml, int number, IEnumerable<object?> cells, bool bold)
    {
        xml.Append("<row r=\"").Append(number).Append("\">");
        var column = 0;
        foreach (var cell in cells)
        {
            var reference = ColumnName(column++) + number.ToString(CultureInfo.InvariantCulture);
            var style = bold ? " s=\"1\"" : "";
            switch (cell)
            {
                case null:
                    break;
                case decimal or int or long or double:
                    xml.Append($"<c r=\"{reference}\"{style}><v>{Format(cell)}</v></c>");
                    break;
                default:
                    xml.Append($"<c r=\"{reference}\"{style} t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(cell is string s ? s : Format(cell))}</t></is></c>");
                    break;
            }
        }

        xml.Append("</row>");
    }

    private static string ColumnName(int index)
    {
        var name = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }

    /// <summary>Excel allows 31 characters and none of <c>\ / ? * [ ] :</c> in a sheet name.</summary>
    private static string SheetName(string name)
    {
        var clean = new string([.. name.Select(c => "\\/?*[]:".Contains(c) ? ' ' : c)]).Trim();
        if (clean.Length == 0) clean = "Export";
        return clean.Length > 31 ? clean[..31] : clean;
    }

    // Control characters other than tab, CR and LF are not allowed in XML at all.
    private static string Escape(string text) =>
        SecurityElement.Escape(new string([.. text.Where(c => c is '\t' or '\r' or '\n' || c >= ' ')])) ?? "";

    private static void Add(ZipArchive zip, string path, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
