using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using AutoCADMCP.Agent;
using AutoCADMCP.Server;
using AutoCADMCP.Server.Tools;
using Newtonsoft.Json.Linq;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

// Tests the two tools the server implements itself rather than proxying:
// XlsxReader (create_table_from_excel) and the PDF crop (plot_to_pdf).
// Fixtures are generated here, so the test needs no AutoCAD, no Python, and no
// checked-in binaries.

int failures = 0;

void Check(string name, bool ok, string detail)
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name,-44} {detail}");
    if (!ok) failures++;
}

string dir = Path.Combine(Path.GetTempPath(), "autocadmcp-server-tool-tests");
Directory.CreateDirectory(dir);
string xlsx = Path.Combine(dir, "schedule.xlsx");
string pdf = Path.Combine(dir, "plot.pdf");
string junk = Path.Combine(dir, "junk.pdf");
foreach (var f in new[] { xlsx, pdf, junk }) File.Delete(f);

const double MmPerInch = 25.4, PtPerInch = 72.0;
static double ToPt(double mm) => mm / MmPerInch * PtPerInch;
static double ToMm(double pt) => pt / PtPerInch * MmPerInch;

// ============================================================================
// XlsxReader
// ============================================================================
Console.WriteLine("\n[ XlsxReader ]");

WriteFixtureWorkbook(xlsx);

var names = XlsxReader.SheetNames(xlsx);
Check("lists sheets in workbook order", names.SequenceEqual(new[] { "Schedule", "Notes" }),
      string.Join(", ", names));

var sheet = XlsxReader.Read(xlsx);
Check("defaults to the first sheet", sheet.Name == "Schedule", sheet.Name);
Check("reads every row", sheet.Rows.Count == 5, $"{sheet.Rows.Count} rows");
Check("header row intact", string.Join("|", sheet.Rows[0]) == "Tag|Description|Qty|Date",
      string.Join("|", sheet.Rows[0]));
Check("shared strings resolved", sheet.Rows[1][1] == "Downlight, recessed", sheet.Rows[1][1]);
Check("inline strings resolved", sheet.Rows[2][1] == "Inline cell", sheet.Rows[2][1]);
Check("integers are not padded", sheet.Rows[1][2] == "24", sheet.Rows[1][2]);
Check("floats keep precision", sheet.Rows[2][2] == "0.018", sheet.Rows[2][2]);
Check("dates render as dates, not serials", sheet.Rows[1][3] == "2026-07-14", sheet.Rows[1][3]);
Check("missing cell reads as empty", sheet.Rows[2][3] == "", $"'{sheet.Rows[2][3]}'");
Check("blank interior row preserved", sheet.Rows[3].All(string.IsNullOrEmpty),
      $"{sheet.Rows[3].Count} empty cells");
Check("sparse row keyed by column letter", sheet.Rows[4][3] == "gap-then-D",
      $"col D = '{sheet.Rows[4][3]}', col B = '{sheet.Rows[4][1]}'");
Check("rows padded to a rectangle", sheet.Rows.All(r => r.Count == 4),
      string.Join(",", sheet.Rows.Select(r => r.Count)));

var notes = XlsxReader.Read(xlsx, "Notes");
Check("reads a sheet by name", notes.Name == "Notes" && notes.Rows[0][0] == "second sheet",
      notes.Rows[0][0]);

try
{
    XlsxReader.Read(xlsx, "Nope");
    Check("unknown sheet throws", false, "no exception raised");
}
catch (KeyNotFoundException ex)
{
    Check("unknown sheet names the alternatives", ex.Message.Contains("Schedule"),
          ex.Message.Split('.')[0]);
}

// ============================================================================
// PDF crop
// ============================================================================
Console.WriteLine("\n[ PDF crop ]");

// "DWG To PDF.pc3" quantises the sheet, so a nominal A1 arrives slightly over.
WriteFixturePdf(pdf, 841.02, 594.08);

double beforeW;
using (var before = PdfReader.Open(pdf, PdfDocumentOpenMode.Import))
    beforeW = ToMm(before.Pages[0].MediaBox.Width);
Check("fixture is an over-size A1", Math.Abs(beforeW - 841.02) < 0.5, $"{beforeW:0.##} mm wide");

// Trim is private by design; the public surface needs a live plugin.
var trim = typeof(PlotToPdfTool).GetMethod("Trim", BindingFlags.NonPublic | BindingFlags.Static)!;
JObject Trim(string path, double w, double h) =>
    (JObject)trim.Invoke(null, new object[] { path, w, h })!;

var report = Trim(pdf, 840.0, 594.0);
Check("reports the crop as done", (bool?)report["trimmed"] == true,
      report.ToString(Newtonsoft.Json.Formatting.None));

using (var after = PdfReader.Open(pdf, PdfDocumentOpenMode.Import))
{
    var box = after.Pages[0].MediaBox;
    double w = ToMm(box.Width), h = ToMm(box.Height);
    Check("MediaBox is exactly the requested size",
          Math.Abs(w - 840) < 0.05 && Math.Abs(h - 594) < 0.05, $"{w:0.###} x {h:0.###} mm");
    Check("crop is centred on the plotted page", Math.Abs(ToMm(box.X1) - 0.51) < 0.05,
          $"x1 = {ToMm(box.X1):0.###} mm, half the 1.02 mm surplus");
    Check("stale CropBox removed", after.Pages[0].Elements["/CropBox"] == null,
          "viewers would otherwise still show the untrimmed page");
}

var grown = Trim(pdf, 2000.0, 2000.0);
Check("refuses to grow the page", (bool?)grown["trimmed"] == false,
      (grown["trim_error"]?.ToString() ?? "").Split('.')[0]);

var negative = Trim(pdf, 0.0, 594.0);
Check("rejects a non-positive size", (bool?)negative["trimmed"] == false, "reported trim_error");

File.WriteAllText(junk, "this is not a pdf");
var corrupt = Trim(junk, 100.0, 100.0);
Check("corrupt PDF degrades instead of throwing",
      (bool?)corrupt["trimmed"] == false && corrupt["trim_error"] != null,
      "a failed crop must not fail the plot");

// ============================================================================
// ExcelTableTool geometry
//
// These expectations were verified against the Python implementation this
// replaces: on a shared fixture, 36 of 39 emitted entities were byte-identical.
// The 3 that differed were dates, which now render as "2026-07-14" instead of
// str(datetime)'s "2026-07-14 00:00:00".
// ============================================================================
Console.WriteLine("\n[ ExcelTableTool geometry ]");

var captured = new JArray();
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
int port = ((IPEndPoint)listener.LocalEndpoint).Port;

// A fake plugin on a real socket, so PluginClient is exercised too.
_ = Task.Run(async () =>
{
    while (true)
    {
        TcpClient client;
        try { client = await listener.AcceptTcpClientAsync(); }
        catch (SocketException) { return; }
        catch (ObjectDisposedException) { return; }

        using (client)
        using (var netStream = client.GetStream())
        {
            var buf = new byte[1 << 20];
            var sb = new StringBuilder();
            int got;
            while (!sb.ToString().Contains('\n') && (got = await netStream.ReadAsync(buf)) > 0)
                sb.Append(Encoding.UTF8.GetString(buf, 0, got));

            string firstLine = sb.ToString().Split('\n')[0];
            if (firstLine.Length == 0) continue;

            var request = JObject.Parse(firstLine);
            var ents = request["params"]?["entities"] as JArray ?? new JArray();
            foreach (var e in ents) captured.Add(e);

            var reply = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = request["id"],
                ["result"] = new JObject
                {
                    ["handles"] = new JArray(Enumerable.Range(0, ents.Count).Select(i => i.ToString())),
                    ["count"] = ents.Count,
                },
            };
            var outBytes = Encoding.UTF8.GetBytes(reply.ToString(Newtonsoft.Json.Formatting.None) + "\n");
            await netStream.WriteAsync(outBytes);
            await netStream.FlushAsync();
        }
    }
});

var summary = await new ExcelTableTool().ExecuteAsync(
    new JObject { ["excel_path"] = xlsx, ["position"] = new JArray(0.0, 0.0) },
    new PluginClient("127.0.0.1", port, 30000), CancellationToken.None);
listener.Stop();

Check("reports success", (bool?)summary["success"] == true,
      summary["error"]?.ToString() ?? "ok");
Check("counts data rows and columns",
      (int?)summary["data_rows"] == 5 && (int?)summary["columns"] == 4,
      $"{summary["data_rows"]} rows x {summary["columns"]} cols");

// Every column falls under min_col_width (2000), so 4 x 2000 wide; height is
// header_row_height + row_height * (rows - 1) = 400 + 350*4.
Check("width honours min_col_width", (double?)summary["table_width"] == 8000.0,
      $"{summary["table_width"]} units");
Check("height is header plus data rows", (double?)summary["table_height"] == 1800.0,
      $"{summary["table_height"]} units");

var lines = captured.Where(e => (string?)e["type"] == "line").ToList();
var texts = captured.Where(e => (string?)e["type"] == "text").ToList();

Check("one rule per row boundary", lines.Count(IsHorizontal) == 6,
      $"{lines.Count(IsHorizontal)} horizontal");
Check("one vertical per column edge", lines.Count(l => !IsHorizontal(l)) == 5,
      $"{lines.Count(l => !IsHorizontal(l))} vertical");
Check("blank cells emit no text", texts.Count == 12, $"{texts.Count} text entities");
Check("dates carry no time component",
      texts.Any(t => (string?)t["params"]?["text"] == "2026-07-14"),
      "2026-07-14, not '2026-07-14 00:00:00'");
Check("header row uses the header text height",
      texts.Any(t => (string?)t["params"]?["text"] == "Tag" &&
                     (double?)t["params"]?["height"] == 140.0), "Tag at h=140");
Check("cell text is inset by the cell padding",
      texts.Any(t => (double?)(t["params"]?["position"] as JArray)?[0] == 100.0), "x = 100");
Check("defaults to layer TABLE and colour 3",
      texts.All(t => (string?)t["params"]?["layer"] == "TABLE" &&
                     (int?)t["params"]?["color"] == 3), "layer=TABLE colour=3");

static bool IsHorizontal(JToken line)
{
    var a = line["params"]?["start"] as JArray;
    var b = line["params"]?["end"] as JArray;
    return a != null && b != null && Math.Abs((double)a[1] - (double)b[1]) < 1e-9;
}

// ============================================================================
// CodeRunner — the agent executes the C# the model writes
// ============================================================================

Console.WriteLine("\n[ CodeRunner ]");

// A second fake plugin that answers any method, so generated code can call
// through it the way a real drawing script would.
var seen = new List<string>();
var runnerListener = new TcpListener(IPAddress.Loopback, 0);
runnerListener.Start();
int runnerPort = ((IPEndPoint)runnerListener.LocalEndpoint).Port;

_ = Task.Run(async () =>
{
    while (true)
    {
        TcpClient client;
        try { client = await runnerListener.AcceptTcpClientAsync(); }
        catch (SocketException) { return; }
        catch (ObjectDisposedException) { return; }

        using (client)
        using (var netStream = client.GetStream())
        {
            var buf = new byte[1 << 16];
            var sb = new StringBuilder();
            int got;
            while (!sb.ToString().Contains('\n') && (got = await netStream.ReadAsync(buf)) > 0)
                sb.Append(Encoding.UTF8.GetString(buf, 0, got));

            string firstLine = sb.ToString().Split('\n')[0];
            if (firstLine.Length == 0) continue;

            var request = JObject.Parse(firstLine);
            string method = request["method"]?.ToString() ?? "";
            lock (seen) seen.Add(method);

            // One method fails, so the error path is covered as well as the happy one.
            JObject reply = method == "erase_entity"
                ? new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = request["id"],
                    ["error"] = new JObject
                    {
                        ["code"] = -32000,
                        ["message"] = "Confirmation required.",
                        ["data"] = new JObject { ["errorCode"] = "NeedsConfirm" },
                    },
                }
                : new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = request["id"],
                    ["result"] = new JObject
                    {
                        ["id"] = "27F",
                        ["echo"] = request["params"],
                    },
                };

            var outBytes = Encoding.UTF8.GetBytes(reply.ToString(Newtonsoft.Json.Formatting.None) + "\n");
            await netStream.WriteAsync(outBytes);
            await netStream.FlushAsync();
        }
    }
});

var runnerPlugin = new PluginClient("127.0.0.1", runnerPort, 15000);

// Exercises what the system prompt actually promises the model: Call with an
// anonymous type, loops, `using static System.Math`, Console output, Result.
var ok = await CodeRunner.RunAsync("""
    var handles = new List<string>();
    for (int i = 0; i < 3; i++)
    {
        double angle = PI * i / 3.0;
        var r = Call("create_line", new {
            start = new[] { 0.0, 0.0 },
            end = new[] { Cos(angle) * 100.0, Sin(angle) * 100.0 },
            layer = "GRID",
        });
        handles.Add(r["id"]!.ToString());
    }
    Console.WriteLine($"drew {handles.Count}");
    Result = new { count = handles.Count, first = handles[0] };
    """, runnerPlugin, 15000);

Check("runs generated code", (bool?)ok["success"] == true,
      ok["error"]?.ToString() ?? ok["detail"]?.ToString() ?? "ok");
Check("Call reaches the plugin", seen.Count(m => m == "create_line") == 3,
      $"{seen.Count(m => m == "create_line")} create_line calls");
Check("captures Console output", (ok["output"]?.ToString() ?? "").Contains("drew 3"),
      (ok["output"]?.ToString() ?? "").Trim());
Check("returns Result as JSON", (int?)ok["result"]?["count"] == 3 &&
      (string?)ok["result"]?["first"] == "27F", ok["result"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "null");

// A plugin-reported error must surface as data, not escape as an exception.
var refused = await CodeRunner.RunAsync(
    """Call("erase_entity", new { id = "27F" });""", runnerPlugin, 15000);

Check("plugin errors become failures", (bool?)refused["success"] == false,
      refused["error"]?.ToString() ?? "unexpectedly succeeded");
Check("plugin error keeps its errorCode",
      (refused["error"]?.ToString() ?? "").Contains("NeedsConfirm"),
      refused["error"]?.ToString() ?? "");

// Code that does not compile is a report, not a crash.
var broken = await CodeRunner.RunAsync("this is not C#;", runnerPlugin, 15000);
Check("compile errors are reported", (bool?)broken["success"] == false &&
      (broken["error"]?.ToString() ?? "").Contains("did not compile"),
      broken["error"]?.ToString() ?? "");

// So is code that throws at runtime.
var threw = await CodeRunner.RunAsync("throw new InvalidOperationException(\"boom\");",
                                      runnerPlugin, 15000);
Check("runtime exceptions are reported", (bool?)threw["success"] == false &&
      (threw["error"]?.ToString() ?? "").Contains("boom"),
      threw["error"]?.ToString() ?? "");

runnerListener.Stop();

// ============================================================================
// Prompts — the catalogue the model is shown
// ============================================================================

Console.WriteLine("\n[ Agent prompt ]");

var catalogue = Prompts.LoadTools();
string systemPrompt = Prompts.BuildSystemPrompt();

Check("catalogue is embedded", catalogue.Count > 100, $"{catalogue.Count} tools");
Check("prompt names every tool",
      catalogue.All(t => systemPrompt.Contains(t["name"]?.ToString() ?? " ")),
      "all present");
Check("prompt states the Call contract",
      systemPrompt.Contains("JObject Call(string method"), "Call signature documented");
Check("prompt warns about confirmation",
      systemPrompt.Contains("__confirm"), "destructive gate documented");

Console.WriteLine();
Console.WriteLine(failures == 0
    ? "  All server-tool checks passed.\n"
    : $"  {failures} check(s) FAILED\n");
return failures == 0 ? 0 : 1;


// ============================================================================
// Fixtures
// ============================================================================

// A minimal but real .xlsx: two sheets, shared and inline strings, a styled date,
// a float, a blank row, and a row that skips columns.
static void WriteFixtureWorkbook(string path)
{
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

    void Part(string name, string xml)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open());
        w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + xml);
    }

    const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    const string rns = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    const string pns = "http://schemas.openxmlformats.org/package/2006/relationships";

    Part("[Content_Types].xml",
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>" +
        "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
        "</Types>");

    Part("_rels/.rels",
        $"<Relationships xmlns=\"{pns}\"><Relationship Id=\"rId1\" " +
        $"Type=\"{rns}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");

    Part("xl/workbook.xml",
        $"<workbook xmlns=\"{ns}\" xmlns:r=\"{rns}\"><sheets>" +
        "<sheet name=\"Schedule\" sheetId=\"1\" r:id=\"rId1\"/>" +
        "<sheet name=\"Notes\" sheetId=\"2\" r:id=\"rId2\"/>" +
        "</sheets></workbook>");

    Part("xl/_rels/workbook.xml.rels",
        $"<Relationships xmlns=\"{pns}\">" +
        $"<Relationship Id=\"rId1\" Type=\"{rns}/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        $"<Relationship Id=\"rId2\" Type=\"{rns}/worksheet\" Target=\"worksheets/sheet2.xml\"/>" +
        $"<Relationship Id=\"rId3\" Type=\"{rns}/styles\" Target=\"styles.xml\"/>" +
        $"<Relationship Id=\"rId4\" Type=\"{rns}/sharedStrings\" Target=\"sharedStrings.xml\"/>" +
        "</Relationships>");

    string[] shared = { "Tag", "Description", "Qty", "Date", "L-01", "Downlight, recessed",
                        "L-02", "second sheet", "gap-then-D" };
    Part("xl/sharedStrings.xml",
        $"<sst xmlns=\"{ns}\" count=\"{shared.Length}\" uniqueCount=\"{shared.Length}\">" +
        string.Concat(shared.Select(s =>
            $"<si><t>{s.Replace("&", "&amp;").Replace("<", "&lt;")}</t></si>")) +
        "</sst>");

    // cellXfs index 1 carries built-in date format 14 -> XlsxReader must
    // recognise it and convert the serial number back to a date.
    Part("xl/styles.xml",
        $"<styleSheet xmlns=\"{ns}\"><cellXfs count=\"2\">" +
        "<xf numFmtId=\"0\"/><xf numFmtId=\"14\" applyNumberFormat=\"1\"/>" +
        "</cellXfs></styleSheet>");

    // 2026-07-14 as an Excel serial (epoch 1899-12-30).
    double serial = (new DateTime(2026, 7, 14) - new DateTime(1899, 12, 30)).TotalDays;

    Part("xl/worksheets/sheet1.xml",
        $"<worksheet xmlns=\"{ns}\"><sheetData>" +
        "<row r=\"1\">" +
        "<c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c>" +
        "<c r=\"C1\" t=\"s\"><v>2</v></c><c r=\"D1\" t=\"s\"><v>3</v></c></row>" +
        "<row r=\"2\">" +
        "<c r=\"A2\" t=\"s\"><v>4</v></c><c r=\"B2\" t=\"s\"><v>5</v></c>" +
        "<c r=\"C2\"><v>24</v></c>" +
        $"<c r=\"D2\" s=\"1\"><v>{serial.ToString(CultureInfo.InvariantCulture)}</v></c></row>" +
        "<row r=\"3\">" +
        "<c r=\"A3\" t=\"s\"><v>6</v></c>" +
        "<c r=\"B3\" t=\"inlineStr\"><is><t>Inline cell</t></is></c>" +
        "<c r=\"C3\"><v>0.018</v></c></row>" +
        "<row r=\"4\"/>" +
        "<row r=\"5\"><c r=\"D5\" t=\"s\"><v>8</v></c></row>" +
        "</sheetData></worksheet>");

    Part("xl/worksheets/sheet2.xml",
        $"<worksheet xmlns=\"{ns}\"><sheetData>" +
        "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>7</v></c></row>" +
        "</sheetData></worksheet>");
}

// A one-page PDF at the given millimetre size, carrying a CropBox at the full
// size — the case that makes viewers ignore a corrected MediaBox.
static void WriteFixturePdf(string path, double widthMm, double heightMm)
{
    using var doc = new PdfDocument();
    var page = doc.AddPage();
    page.Width = XUnit.FromPoint(widthMm / 25.4 * 72.0);
    page.Height = XUnit.FromPoint(heightMm / 25.4 * 72.0);
    page.Elements["/CropBox"] =
        new PdfRectangle(new XRect(0, 0, page.Width.Point, page.Height.Point));
    doc.Save(path);
}
