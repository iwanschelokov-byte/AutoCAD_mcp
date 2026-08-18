using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
// Both DatabaseServices and PlottingServices declare a PlotType enum. The one
// PlotSettingsValidator.SetPlotType takes lives in DatabaseServices (acdbmgd),
// which cannot reference accoremgd, so this is the only correct binding.
using PlotType = Autodesk.AutoCAD.DatabaseServices.PlotType;

namespace AutoCADMCPPlugin.Commands
{
    /// <summary>
    /// Shared plotting helpers: device/media lookup and the arithmetic that
    /// decides which sheet a given window fits on.
    /// </summary>
    internal static class PlotHelper
    {
        public const string DefaultDevice = "DWG To PDF.pc3";
        public const string DefaultStyleTable = "monochrome.ctb";

        /// <summary>A media size, in millimetres, as the device reports it.</summary>
        internal class Media
        {
            public string Canonical;
            public string Localized;
            public double Width;          // sheet, mm
            public double Height;         // sheet, mm
            public double PrintableWidth; // sheet minus margins, mm
            public double PrintableHeight;
            public double MarginLeft, MarginBottom, MarginRight, MarginTop;

            public JObject ToJson()
            {
                return new JObject
                {
                    ["canonical"] = Canonical,
                    ["name"] = Localized,
                    ["width_mm"] = Math.Round(Width, 2),
                    ["height_mm"] = Math.Round(Height, 2),
                    ["printable_width_mm"] = Math.Round(PrintableWidth, 2),
                    ["printable_height_mm"] = Math.Round(PrintableHeight, 2),
                    ["margins_mm"] = new JArray(
                        Math.Round(MarginLeft, 2), Math.Round(MarginBottom, 2),
                        Math.Round(MarginRight, 2), Math.Round(MarginTop, 2)),
                    ["full_bleed"] = MarginLeft + MarginBottom + MarginRight + MarginTop < 0.01
                };
            }
        }

        /// <summary>Every plot device AutoCAD currently knows about.</summary>
        public static List<string> Devices()
        {
            var list = new List<string>();
            try
            {
                StringCollection sc = PlotSettingsValidator.Current.GetPlotDeviceList();
                foreach (string s in sc) list.Add(s);
            }
            catch { }
            return list;
        }

        /// <summary>Every plot style table (.ctb/.stb) AutoCAD currently knows about.</summary>
        public static List<string> StyleTables()
        {
            var list = new List<string>();
            try
            {
                StringCollection sc = PlotSettingsValidator.Current.GetPlotStyleSheetList();
                foreach (string s in sc) list.Add(s);
            }
            catch { }
            return list;
        }

        /// <summary>
        /// Case-insensitive device lookup, so callers can write "dwg to pdf.pc3".
        /// Returns the exact spelling AutoCAD expects, or null.
        /// </summary>
        public static string ResolveDevice(string wanted)
        {
            if (string.IsNullOrWhiteSpace(wanted)) return null;
            string w = wanted.Trim();
            foreach (string d in Devices())
            {
                if (d.Equals(w, StringComparison.OrdinalIgnoreCase)) return d;
            }
            // Allow the ".pc3" to be left off.
            foreach (string d in Devices())
            {
                if (d.Equals(w + ".pc3", StringComparison.OrdinalIgnoreCase)) return d;
            }
            return null;
        }

        /// <summary>
        /// Every media the given (already configured) PlotSettings device offers,
        /// measured in millimetres. Measuring means applying each media in turn,
        /// which is why this takes a throw-away PlotSettings.
        /// </summary>
        public static List<Media> MediaList(PlotSettings ps)
        {
            var result = new List<Media>();
            PlotSettingsValidator v = PlotSettingsValidator.Current;

            StringCollection names;
            try { names = v.GetCanonicalMediaNameList(ps); }
            catch { return result; }

            foreach (string canonical in names)
            {
                var m = new Media { Canonical = canonical, Localized = canonical };
                try { m.Localized = v.GetLocaleMediaName(ps, canonical); } catch { }
                try
                {
                    v.SetCanonicalMediaName(ps, canonical);
                    Point2d size = ps.PlotPaperSize;
                    Extents2d mar = ps.PlotPaperMargins;
                    m.Width = size.X;
                    m.Height = size.Y;
                    m.MarginLeft = mar.MinPoint.X;
                    m.MarginBottom = mar.MinPoint.Y;
                    m.MarginRight = mar.MaxPoint.X;
                    m.MarginTop = mar.MaxPoint.Y;
                    m.PrintableWidth = m.Width - m.MarginLeft - m.MarginRight;
                    m.PrintableHeight = m.Height - m.MarginBottom - m.MarginTop;
                }
                catch { continue; }
                result.Add(m);
            }
            return result;
        }

        /// <summary>
        /// True when a needW x needH image fits the media's printable area,
        /// possibly after a 90 degree rotation. <paramref name="rotate"/> reports
        /// which of the two it was; a tie prefers no rotation.
        /// </summary>
        public static bool Fits(Media m, double needW, double needH, out bool rotate, out double waste)
        {
            const double eps = 1e-6;
            double pw = m.PrintableWidth, ph = m.PrintableHeight;

            bool straight = needW <= pw + eps && needH <= ph + eps;
            bool turned = needH <= pw + eps && needW <= ph + eps;

            rotate = false;
            waste = double.MaxValue;

            if (!straight && !turned) return false;

            double wasteStraight = straight ? (pw * ph - needW * needH) : double.MaxValue;
            double wasteTurned = turned ? (pw * ph - needW * needH) : double.MaxValue;

            // Same area either way; prefer the orientation whose aspect ratio is
            // closest to the content, which is what a human would choose.
            if (straight && turned)
            {
                double dStraight = Math.Abs((pw / Math.Max(ph, 1e-9)) - (needW / Math.Max(needH, 1e-9)));
                double dTurned = Math.Abs((pw / Math.Max(ph, 1e-9)) - (needH / Math.Max(needW, 1e-9)));
                rotate = dTurned < dStraight;
                waste = rotate ? wasteTurned : wasteStraight;
                return true;
            }

            rotate = turned;
            waste = rotate ? wasteTurned : wasteStraight;
            return true;
        }

        /// <summary>
        /// Parse "1=1", "1:100", "2" or "fit" into a paper-units / drawing-units
        /// ratio. Returns false for "fit", which has no fixed ratio.
        /// </summary>
        public static bool ParseScale(string text, out double numerator, out double denominator, out string error)
        {
            numerator = 1; denominator = 1; error = null;
            if (string.IsNullOrWhiteSpace(text)) return true;

            string s = text.Trim();
            if (s.Equals("fit", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("scaletofit", StringComparison.OrdinalIgnoreCase))
                return false;

            char[] seps = { '=', ':', '/' };
            int i = s.IndexOfAny(seps);
            if (i > 0)
            {
                string a = s.Substring(0, i).Trim();
                string b = s.Substring(i + 1).Trim();
                if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double na) &&
                    double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double nb) &&
                    na > 0 && nb > 0)
                {
                    numerator = na; denominator = nb; return true;
                }
                error = $"Cannot read scale '{text}'. Use \"1=1\", \"1:100\", a number, or \"fit\".";
                return true;
            }

            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && n > 0)
            {
                numerator = n; denominator = 1; return true;
            }

            error = $"Cannot read scale '{text}'. Use \"1=1\", \"1:100\", a number, or \"fit\".";
            return true;
        }

        /// <summary>Read a 4-number window from JSON: [x1,y1,x2,y2] or {x1,y1,x2,y2}.</summary>
        public static bool ReadWindow(JToken token, out double x1, out double y1, out double x2, out double y2)
        {
            x1 = y1 = x2 = y2 = 0;
            if (token == null) return false;

            if (token is JArray arr && arr.Count >= 4)
            {
                try
                {
                    x1 = arr[0].Value<double>(); y1 = arr[1].Value<double>();
                    x2 = arr[2].Value<double>(); y2 = arr[3].Value<double>();
                }
                catch { return false; }
            }
            else if (token is JObject o)
            {
                try
                {
                    x1 = (o["x1"] ?? o["min_x"]).Value<double>();
                    y1 = (o["y1"] ?? o["min_y"]).Value<double>();
                    x2 = (o["x2"] ?? o["max_x"]).Value<double>();
                    y2 = (o["y2"] ?? o["max_y"]).Value<double>();
                }
                catch { return false; }
            }
            else return false;

            if (x2 < x1) { double t = x1; x1 = x2; x2 = t; }
            if (y2 < y1) { double t = y1; y1 = y2; y2 = t; }
            return x2 > x1 && y2 > y1;
        }
    }

    /// <summary>
    /// What the current drawing can be plotted to: devices, the media each one
    /// offers (with real millimetre sizes), and the available plot style tables.
    ///
    /// Without this, plotting is guesswork. Canonical media names are not
    /// guessable — the A1 sheet a Russian AutoCAD shows as
    /// "ISO A1 (841.00 x 594.00 мм)" is called
    /// "ISO_full_bleed_A1_(841.00_x_594.00_MM)" in the API — and the printable
    /// area (sheet minus device margins) is what actually decides whether a
    /// drawing frame gets clipped. Both are reported here.
    /// </summary>
    public class PlotDevicesCommand : AcadCommand
    {
        public override string MethodName => "plot_devices";

        public override CommandResult Execute(JObject parameters)
        {
            // This has to be the very first statement, before a single line of
            // the plot API is touched.
            //
            // Everything this command reads - the device list, the style table
            // list, the media list - comes out of PlotSettingsValidator.Current,
            // and "Current" means current *for the active document*. With no
            // drawing open there is nothing for it to be current of, and the
            // call faults down in the unmanaged plot configuration code. That
            // takes the whole AutoCAD process with it: it is not an exception,
            // so the try/catch inside PlotHelper.Devices() never sees it and
            // the caller just watches the socket die.
            //
            // An earlier build listed the devices first and only checked for a
            // document further down, on the theory that a plain list of driver
            // names could not possibly need a drawing. It can, and that theory
            // cost two AutoCAD crashes.
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return CommandResult.Fail(
                    "No drawing is open. Plot devices, style tables and paper sizes are all read " +
                    "through AutoCAD's plot configuration, which only exists while a drawing is " +
                    "open - asking for them with no drawing open would terminate AutoCAD. Create " +
                    "a drawing with drawing_new, or open one, and call this again.");

            // Reading any of this is less passive than it looks: every sheet has
            // to be applied to a PlotSettings with SetCanonicalMediaName before
            // its size can be read back, which walks the same plot configuration
            // machinery an actual plot uses. So none of it may run while a plot
            // is in progress, and all of it runs under a document lock - this
            // command arrives in application context, not document context.
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                return CommandResult.Fail(
                    "AutoCAD is plotting right now, and reading the plot configuration walks the " +
                    "same machinery. Wait for that job to finish and try again.");

            string asked = parameters?["device"]?.ToString();
            string askedPlotter = parameters?["plotter"]?.ToString();
            string filter = parameters?["filter"]?.ToString();

            // 'plotter' is a synonym for 'device'. Some MCP hosts route calls
            // through a bridge that reserves the argument name "device" for
            // itself and consumes it before the tool is ever reached, so the
            // command has to be reachable under a second name.
            string device = !string.IsNullOrWhiteSpace(asked) ? asked : askedPlotter;

            var result = new JObject();

            // Echo what actually arrived. A caller who passes 'device' and gets
            // back an empty 'requested_device' knows the argument was lost on the
            // way in rather than misunderstood here.
            result["requested_device"] = asked ?? "";
            result["requested_plotter"] = askedPlotter ?? "";
            result["requested_filter"] = filter ?? "";

            try
            {
                using (doc.LockDocument())
                {
                    result["devices"] = new JArray(PlotHelper.Devices().ToArray());
                    result["style_tables"] = new JArray(PlotHelper.StyleTables().ToArray());

                    bool fellBack = false;
                    if (string.IsNullOrWhiteSpace(device))
                    {
                        // No device named: still answer the question people
                        // actually have by listing the default PDF driver's sheets.
                        device = PlotHelper.DefaultDevice;
                        fellBack = true;
                        result["message"] =
                            "Neither 'device' nor 'plotter' arrived, so these are the paper sizes of " +
                            PlotHelper.DefaultDevice + ". Pass 'device' to ask about another one. If " +
                            "you did pass 'device' and it still came back empty in 'requested_device' " +
                            "above, the host consumed the argument before the tool saw it - pass the " +
                            "same value as 'plotter', which means exactly the same thing here.";
                    }

                    string resolved = PlotHelper.ResolveDevice(device);
                    if (resolved == null && fellBack)
                    {
                        result["message"] = "Pass 'device' (or 'plotter') to also list that device's paper sizes.";
                        return CommandResult.Ok(result);
                    }
                    if (resolved == null)
                        return CommandResult.Fail(
                            $"Unknown plot device '{device}'. Call plot_devices with no arguments to list them.");

                    result["device"] = resolved;

                    using (var ps = new PlotSettings(true))
                    {
                        PlotSettingsValidator v = PlotSettingsValidator.Current;
                        v.SetPlotConfigurationName(ps, resolved, null);
                        v.RefreshLists(ps);

                        var media = new JArray();
                        int total = 0;
                        foreach (PlotHelper.Media m in PlotHelper.MediaList(ps))
                        {
                            total++;
                            if (!string.IsNullOrWhiteSpace(filter) &&
                                m.Canonical.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                                (m.Localized ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                            media.Add(m.ToJson());
                        }

                        result["media"] = media;
                        result["media_count"] = media.Count;
                        result["media_total"] = total;
                    }
                }
            }
            catch (System.Exception ex)
            {
                return CommandResult.Fail($"Could not read the plot configuration: {ex.Message}");
            }

            return CommandResult.Ok(result);
        }
    }

    /// <summary>
    /// Plot the drawing to a PDF (or to any other configured device that can
    /// print to a file) and wait for the file to be written.
    ///
    /// The previous implementation sent "._-EXPORTPDF &lt;path&gt;" with
    /// SendStringToExecute. EXPORTPDF has no command-line version, so AutoCAD
    /// reported an unknown command and then took the quoted path as the *next*
    /// command — the caller saw a cheerful "PDF export command sent" and no
    /// file. Nothing about the sheet could be controlled either: no window, no
    /// scale, no paper size.
    ///
    /// This uses the PlottingServices API instead. It runs synchronously, so the
    /// result reports the real outcome and the real file size; it does not go
    /// through the command line, so it is immune to UI language and to the fact
    /// that -PLOT asks different questions in model space than in a layout; and
    /// it can enumerate media, so "paper": "auto" can pick the smallest sheet the
    /// window actually fits on.
    ///
    /// Parameters (all optional except output_path):
    ///   output_path  where to write. Relative paths resolve next to the drawing.
    ///   device       plot device, default "DWG To PDF.pc3".
    ///   plotter      synonym for device, for hosts that eat "device".
    ///   paper        canonical or localized media name, or "auto" (default) to
    ///                pick the smallest sheet whose printable area fits.
    ///   style_table  plot style table, default "monochrome.ctb"; "none" to keep
    ///                whatever the layout already uses.
    ///   area         extents | window | display | layout | limits.
    ///                Defaults to window when 'window' is given, else extents.
    ///   window       [x1,y1,x2,y2] in drawing units.
    ///   scale        "1=1" (default), "1:100", a number, or "fit".
    ///   offset       "center" (default) or [dx,dy] in millimetres.
    ///   orientation  auto (default) | portrait | landscape.
    ///   layout       layout to plot; default the current one.
    ///   lineweights  honour lineweights, default true.
    ///   overwrite    overwrite an existing file, default true.
    ///
    /// A note on non-standard sheet sizes: AutoCAD can only plot to a paper size
    /// the device defines, so a 840x594 sheet is produced by plotting 1:1 and
    /// centred on full-bleed A1 (841x594) and then trimming the PDF MediaBox.
    /// The trimming itself is not done here — it needs a PDF library, and this
    /// assembly deliberately has no dependency beyond AutoCAD and Newtonsoft —
    /// so the result reports "required_mm", the window multiplied by the scale,
    /// which is exactly the box to crop to. The bundled MCP server does that
    /// crop with PdfSharp and reports whether it worked; a caller talking to this
    /// socket directly gets the number and can crop it itself.
    /// </summary>
    public class PlotToPdfCommand : AcadCommand
    {
        public override string MethodName => "plot_to_pdf";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string outputPath = parameters?["output_path"]?.ToString();
            if (string.IsNullOrWhiteSpace(outputPath))
                return CommandResult.Fail("Parameter 'output_path' is required");

            // --- output file -------------------------------------------------
            try
            {
                if (!Path.IsPathRooted(outputPath))
                {
                    string baseDir = null;
                    try { baseDir = Path.GetDirectoryName(doc.Database?.Filename ?? ""); } catch { }
                    if (string.IsNullOrEmpty(baseDir)) baseDir = Environment.CurrentDirectory;
                    outputPath = Path.Combine(baseDir, outputPath);
                }
                outputPath = Path.GetFullPath(outputPath);
            }
            catch (System.Exception ex)
            {
                return CommandResult.Fail($"Bad output_path '{outputPath}': {ex.Message}");
            }

            bool overwrite = parameters?["overwrite"]?.Value<bool>() ?? true;
            if (File.Exists(outputPath) && !overwrite)
                return CommandResult.Fail($"File already exists: {outputPath}. Pass overwrite=true to replace it.");

            try
            {
                string dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch (System.Exception ex)
            {
                return CommandResult.Fail($"Cannot create output folder: {ex.Message}");
            }

            // --- device ------------------------------------------------------
            // 'plotter' is a synonym, for hosts whose bridge reserves the
            // argument name "device" and eats it on the way in.
            string deviceWanted = parameters?["device"]?.ToString();
            if (string.IsNullOrWhiteSpace(deviceWanted))
                deviceWanted = parameters?["plotter"]?.ToString();
            if (string.IsNullOrWhiteSpace(deviceWanted)) deviceWanted = PlotHelper.DefaultDevice;
            string device = PlotHelper.ResolveDevice(deviceWanted);
            if (device == null)
                return CommandResult.Fail(
                    $"Unknown plot device '{deviceWanted}'. Call plot_devices to list what is installed.");

            // --- scale -------------------------------------------------------
            string scaleText = parameters?["scale"]?.ToString();
            bool fitToPaper = !PlotHelper.ParseScale(scaleText, out double scaleNum, out double scaleDen, out string scaleError);
            if (scaleError != null) return CommandResult.Fail(scaleError);

            // --- area / window -----------------------------------------------
            JToken windowToken = parameters?["window"];
            bool haveWindow = PlotHelper.ReadWindow(windowToken, out double wx1, out double wy1, out double wx2, out double wy2);
            if (windowToken != null && !haveWindow)
                return CommandResult.Fail("Parameter 'window' must be [x1, y1, x2, y2] with x2>x1 and y2>y1.");

            string areaText = parameters?["area"]?.ToString();
            if (string.IsNullOrWhiteSpace(areaText)) areaText = haveWindow ? "window" : "extents";

            PlotType plotType;
            switch (areaText.Trim().ToLowerInvariant())
            {
                case "window": plotType = PlotType.Window; break;
                case "extents": plotType = PlotType.Extents; break;
                case "display": plotType = PlotType.Display; break;
                case "layout": plotType = PlotType.Layout; break;
                case "limits": plotType = PlotType.Limits; break;
                default:
                    return CommandResult.Fail(
                        $"Unknown area '{areaText}'. Use extents, window, display, layout or limits.");
            }
            if (plotType == PlotType.Window && !haveWindow)
                return CommandResult.Fail("area=\"window\" needs 'window': [x1, y1, x2, y2].");

            // --- everything else ----------------------------------------------
            string paperWanted = parameters?["paper"]?.ToString();
            string styleWanted = parameters?["style_table"]?.ToString();
            if (styleWanted == null) styleWanted = PlotHelper.DefaultStyleTable;
            string orientation = (parameters?["orientation"]?.ToString() ?? "auto").Trim().ToLowerInvariant();
            bool lineweights = parameters?["lineweights"]?.Value<bool>() ?? true;
            string layoutWanted = parameters?["layout"]?.ToString();

            JToken offsetToken = parameters?["offset"];
            bool centered = true;
            double offX = 0, offY = 0;
            if (offsetToken != null)
            {
                if (offsetToken.Type == JTokenType.String)
                {
                    string s = offsetToken.ToString().Trim();
                    if (!s.Equals("center", StringComparison.OrdinalIgnoreCase) &&
                        !s.Equals("centre", StringComparison.OrdinalIgnoreCase))
                        return CommandResult.Fail("Parameter 'offset' must be \"center\" or [dx, dy] in millimetres.");
                }
                else if (offsetToken is JArray oa && oa.Count >= 2)
                {
                    try { offX = oa[0].Value<double>(); offY = oa[1].Value<double>(); }
                    catch { return CommandResult.Fail("Parameter 'offset' must be \"center\" or [dx, dy] in millimetres."); }
                    centered = false;
                }
                else return CommandResult.Fail("Parameter 'offset' must be \"center\" or [dx, dy] in millimetres.");
            }

            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                return CommandResult.Fail("AutoCAD is already plotting. Wait for that job to finish and try again.");

            // Background plotting would return before the file exists, which is
            // exactly the failure mode this command was written to remove.
            object savedBgPlot = null;
            try { savedBgPlot = Application.GetSystemVariable("BACKGROUNDPLOT"); } catch { }
            try { Application.SetSystemVariable("BACKGROUNDPLOT", 0); } catch { }

            string savedLayout = null;
            var info = new JObject();

            // Which drawing is actually going onto the paper. plot_to_pdf always
            // uses MdiActiveDocument, and with several drawings open that is not
            // necessarily the one the caller had in mind - so say it out loud
            // instead of leaving the caller to infer it from the PDF.
            try
            {
                info["document"] = doc.Name;
                string dwgPath = null;
                try { dwgPath = doc.Database?.Filename; } catch { }
                if (!string.IsNullOrEmpty(dwgPath)) info["document_path"] = dwgPath;
                try
                {
                    object dbmod = Application.GetSystemVariable("DBMOD");
                    if (dbmod != null) info["unsaved_changes"] = Convert.ToInt32(dbmod) != 0;
                }
                catch { }

                int openCount = 0;
                foreach (Document od in Application.DocumentManager) { if (od != null) openCount++; }
                info["documents_open"] = openCount;
                if (openCount > 1)
                    info["document_note"] =
                        "Plotted the ACTIVE drawing. " + openCount + " drawings are open - if this is " +
                        "not the one you meant, activate it first (drawing_open on its path brings it " +
                        "to the front) and plot again.";
            }
            catch { }

            try
            {
                LayoutManager lm = LayoutManager.Current;
                if (!string.IsNullOrWhiteSpace(layoutWanted))
                {
                    try { savedLayout = lm.CurrentLayout; } catch { }
                    if (savedLayout == null ||
                        !layoutWanted.Equals(savedLayout, StringComparison.OrdinalIgnoreCase))
                    {
                        try { lm.CurrentLayout = layoutWanted; }
                        catch (System.Exception ex)
                        {
                            return CommandResult.Fail($"No layout named '{layoutWanted}': {ex.Message}");
                        }
                    }
                    else savedLayout = null; // already there, nothing to restore
                }

                string error = PlotOnce(doc, device, paperWanted, styleWanted, plotType,
                                        wx1, wy1, wx2, wy2, fitToPaper, scaleNum, scaleDen,
                                        centered, offX, offY, orientation, lineweights,
                                        outputPath, info);
                if (error != null) return CommandResult.Fail(error);
            }
            catch (System.Exception ex)
            {
                return CommandResult.Fail($"Plot failed: {ex.Message}");
            }
            finally
            {
                if (savedLayout != null)
                {
                    try { LayoutManager.Current.CurrentLayout = savedLayout; } catch { }
                }
                if (savedBgPlot != null)
                {
                    try { Application.SetSystemVariable("BACKGROUNDPLOT", savedBgPlot); } catch { }
                }
            }

            if (!File.Exists(outputPath))
                return CommandResult.Fail(
                    "AutoCAD reported no error but no file was written to " + outputPath +
                    ". Check that the device can plot to file and that the path is writable.");

            long size = 0;
            try { size = new FileInfo(outputPath).Length; } catch { }

            info["output_path"] = outputPath;
            info["file_size"] = size;
            string plottedDoc = info["document"] == null ? "the active drawing" : info["document"].ToString();
            string plottedLayout = info["layout"] == null ? null : info["layout"].ToString();
            info["message"] =
                "Plotted '" + plottedDoc + "'" +
                (plottedLayout == null ? "" : " (layout '" + plottedLayout + "')") +
                $" to {outputPath} ({size:N0} bytes).";
            return CommandResult.Ok(info);
        }

        /// <summary>
        /// Configure a PlotSettings and run one page through the publish engine.
        /// Returns null on success, or a message describing what went wrong.
        /// </summary>
        private static string PlotOnce(
            Document doc, string device, string paperWanted, string styleWanted,
            PlotType plotType, double wx1, double wy1, double wx2, double wy2,
            bool fitToPaper, double scaleNum, double scaleDen,
            bool centered, double offX, double offY, string orientation,
            bool lineweights, string outputPath, JObject info)
        {
            Database db = doc.Database;
            PlotSettingsValidator v = PlotSettingsValidator.Current;

            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Layout layout = null;
                try
                {
                    var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                    layout = (Layout)tr.GetObject(space.LayoutId, OpenMode.ForRead);
                }
                catch { }
                if (layout == null) return "Could not read the current layout.";

                info["layout"] = layout.LayoutName;
                info["model_space"] = layout.ModelType;

                using (var ps = new PlotSettings(layout.ModelType))
                {
                    ps.CopyFrom(layout);

                    // 1. Device first: the media and style lists depend on it.
                    v.SetPlotConfigurationName(ps, device, null);
                    v.RefreshLists(ps);
                    info["device"] = device;

                    // 2. Area. The window has to be set before the type.
                    if (plotType == PlotType.Window)
                    {
                        v.SetPlotWindowArea(ps, new Extents2d(wx1, wy1, wx2, wy2));
                        info["window"] = new JArray(wx1, wy1, wx2, wy2);
                    }
                    v.SetPlotType(ps, plotType);
                    info["area"] = plotType.ToString().ToLowerInvariant();

                    // 3. Paper. "auto" needs the size the content will occupy,
                    //    which only exists once the scale is known.
                    double needW = 0, needH = 0;
                    bool canSize = plotType == PlotType.Window && !fitToPaper;
                    if (canSize)
                    {
                        double f = scaleNum / scaleDen;
                        needW = (wx2 - wx1) * f;
                        needH = (wy2 - wy1) * f;
                        info["required_mm"] = new JArray(Math.Round(needW, 2), Math.Round(needH, 2));
                    }

                    string mediaError = ChooseMedia(v, ps, paperWanted, canSize, needW, needH,
                                                    orientation, info, out bool rotate);
                    if (mediaError != null) return mediaError;

                    // 4. Units and rotation.
                    v.SetPlotPaperUnits(ps, PlotPaperUnit.Millimeters);
                    PlotRotation rot = rotate ? PlotRotation.Degrees090 : PlotRotation.Degrees000;
                    v.SetPlotRotation(ps, rot);
                    info["rotation"] = rotate ? 90 : 0;

                    // 5. Scale.
                    if (fitToPaper)
                    {
                        v.SetUseStandardScale(ps, true);
                        v.SetStdScaleType(ps, StdScaleType.ScaleToFit);
                        info["scale"] = "fit";
                    }
                    else
                    {
                        v.SetUseStandardScale(ps, false);
                        v.SetCustomPrintScale(ps, new CustomScale(scaleNum, scaleDen));
                        info["scale"] = scaleDen == 1
                            ? scaleNum.ToString("0.####", CultureInfo.InvariantCulture) + "=1"
                            : scaleNum.ToString("0.####", CultureInfo.InvariantCulture) + "=" +
                              scaleDen.ToString("0.####", CultureInfo.InvariantCulture);
                    }

                    // 6. Origin.
                    if (centered)
                    {
                        v.SetPlotCentered(ps, true);
                        info["offset"] = "center";
                    }
                    else
                    {
                        v.SetPlotOrigin(ps, new Point2d(offX, offY));
                        info["offset"] = new JArray(offX, offY);
                    }

                    // 7. Plot styles.
                    if (!string.IsNullOrWhiteSpace(styleWanted) &&
                        !styleWanted.Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        string style = ResolveStyleTable(styleWanted);
                        if (style == null)
                            return $"Unknown plot style table '{styleWanted}'. Call plot_devices to list them.";
                        try
                        {
                            v.SetCurrentStyleSheet(ps, style);
                            ps.PlotPlotStyles = true;
                            ps.ShowPlotStyles = true;
                            info["style_table"] = style;
                        }
                        catch (System.Exception ex)
                        {
                            return $"Could not apply plot style table '{style}': {ex.Message}";
                        }
                    }

                    ps.PrintLineweights = lineweights;
                    ps.ScaleLineweights = false;
                    info["lineweights"] = lineweights;

                    // 8. Run it.
                    var pi = new PlotInfo
                    {
                        Layout = layout.ObjectId,
                        OverrideSettings = ps
                    };
                    var piv = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled };
                    try { piv.Validate(pi); }
                    catch (System.Exception ex)
                    {
                        return $"AutoCAD rejected these plot settings: {ex.Message}";
                    }

                    string docName;
                    try { docName = Path.GetFileNameWithoutExtension(doc.Name); }
                    catch { docName = "drawing"; }
                    if (string.IsNullOrEmpty(docName)) docName = "drawing";

                    PlotEngine pe = PlotFactory.CreatePublishEngine();
                    try
                    {
                        pe.BeginPlot(null, null);
                        pe.BeginDocument(pi, docName, null, 1, true, outputPath);
                        pe.BeginPage(new PlotPageInfo(), pi, true, null);
                        pe.BeginGenerateGraphics(null);
                        pe.EndGenerateGraphics(null);
                        pe.EndPage(null);
                        pe.EndDocument(null);
                        pe.EndPlot(null);
                    }
                    finally
                    {
                        try { pe.Destroy(); } catch { }
                    }
                }

                // Nothing here changes the drawing, but committing keeps the
                // transaction bookkeeping honest.
                tr.Commit();
            }

            return null;
        }

        /// <summary>
        /// Apply the requested media, or find the smallest one that fits.
        /// Reports the media actually used, and whether the page has to turn.
        /// </summary>
        private static string ChooseMedia(
            PlotSettingsValidator v, PlotSettings ps, string paperWanted,
            bool canSize, double needW, double needH, string orientation,
            JObject info, out bool rotate)
        {
            rotate = false;
            bool auto = string.IsNullOrWhiteSpace(paperWanted) ||
                        paperWanted.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);

            List<PlotHelper.Media> media = PlotHelper.MediaList(ps);
            if (media.Count == 0)
                return "The plot device reports no paper sizes.";

            PlotHelper.Media chosen = null;

            if (!auto)
            {
                string want = paperWanted.Trim();
                foreach (PlotHelper.Media m in media)
                {
                    if (m.Canonical.Equals(want, StringComparison.OrdinalIgnoreCase) ||
                        (m.Localized ?? "").Equals(want, StringComparison.OrdinalIgnoreCase))
                    { chosen = m; break; }
                }
                if (chosen == null)
                {
                    // Underscores in canonical names trip people up; be forgiving.
                    string loose = want.Replace(' ', '_');
                    foreach (PlotHelper.Media m in media)
                    {
                        if (m.Canonical.Equals(loose, StringComparison.OrdinalIgnoreCase))
                        { chosen = m; break; }
                    }
                }
                if (chosen == null)
                    return $"This device has no paper size called '{paperWanted}'. " +
                           "Call plot_devices with this device to list the canonical names.";
            }
            else if (canSize)
            {
                double best = double.MaxValue;
                bool bestRotate = false;
                foreach (PlotHelper.Media m in media)
                {
                    if (!PlotHelper.Fits(m, needW, needH, out bool r, out double waste)) continue;
                    // Smallest sheet wins; between two sheets of the same size
                    // (a bordered one and its full-bleed twin) the one with the
                    // larger printable area wins, because it wastes less.
                    double area = m.Width * m.Height;
                    double score = area * 1000.0 - m.PrintableWidth * m.PrintableHeight;

                    bool better;
                    if (chosen == null) better = true;
                    else if (score < best - 1e-9) better = true;
                    else if (score > best + 1e-9) better = false;
                    // Every device that defines A1 defines it twice, portrait and
                    // landscape, and the two score identically. Without this the
                    // winner is whichever the driver happens to list first, so a
                    // landscape window lands on a portrait sheet turned 90 degrees.
                    else better = bestRotate && !r;

                    if (better)
                    {
                        best = score; chosen = m; bestRotate = r;
                    }
                }
                if (chosen == null)
                    return $"No paper size on this device fits {needW:0.##} x {needH:0.##} mm. " +
                           "Plot at a smaller scale, use scale=\"fit\", or name a paper explicitly.";
                rotate = bestRotate;
                info["paper_auto"] = true;
            }
            else
            {
                // "auto" but nothing to measure against (fit-to-paper, or a
                // non-window area): keep whatever the layout already had.
                string current = null;
                try { current = ps.CanonicalMediaName; } catch { }
                foreach (PlotHelper.Media m in media)
                    if (m.Canonical.Equals(current, StringComparison.OrdinalIgnoreCase)) { chosen = m; break; }
                if (chosen == null) chosen = media[0];
                info["paper_auto"] = false;
            }

            try { v.SetCanonicalMediaName(ps, chosen.Canonical); }
            catch (System.Exception ex)
            {
                return $"Could not select paper '{chosen.Canonical}': {ex.Message}";
            }

            if (!auto || !canSize)
            {
                // Explicit paper: honour an explicit orientation, otherwise turn
                // the page only if that is what makes the content fit.
                if (orientation == "portrait") rotate = false;
                else if (orientation == "landscape") rotate = true;
                else if (canSize) PlotHelper.Fits(chosen, needW, needH, out rotate, out _);
            }
            else
            {
                if (orientation == "portrait") rotate = false;
                else if (orientation == "landscape") rotate = true;
            }

            info["paper"] = chosen.Canonical;
            info["paper_name"] = chosen.Localized;
            info["paper_size_mm"] = new JArray(Math.Round(chosen.Width, 2), Math.Round(chosen.Height, 2));
            info["printable_area_mm"] = new JArray(
                Math.Round(chosen.PrintableWidth, 2), Math.Round(chosen.PrintableHeight, 2));
            info["margins_mm"] = new JArray(
                Math.Round(chosen.MarginLeft, 2), Math.Round(chosen.MarginBottom, 2),
                Math.Round(chosen.MarginRight, 2), Math.Round(chosen.MarginTop, 2));

            if (canSize)
            {
                double pw = rotate ? chosen.PrintableHeight : chosen.PrintableWidth;
                double ph = rotate ? chosen.PrintableWidth : chosen.PrintableHeight;
                if (needW > pw + 1e-6 || needH > ph + 1e-6)
                {
                    info["warning"] =
                        $"Content is {needW:0.##} x {needH:0.##} mm but the printable area is " +
                        $"{pw:0.##} x {ph:0.##} mm; the plot will be clipped.";
                }
            }

            return null;
        }

        private static string ResolveStyleTable(string wanted)
        {
            string w = wanted.Trim();
            foreach (string s in PlotHelper.StyleTables())
                if (s.Equals(w, StringComparison.OrdinalIgnoreCase)) return s;
            foreach (string s in PlotHelper.StyleTables())
                if (s.Equals(w + ".ctb", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals(w + ".stb", StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }
    }
}
