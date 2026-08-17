using System.Globalization;
using Newtonsoft.Json.Linq;

namespace AutoCADMCP.Server.Tools;

/// <summary>
/// create_table_from_excel — draw a spreadsheet as a grid of lines and text.
///
/// The geometry is built here and sent as one bulk_create per batch rather than
/// a round trip per cell; a 20x8 sheet is ~350 entities, so batching turns 350
/// calls into one.
///
/// The layout deliberately reproduces the previous Python implementation
/// exactly — same width formula, same row heights, same 0.65 baseline factor,
/// same title spanning the full width — so drawings produced before and after
/// the port are identical.
/// </summary>
public sealed class ExcelTableTool : IServerTool
{
    public string Name => "create_table_from_excel";

    private const int BatchSize = 500;
    private const double CellPad = 100;      // left inset for cell text
    private const double BaselineFactor = 0.65;  // text sits 65% down its row

    public async Task<JObject> ExecuteAsync(JObject args, PluginClient plugin, CancellationToken ct)
    {
        string path = Str(args, "excel_path");
        if (string.IsNullOrWhiteSpace(path))
            return Fail("Parameter 'excel_path' is required.");
        if (!File.Exists(path))
            return Fail($"File not found: {path}");

        if (args["position"] is not JArray pos || pos.Count < 2)
            return Fail("Parameter 'position' is required, as [x, y].");
        double x0 = pos[0].Value<double>(), y0 = pos[1].Value<double>();

        XlsxReader.Sheet sheet;
        try
        {
            sheet = XlsxReader.Read(path, Str(args, "sheet_name"));
        }
        catch (KeyNotFoundException ex) { return Fail(ex.Message); }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return Fail($"Could not read '{path}': {ex.Message}");
        }

        double textHeight = Num(args, "text_height", 120);
        double headerTextHeight = Num(args, "header_text_height", 140);
        double titleTextHeight = Num(args, "title_text_height", 250);
        double rowHeight = Num(args, "row_height", 350);
        double headerRowHeight = Num(args, "header_row_height", 400);
        double titleRowHeight = Num(args, "title_row_height", 600);
        double minColWidth = Num(args, "min_col_width", 2000);
        double charWidth = Num(args, "char_width", 80);
        int color = (int)Num(args, "color", 3);
        string layer = args["layer"] == null ? "TABLE" : Str(args, "layer");
        string title = Str(args, "title");

        // Excel-style 1-based inclusive range; 0 means "to the end".
        int startRow = (int)Num(args, "start_row", 1);
        int endRow = (int)Num(args, "end_row", 0);
        int startCol = (int)Num(args, "start_col", 1);
        int endCol = (int)Num(args, "end_col", 0);

        var data = Slice(sheet.Rows, startRow, endRow, startCol, endCol);
        if (data.Count == 0)
            return Fail($"Sheet '{sheet.Name}' has no data in the requested range.");

        int rowCount = data.Count;
        int colCount = data.Max(r => r.Count);
        if (colCount == 0)
            return Fail($"Sheet '{sheet.Name}' has no columns in the requested range.");

        // Column width from the widest cell. There are no font metrics here, so
        // this is the same estimate the Python implementation used; measure_text
        // would be exact but costs a round trip per string.
        var widths = new double[colCount];
        for (int c = 0; c < colCount; c++)
        {
            double widest = 0;
            for (int r = 0; r < rowCount; r++)
            {
                double h = r == 0 ? headerTextHeight : textHeight;
                int len = c < data[r].Count ? data[r][c].Length : 0;
                widest = Math.Max(widest, len * charWidth * (h / 120.0) + 400);
            }
            widths[c] = Math.Max(minColWidth, widest);
        }

        var colX = new double[colCount + 1];
        colX[0] = x0;
        for (int c = 0; c < colCount; c++) colX[c + 1] = colX[c] + widths[c];
        double totalW = colX[colCount] - x0;

        bool hasTitle = title.Length > 0;
        double totalH = (hasTitle ? titleRowHeight : 0) + headerRowHeight + rowHeight * (rowCount - 1);

        var entities = new JArray();

        // --- horizontal rules -------------------------------------------------
        double y = y0;
        entities.Add(Line(x0, y, x0 + totalW, y, layer, color));

        if (hasTitle)
        {
            y -= titleRowHeight;
            entities.Add(Line(x0, y, x0 + totalW, y, layer, color));
        }

        y -= headerRowHeight;
        entities.Add(Line(x0, y, x0 + totalW, y, layer, color));

        for (int r = 1; r < rowCount; r++)
        {
            y -= rowHeight;
            entities.Add(Line(x0, y, x0 + totalW, y, layer, color));
        }

        // --- verticals --------------------------------------------------------
        // With a title, only the outer two run its full height; the inner ones
        // start below it so the title reads as one merged cell.
        double vertTop = y0 - (hasTitle ? titleRowHeight : 0);
        for (int idx = 0; idx <= colCount; idx++)
        {
            double top = (hasTitle && (idx == 0 || idx == colCount)) ? y0 : vertTop;
            entities.Add(Line(colX[idx], top, colX[idx], y0 - totalH, layer, color));
        }

        // --- text -------------------------------------------------------------
        if (hasTitle)
        {
            var p = new JObject
            {
                ["position"] = new JArray(Round(x0 + totalW / 2), Round(y0 - titleRowHeight / 2)),
                ["text"] = title,
                ["height"] = titleTextHeight,
                ["layer"] = layer,
                ["color"] = color,
                ["justification"] = "middle-center",
            };
            entities.Add(new JObject { ["type"] = "text", ["params"] = p });
        }

        double headerYStart = y0 - (hasTitle ? titleRowHeight : 0);
        double hy = headerYStart - headerRowHeight * BaselineFactor;
        for (int c = 0; c < colCount; c++)
        {
            string txt = c < data[0].Count ? data[0][c] : "";
            if (txt.Length > 0)
                entities.Add(Text(colX[c] + CellPad, hy, txt, headerTextHeight, layer, color));
        }

        double dataYStart = headerYStart - headerRowHeight;
        for (int r = 1; r < rowCount; r++)
        {
            double ry = dataYStart - (r - 1) * rowHeight - rowHeight * BaselineFactor;
            for (int c = 0; c < colCount; c++)
            {
                string txt = c < data[r].Count ? data[r][c] : "";
                if (txt.Length > 0)
                    entities.Add(Text(colX[c] + CellPad, ry, txt, textHeight, layer, color));
            }
        }

        // --- send -------------------------------------------------------------
        var handles = new JArray();
        int created = 0;

        for (int i = 0; i < entities.Count; i += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = new JArray(entities.Skip(i).Take(BatchSize));
            var response = await plugin.CallAsync("bulk_create", new JObject { ["entities"] = batch }, ct);

            if (response["error"] is JObject err)
            {
                return Fail($"bulk_create failed on batch {i / BatchSize + 1}: " +
                            (err["message"]?.ToString() ?? "unknown error"),
                            new JObject { ["entities_created"] = created });
            }

            if (response["result"]?["handles"] is JArray got)
            {
                foreach (var h in got) handles.Add(h);
                created += got.Count;
            }
        }

        return new JObject
        {
            ["success"] = true,
            ["message"] = $"Table created from '{Path.GetFileName(path)}'",
            ["sheet"] = sheet.Name,
            ["data_rows"] = rowCount,
            ["columns"] = colCount,
            ["entities_created"] = created,
            ["table_width"] = Round(totalW),
            ["table_height"] = Round(totalH),
            ["position"] = new JArray(x0, y0),
            ["handles"] = handles,
        };
    }

    /// <summary>Apply the 1-based, inclusive Excel range; 0 means to the end.</summary>
    private static List<List<string>> Slice(List<List<string>> rows,
                                            int startRow, int endRow, int startCol, int endCol)
    {
        int r0 = Math.Max(0, startRow - 1);
        int r1 = endRow > 0 ? Math.Min(rows.Count, endRow) : rows.Count;
        int c0 = Math.Max(0, startCol - 1);

        var result = new List<List<string>>();
        for (int r = r0; r < r1; r++)
        {
            var source = rows[r];
            int c1 = endCol > 0 ? Math.Min(source.Count, endCol) : source.Count;

            var line = new List<string>();
            for (int c = c0; c < c1; c++) line.Add(source[c]);
            result.Add(line);
        }

        // Trailing blank rows carry no information once a range is applied.
        while (result.Count > 0 && result[^1].All(string.IsNullOrEmpty))
            result.RemoveAt(result.Count - 1);

        return result;
    }

    // ---- helpers -----------------------------------------------------------

    private static JObject Text(double x, double y, string text, double height,
                                string layer, int color) =>
        new()
        {
            ["type"] = "text",
            ["params"] = new JObject
            {
                ["position"] = new JArray(Round(x), Round(y)),
                ["text"] = text,
                ["height"] = height,
                ["layer"] = layer,
                ["color"] = color,
            },
        };

    private static JObject Line(double x1, double y1, double x2, double y2,
                                string layer, int color) =>
        new()
        {
            ["type"] = "line",
            ["params"] = new JObject
            {
                ["start"] = new JArray(Round(x1), Round(y1)),
                ["end"] = new JArray(Round(x2), Round(y2)),
                ["layer"] = layer,
                ["color"] = color,
            },
        };

    private static double Round(double v) => Math.Round(v, 4);

    private static JObject Fail(string message, JObject? extra = null)
    {
        var o = new JObject { ["success"] = false, ["error"] = message };
        if (extra != null)
            foreach (var p in extra.Properties()) o[p.Name] = p.Value;
        return o;
    }

    private static string Str(JObject a, string name) => a[name]?.ToString() ?? "";

    private static double Num(JObject a, string name, double fallback)
    {
        var t = a[name];
        if (t == null || t.Type == JTokenType.Null) return fallback;
        return double.TryParse(t.ToString(), NumberStyles.Float,
                               CultureInfo.InvariantCulture, out double v) ? v : fallback;
    }
}
