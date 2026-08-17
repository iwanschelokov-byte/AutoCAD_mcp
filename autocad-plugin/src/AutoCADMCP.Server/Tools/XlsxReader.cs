using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace AutoCADMCP.Server.Tools;

/// <summary>
/// Minimal read-only .xlsx reader.
///
/// An .xlsx is a zip of XML, and all this needs is cell values from one sheet.
/// A real spreadsheet library (ClosedXML) would pull in eight packages including
/// a font rasteriser — worth avoiding for a job this small, and it keeps the
/// published server lean.
///
/// Handles shared strings, inline strings, numbers, booleans, and dates (via the
/// cell's number format), which is the same surface openpyxl exposed to the
/// Python implementation this replaces.
/// </summary>
public static class XlsxReader
{
    private static readonly XNamespace Main =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRel =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    public sealed class Sheet
    {
        public string Name { get; init; } = "";
        /// <summary>Rows of cell values, already trimmed to the used range.</summary>
        public List<List<string>> Rows { get; init; } = new();
    }

    /// <summary>
    /// Read one worksheet. Pass an empty name for the first sheet.
    /// Throws IOException / InvalidDataException for an unreadable file, and
    /// KeyNotFoundException when the named sheet does not exist.
    /// </summary>
    public static Sheet Read(string path, string sheetName = "")
    {
        using var zip = ZipFile.OpenRead(path);

        var shared = ReadSharedStrings(zip);
        var dateStyles = ReadDateStyles(zip);
        var (name, target) = ResolveSheet(zip, sheetName);

        var entry = zip.GetEntry(target)
            ?? throw new KeyNotFoundException($"Worksheet part not found in workbook: {target}");

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);

        // Cells are sparse — a row may skip columns entirely — so index by the
        // column letters in each cell's reference rather than by position.
        var rows = new List<List<string>>();
        int width = 0;

        foreach (var row in doc.Descendants(Main + "row"))
        {
            var cells = new SortedDictionary<int, string>();

            foreach (var c in row.Elements(Main + "c"))
            {
                string reference = (string?)c.Attribute("r") ?? "";
                int col = ColumnIndex(reference);
                if (col < 0) continue;

                string value = CellValue(c, shared, dateStyles);
                if (value.Length > 0) cells[col] = value;
            }

            if (cells.Count == 0)
            {
                rows.Add(new List<string>());
                continue;
            }

            int last = cells.Keys.Max();
            width = Math.Max(width, last + 1);

            var flat = new List<string>(last + 1);
            for (int i = 0; i <= last; i++)
                flat.Add(cells.TryGetValue(i, out var v) ? v : "");
            rows.Add(flat);
        }

        // Drop trailing blank rows, then pad every row to a rectangle.
        while (rows.Count > 0 && rows[^1].All(string.IsNullOrEmpty)) rows.RemoveAt(rows.Count - 1);
        foreach (var r in rows)
            while (r.Count < width) r.Add("");

        return new Sheet { Name = name, Rows = rows };
    }

    /// <summary>List the worksheet names, in workbook order.</summary>
    public static List<string> SheetNames(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var wb = zip.GetEntry("xl/workbook.xml");
        if (wb == null) return new List<string>();

        using var stream = wb.Open();
        return XDocument.Load(stream)
            .Descendants(Main + "sheet")
            .Select(s => (string?)s.Attribute("name") ?? "")
            .Where(n => n.Length > 0)
            .ToList();
    }

    // ---- internals ---------------------------------------------------------

    private static (string Name, string Target) ResolveSheet(ZipArchive zip, string wanted)
    {
        var wbEntry = zip.GetEntry("xl/workbook.xml")
            ?? throw new InvalidDataException("Not a workbook: xl/workbook.xml is missing.");

        XDocument wb;
        using (var s = wbEntry.Open()) wb = XDocument.Load(s);

        var sheets = wb.Descendants(Main + "sheet").ToList();
        if (sheets.Count == 0) throw new InvalidDataException("The workbook contains no sheets.");

        var chosen = string.IsNullOrWhiteSpace(wanted)
            ? sheets[0]
            : sheets.FirstOrDefault(s =>
                  string.Equals((string?)s.Attribute("name"), wanted, StringComparison.OrdinalIgnoreCase))
              ?? throw new KeyNotFoundException(
                  $"Sheet '{wanted}' not found. Available: " +
                  string.Join(", ", sheets.Select(s => (string?)s.Attribute("name"))));

        string name = (string?)chosen.Attribute("name") ?? "";
        string relId = (string?)chosen.Attribute(Rel + "id") ?? "";

        // Map the relationship id to the actual part path.
        var relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (relsEntry != null && relId.Length > 0)
        {
            XDocument rels;
            using (var s = relsEntry.Open()) rels = XDocument.Load(s);

            string? target = rels.Descendants(PkgRel + "Relationship")
                .FirstOrDefault(r => (string?)r.Attribute("Id") == relId)
                ?.Attribute("Target")?.Value;

            if (!string.IsNullOrEmpty(target))
            {
                target = target.TrimStart('/');
                if (!target.StartsWith("xl/", StringComparison.Ordinal)) target = "xl/" + target;
                return (name, target);
            }
        }

        // Fall back to positional naming when the rels part is absent.
        int index = sheets.IndexOf(chosen) + 1;
        return (name, $"xl/worksheets/sheet{index}.xml");
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var result = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return result;

        using var stream = entry.Open();
        foreach (var si in XDocument.Load(stream).Descendants(Main + "si"))
        {
            // A string may be split across runs (<r><t>..</t></r>); concatenate.
            result.Add(string.Concat(si.Descendants(Main + "t").Select(t => t.Value)));
        }
        return result;
    }

    /// <summary>
    /// Style indices whose number format renders as a date. Without this, a date
    /// cell reads back as its serial number (45123 rather than 2023-07-14).
    /// </summary>
    private static HashSet<int> ReadDateStyles(ZipArchive zip)
    {
        var dateStyles = new HashSet<int>();
        var entry = zip.GetEntry("xl/styles.xml");
        if (entry == null) return dateStyles;

        XDocument doc;
        using (var s = entry.Open()) doc = XDocument.Load(s);

        // Custom formats are dates if their code mentions a date component
        // outside a literal. A light check is enough here.
        var customDate = doc.Descendants(Main + "numFmt")
            .Where(f =>
            {
                string code = ((string?)f.Attribute("formatCode") ?? "").ToLowerInvariant();
                code = System.Text.RegularExpressions.Regex.Replace(code, "\\[[^\\]]*\\]", "");
                return code.Contains('y') || code.Contains('d') ||
                       code.Contains("mmm") || code.Contains("hh");
            })
            .Select(f => int.TryParse((string?)f.Attribute("numFmtId"), out int id) ? id : -1)
            .Where(id => id >= 0)
            .ToHashSet();

        // Built-in date/time formats.
        var builtIn = new HashSet<int> { 14, 15, 16, 17, 18, 19, 20, 21, 22, 45, 46, 47 };

        var xfs = doc.Descendants(Main + "cellXfs").Elements(Main + "xf").ToList();
        for (int i = 0; i < xfs.Count; i++)
        {
            if (!int.TryParse((string?)xfs[i].Attribute("numFmtId"), out int fmt)) continue;
            if (builtIn.Contains(fmt) || customDate.Contains(fmt)) dateStyles.Add(i);
        }
        return dateStyles;
    }

    private static string CellValue(XElement c, List<string> shared, HashSet<int> dateStyles)
    {
        string type = (string?)c.Attribute("t") ?? "n";

        if (type == "inlineStr")
            return string.Concat(c.Descendants(Main + "t").Select(t => t.Value));

        string raw = c.Element(Main + "v")?.Value ?? "";
        if (raw.Length == 0) return "";

        switch (type)
        {
            case "s":
                return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int si)
                       && si >= 0 && si < shared.Count
                    ? shared[si]
                    : "";
            case "b":
                return raw == "1" ? "TRUE" : "FALSE";
            case "str":
            case "e":
                return raw;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            return raw;

        int styleIndex = int.TryParse((string?)c.Attribute("s"), out int sx) ? sx : -1;
        if (styleIndex >= 0 && dateStyles.Contains(styleIndex) && num > 0)
        {
            // Excel's epoch is 1899-12-30 (its 1900 leap-year bug is baked in).
            var date = new DateTime(1899, 12, 30).AddDays(num);
            return date.TimeOfDay == TimeSpan.Zero
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        return num.ToString("0.############", CultureInfo.InvariantCulture);
    }

    /// <summary>"BC12" -> 54. Returns -1 when the reference has no column part.</summary>
    private static int ColumnIndex(string reference)
    {
        int index = 0, letters = 0;
        foreach (char ch in reference)
        {
            if (ch is >= 'A' and <= 'Z') { index = index * 26 + (ch - 'A' + 1); letters++; }
            else if (ch is >= 'a' and <= 'z') { index = index * 26 + (ch - 'a' + 1); letters++; }
            else break;
        }
        return letters == 0 ? -1 : index - 1;
    }
}
