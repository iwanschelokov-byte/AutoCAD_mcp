using System;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using static AutoCADMCPPlugin.Commands.EntityHelper;

namespace AutoCADMCPPlugin.Commands
{
    // =========================================================================
    // measure_text / measure_texts
    //
    // Browser callers building CAD schedules need ground-truth text widths
    // to size table columns correctly. SHX fonts (Romans, Standard,
    // Romantic, ...) have proportional, hand-tuned glyph widths — a
    // pixel-based JS estimate diverges noticeably from the real render
    // and leads to header labels overflowing their merged spans.
    //
    // Both commands work by appending a DBText probe to model space,
    // reading `GeometricExtents`, then aborting the transaction so the
    // probe never lands in the user's drawing. The batch variant
    // (`measure_texts`) wraps the whole list in a single transaction
    // to keep the per-text cost down for callers measuring 100+
    // strings before they emit geometry.
    // =========================================================================

    public class MeasureTextCommand : AcadCommand
    {
        public override string MethodName => "measure_text";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string text = parameters["text"]?.ToString() ?? "";
            double height = parameters["height"]?.Value<double>() ?? 2.5;
            string styleName = parameters["style"]?.ToString();
            double widthFactor = parameters["width_factor"]?.Value<double>() ?? 1.0;

            Database db = doc.Database;
            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                JObject box = MeasureProbe(tr, db, text, height, styleName, widthFactor);
                tr.Abort();
                return CommandResult.Ok(box);
            }
        }

        /// <summary>
        /// Append a DBText probe, read its bounding box, return the box.
        /// Caller is responsible for aborting / committing the transaction.
        /// </summary>
        internal static JObject MeasureProbe(
            Transaction tr,
            Database db,
            string text,
            double height,
            string styleName,
            double widthFactor)
        {
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            DBText probe = new DBText
            {
                Position = new Point3d(0, 0, 0),
                TextString = text,
                Height = height > 0 ? height : 2.5,
                WidthFactor = widthFactor > 0 ? widthFactor : 1.0,
            };

            if (!string.IsNullOrEmpty(styleName))
            {
                TextStyleTable tst = (TextStyleTable)tr.GetObject(
                    db.TextStyleTableId, OpenMode.ForRead);
                if (tst.Has(styleName))
                {
                    probe.TextStyleId = tst[styleName];
                }
            }

            ms.AppendEntity(probe);
            tr.AddNewlyCreatedDBObject(probe, true);

            double width = 0;
            double measuredHeight = height;
            try
            {
                Extents3d ext = probe.GeometricExtents;
                width = ext.MaxPoint.X - ext.MinPoint.X;
                measuredHeight = ext.MaxPoint.Y - ext.MinPoint.Y;
            }
            catch
            {
                // Empty / whitespace-only text — keep zero width.
            }

            return new JObject
            {
                ["text"] = text,
                ["width"] = width,
                ["height"] = measuredHeight,
            };
        }
    }

    public class MeasureTextsCommand : AcadCommand
    {
        public override string MethodName => "measure_texts";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            JArray items = parameters["items"] as JArray;
            if (items == null || items.Count == 0)
                return CommandResult.Fail("Parameter 'items' array (>=1) required");

            // Cap the batch to a sane size so a runaway client doesn't
            // pin AutoCAD's main thread.
            const int MAX_BATCH = 2000;
            if (items.Count > MAX_BATCH)
                return CommandResult.Fail($"items length {items.Count} exceeds cap {MAX_BATCH}");

            Database db = doc.Database;
            JArray results = new JArray();

            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (JToken item in items)
                {
                    string text = item["text"]?.ToString() ?? "";
                    double height = item["height"]?.Value<double>() ?? 2.5;
                    string styleName = item["style"]?.ToString();
                    double widthFactor = item["width_factor"]?.Value<double>() ?? 1.0;

                    try
                    {
                        results.Add(
                            MeasureTextCommand.MeasureProbe(
                                tr, db, text, height, styleName, widthFactor));
                    }
                    catch (Exception ex)
                    {
                        // Don't abort the whole batch on one bad item —
                        // return a zero-width entry with an error tag so
                        // the caller can fall back to its own estimator.
                        results.Add(new JObject
                        {
                            ["text"] = text,
                            ["width"] = 0,
                            ["height"] = height,
                            ["error"] = ex.Message,
                        });
                    }
                }

                tr.Abort();
            }

            return CommandResult.Ok(new JObject
            {
                ["results"] = results,
            });
        }
    }
}
