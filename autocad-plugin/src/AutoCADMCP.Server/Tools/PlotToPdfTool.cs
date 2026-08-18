using Newtonsoft.Json.Linq;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace AutoCADMCP.Server.Tools;

/// <summary>
/// plot_to_pdf — plot via the plugin, then crop the page to the requested size.
///
/// AutoCAD can only plot onto a paper size its driver defines, so a non-standard
/// sheet (840x594, 297x630) is plotted 1:1 centred on the nearest larger sheet.
/// The plugin reports the size it should have been as `required_mm` and leaves
/// the crop to us — it has no PDF library by design, because every extra
/// assembly inside acad.exe risks shadowing one AutoCAD already loaded.
///
/// A PDF that cannot be cropped is still a valid plot, so failures here are
/// reported in `trim_error` and never turned into a failed call.
/// </summary>
public sealed class PlotToPdfTool : IServerTool
{
    public string Name => "plot_to_pdf";

    private const double MmPerInch = 25.4;
    private const double PointsPerInch = 72.0;

    public async Task<JObject> ExecuteAsync(JObject args, PluginClient plugin, CancellationToken ct)
    {
        var response = await plugin.CallAsync("plot_to_pdf", args, ct);

        if (response["error"] is JObject err)
        {
            var message = err["message"]?.ToString() ?? "unknown plugin error";
            var code = err["data"]?["errorCode"]?.ToString();
            return new JObject
            {
                ["success"] = false,
                ["error"] = code == null ? message : $"[{code}] {message}",
            };
        }

        if (response["result"] is not JObject result)
            return new JObject { ["success"] = false, ["error"] = "The plugin returned no result." };

        // Nothing to do unless the plot succeeded and asked for a specific size.
        if (result["success"]?.Type == JTokenType.Boolean && !result["success"]!.Value<bool>())
            return result;

        string path = result["output_path"]?.ToString() ?? args["output_path"]?.ToString() ?? "";
        if (result["required_mm"] is not JArray need || need.Count < 2)
            return result;   // standard sheet — the driver already produced the right size

        // trim=false is the caller keeping the whole printer sheet on purpose.
        // Reported rather than silent, so "why is my page still 841 wide" has an
        // answer in the reply itself.
        if (args["trim"]?.Type == JTokenType.Boolean && !args["trim"]!.Value<bool>())
        {
            result["trimmed"] = false;
            result["trim_reason"] = "trim was false, so the whole printer sheet was kept.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            result["trimmed"] = false;
            result["trim_error"] = string.IsNullOrWhiteSpace(path)
                ? "The plot did not report an output path, so the page could not be cropped."
                : $"The plotted file was not found at '{path}', so the page could not be cropped.";
            return result;
        }

        Merge(result, Trim(path, need[0].Value<double>(), need[1].Value<double>()));
        return result;
    }

    /// <summary>
    /// Crop every page to width x height millimetres, centred on the existing
    /// page. Returns a report merged into the plot result; never throws.
    /// </summary>
    private static JObject Trim(string path, double widthMm, double heightMm)
    {
        if (widthMm <= 0 || heightMm <= 0)
            return Report(false, "The requested size was not positive, so the page was left as plotted.");

        double targetW = widthMm / MmPerInch * PointsPerInch;
        double targetH = heightMm / MmPerInch * PointsPerInch;

        try
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            if (document.PageCount == 0)
                return Report(false, "The plotted PDF contains no pages.");

            // The page can be smaller than requested if the driver clipped it;
            // cropping to a larger box would add blank area rather than remove it.
            var first = document.Pages[0];
            if (first.MediaBox.Width < targetW - 1 || first.MediaBox.Height < targetH - 1)
            {
                return Report(false,
                    $"The plotted page ({Mm(first.MediaBox.Width):0.##} x " +
                    $"{Mm(first.MediaBox.Height):0.##} mm) is smaller than the requested " +
                    $"{widthMm:0.##} x {heightMm:0.##} mm, so it was left as plotted.");
            }

            double originX = 0, originY = 0;

            foreach (PdfPage page in document.Pages)
            {
                var box = page.MediaBox;
                double x = box.X1 + (box.Width - targetW) / 2.0;
                double y = box.Y1 + (box.Height - targetH) / 2.0;

                page.MediaBox = new PdfRectangle(new XRect(x, y, targetW, targetH));

                // A leftover CropBox at the old size wins in most viewers, so the
                // page would still display untrimmed. Drop all the derived boxes.
                foreach (var key in new[] { "/CropBox", "/TrimBox", "/ArtBox", "/BleedBox" })
                    page.Elements.Remove(key);

                if (ReferenceEquals(page, first)) { originX = x; originY = y; }
            }

            // Read everything needed for the report BEFORE saving: PdfSharp
            // invalidates the in-memory document once Save() has run, and
            // touching a page afterwards throws.
            int pageCount = document.PageCount;

            document.Save(path);

            return new JObject
            {
                ["trimmed"] = true,
                ["trimmed_size_mm"] = new JArray(Math.Round(widthMm, 2), Math.Round(heightMm, 2)),
                ["trim_box_mm"] = new JArray(
                    Math.Round(Mm(originX), 2), Math.Round(Mm(originY), 2),
                    Math.Round(widthMm, 2), Math.Round(heightMm, 2)),
                ["pages_trimmed"] = pageCount,
            };
        }
        catch (Exception ex)   // a failed crop must not fail the plot
        {
            return Report(false,
                $"The page could not be cropped ({ex.GetType().Name}: {ex.Message}). " +
                "The plot itself succeeded and the file is at the printer sheet size.");
        }
    }

    private static double Mm(double points) => points / PointsPerInch * MmPerInch;

    private static JObject Report(bool trimmed, string reason) =>
        new() { ["trimmed"] = trimmed, ["trim_error"] = reason };

    private static void Merge(JObject target, JObject extra)
    {
        foreach (var p in extra.Properties()) target[p.Name] = p.Value;
    }
}
