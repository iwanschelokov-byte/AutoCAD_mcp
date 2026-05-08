using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using static AutoCADMCPPlugin.Commands.EntityHelper;

namespace AutoCADMCPPlugin.Commands
{
    public class PurgeDrawingCommand : ICommand
    {
        public string MethodName => "purge_drawing";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            Database db = doc.Database;
            int totalPurged = 0;

            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectIdCollection idsToCheck = new ObjectIdCollection();

                // Collect purgeable items from all symbol tables
                foreach (var tableId in new[] { db.LayerTableId, db.BlockTableId, db.TextStyleTableId,
                    db.DimStyleTableId, db.LinetypeTableId, db.RegAppTableId })
                {
                    SymbolTable st = (SymbolTable)tr.GetObject(tableId, OpenMode.ForRead);
                    foreach (ObjectId id in st)
                        idsToCheck.Add(id);
                }

                // Purge repeatedly until nothing left
                for (int pass = 0; pass < 5; pass++)
                {
                    ObjectIdCollection purgeable = new ObjectIdCollection();
                    db.Purge(idsToCheck);
                    // After Purge, idsToCheck contains only the purgeable ones
                    if (idsToCheck.Count == 0) break;

                    foreach (ObjectId id in idsToCheck)
                    {
                        try
                        {
                            DBObject obj = tr.GetObject(id, OpenMode.ForWrite);
                            obj.Erase();
                            totalPurged++;
                        }
                        catch { }
                    }

                    // Rebuild for next pass
                    idsToCheck.Clear();
                    foreach (var tableId in new[] { db.LayerTableId, db.BlockTableId, db.TextStyleTableId,
                        db.DimStyleTableId, db.LinetypeTableId })
                    {
                        SymbolTable st = (SymbolTable)tr.GetObject(tableId, OpenMode.ForRead);
                        foreach (ObjectId id in st)
                            idsToCheck.Add(id);
                    }
                }

                tr.Commit();
            }

            return CommandResult.Ok(new JObject { ["purged"] = totalPurged });
        }
    }

    public class SetUnitsCommand : ICommand
    {
        public string MethodName => "set_units";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            if (parameters["linear_units"] != null)
                Application.SetSystemVariable("LUNITS", parameters["linear_units"].Value<int>());
            if (parameters["precision"] != null)
                Application.SetSystemVariable("LUPREC", parameters["precision"].Value<int>());
            if (parameters["insert_units"] != null)
                Application.SetSystemVariable("INSUNITS", parameters["insert_units"].Value<int>());
            if (parameters["angle_units"] != null)
                Application.SetSystemVariable("AUNITS", parameters["angle_units"].Value<int>());
            if (parameters["angle_precision"] != null)
                Application.SetSystemVariable("AUPREC", parameters["angle_precision"].Value<int>());

            return CommandResult.Ok("Drawing units updated");
        }
    }

    public class DeleteLayerCommand : ICommand
    {
        public string MethodName => "delete_layer";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string name = parameters["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                return CommandResult.Fail("Parameter 'name' is required");
            if (name == "0")
                return CommandResult.Fail("Cannot delete layer '0'");

            Database db = doc.Database;
            int movedEntities = 0;

            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
                if (!lt.Has(name))
                    return CommandResult.Fail($"Layer '{name}' not found");

                ObjectId layerId = lt[name];
                if (layerId == db.Clayer)
                    return CommandResult.Fail($"Cannot delete current layer '{name}'. Switch to another layer first.");

                // Move all entities on this layer to layer "0"
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId entId in ms)
                {
                    Entity ent = tr.GetObject(entId, OpenMode.ForRead) as Entity;
                    if (ent != null && ent.Layer.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        Entity entW = tr.GetObject(entId, OpenMode.ForWrite) as Entity;
                        entW.Layer = "0";
                        movedEntities++;
                    }
                }

                // Now erase the layer
                LayerTableRecord layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForWrite);
                layer.Erase();
                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["deleted"] = name,
                ["entities_moved_to_0"] = movedEntities
            });
        }
    }

    public class RenameLayerCommand : ICommand
    {
        public string MethodName => "rename_layer";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string oldName = parameters["old_name"]?.ToString();
            string newName = parameters["new_name"]?.ToString();
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName))
                return CommandResult.Fail("Parameters 'old_name' and 'new_name' are required");
            if (oldName == "0")
                return CommandResult.Fail("Cannot rename layer '0'");

            Database db = doc.Database;
            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(oldName))
                    return CommandResult.Fail($"Layer '{oldName}' not found");
                if (lt.Has(newName))
                    return CommandResult.Fail($"Layer '{newName}' already exists");

                LayerTableRecord layer = (LayerTableRecord)tr.GetObject(lt[oldName], OpenMode.ForWrite);
                layer.Name = newName;
                tr.Commit();
            }

            return CommandResult.Ok($"Layer '{oldName}' renamed to '{newName}'");
        }
    }

    public class CreateBlockCommand : ICommand
    {
        public string MethodName => "create_block";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string name = parameters["name"]?.ToString();
            JArray handles = parameters["handles"] as JArray;
            if (string.IsNullOrEmpty(name) || handles == null || handles.Count == 0)
                return CommandResult.Fail("Parameters 'name' and 'handles' are required");

            Point3d basePt = Point3d.Origin;
            if (parameters["base_point"] != null)
                basePt = ParsePoint(parameters["base_point"], "base_point");
            bool eraseOriginals = parameters["erase_originals"]?.Value<bool>() ?? false;

            Database db = doc.Database;
            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
                if (bt.Has(name))
                    return CommandResult.Fail($"Block '{name}' already exists");

                BlockTableRecord btr = new BlockTableRecord();
                btr.Name = name;
                btr.Origin = basePt;
                ObjectId blockId = bt.Add(btr);
                tr.AddNewlyCreatedDBObject(btr, true);

                int entityCount = 0;
                foreach (var hToken in handles)
                {
                    Handle h = new Handle(Convert.ToInt64(hToken.ToString()));
                    if (!db.TryGetObjectId(h, out ObjectId entId)) continue;

                    Entity ent = tr.GetObject(entId, eraseOriginals ? OpenMode.ForWrite : OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    Entity clone = ent.Clone() as Entity;
                    // Translate relative to base point
                    clone.TransformBy(Matrix3d.Displacement(Point3d.Origin - basePt));
                    btr.AppendEntity(clone);
                    tr.AddNewlyCreatedDBObject(clone, true);
                    entityCount++;

                    if (eraseOriginals) ent.Erase();
                }

                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["name"] = name,
                    ["entity_count"] = entityCount,
                    ["message"] = $"Block '{name}' created with {entityCount} entities"
                });
            }
        }
    }

    public class BulkCreateCommand : ICommand
    {
        public string MethodName => "bulk_create";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            JArray entities = parameters["entities"] as JArray;
            if (entities == null || entities.Count == 0)
                return CommandResult.Fail("Parameter 'entities' array is required");

            Database db = doc.Database;
            JArray createdHandles = new JArray();

            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

                foreach (JToken item in entities)
                {
                    string type = item["type"]?.ToString()?.ToLower();
                    JObject p = item["params"] as JObject ?? new JObject();

                    Entity ent = null;
                    try
                    {
                        switch (type)
                        {
                            case "line":
                                Point3d ls = ParsePoint(p["start"], "start");
                                Point3d le = ParsePoint(p["end"], "end");
                                ent = new Line(ls, le);
                                break;

                            case "circle":
                                Point3d cc = ParsePoint(p["center"], "center");
                                double cr = p["radius"]?.Value<double>() ?? 1;
                                ent = new Circle(cc, Vector3d.ZAxis, cr);
                                break;

                            case "arc":
                                Point3d ac = ParsePoint(p["center"], "center");
                                double ar = p["radius"]?.Value<double>() ?? 1;
                                double asa = (p["start_angle"]?.Value<double>() ?? 0) * Math.PI / 180.0;
                                double aea = (p["end_angle"]?.Value<double>() ?? 180) * Math.PI / 180.0;
                                ent = new Arc(ac, ar, asa, aea);
                                break;

                            case "polyline":
                                JArray pts = p["points"] as JArray;
                                if (pts == null || pts.Count < 2) continue;
                                Polyline pl = new Polyline();
                                for (int i = 0; i < pts.Count; i++)
                                {
                                    Point3d pp = ParsePoint(pts[i], $"pt{i}");
                                    pl.AddVertexAt(i, new Point2d(pp.X, pp.Y), 0, 0, 0);
                                }
                                if (p["closed"]?.Value<bool>() == true) pl.Closed = true;
                                ent = pl;
                                break;

                            case "rectangle":
                                Point3d rc1 = ParsePoint(p["corner1"], "corner1");
                                Point3d rc2 = ParsePoint(p["corner2"], "corner2");
                                Polyline rect = new Polyline();
                                rect.AddVertexAt(0, new Point2d(rc1.X, rc1.Y), 0, 0, 0);
                                rect.AddVertexAt(1, new Point2d(rc2.X, rc1.Y), 0, 0, 0);
                                rect.AddVertexAt(2, new Point2d(rc2.X, rc2.Y), 0, 0, 0);
                                rect.AddVertexAt(3, new Point2d(rc1.X, rc2.Y), 0, 0, 0);
                                rect.Closed = true;
                                ent = rect;
                                break;

                            case "text":
                                DBText txt = new DBText();
                                txt.Position = ParsePoint(p["position"], "position");
                                txt.TextString = p["text"]?.ToString() ?? "";
                                txt.Height = p["height"]?.Value<double>() ?? 2.5;
                                double rot = p["rotation"]?.Value<double>() ?? 0;
                                txt.Rotation = rot * Math.PI / 180.0;
                                string tjust = p["justification"]?.ToString() ?? "";
                                if (tjust == "middle-center")
                                {
                                    txt.HorizontalMode = TextHorizontalMode.TextCenter;
                                    txt.VerticalMode = TextVerticalMode.TextVerticalMid;
                                    txt.AlignmentPoint = ParsePoint(p["position"], "position");
                                }
                                ent = txt;
                                break;

                            case "mtext":
                                MText mt = new MText();
                                mt.Location = ParsePoint(p["position"], "position");
                                mt.Contents = p["text"]?.ToString() ?? "";
                                mt.TextHeight = p["height"]?.Value<double>() ?? 2.5;
                                double w = p["width"]?.Value<double>() ?? 0;
                                if (w > 0) mt.Width = w;
                                string mjust = p["justification"]?.ToString() ?? "";
                                if (mjust == "middle-center")
                                    mt.Attachment = AttachmentPoint.MiddleCenter;
                                ent = mt;
                                break;

                            case "ellipse":
                                Point3d ec = ParsePoint(p["center"], "center");
                                double emaj = p["major_radius"]?.Value<double>() ?? 1;
                                double emin = p["minor_radius"]?.Value<double>() ?? 0.5;
                                ent = new Ellipse(ec, Vector3d.ZAxis, new Vector3d(emaj, 0, 0), emin / emaj, 0, 2 * Math.PI);
                                break;

                            case "hatch":
                            {
                                // Hatch needs special handling: a closed
                                // boundary polyline must be appended to the
                                // database BEFORE the hatch can reference it
                                // via AppendLoop. We do that inline here and
                                // set `ent = null` to skip the post-switch
                                // append/colour pass — both the boundary and
                                // the hatch are fully appended + coloured
                                // within this case.
                                JArray hPts = p["boundary"] as JArray;
                                if (hPts == null || hPts.Count < 3) continue;

                                Polyline hb = new Polyline();
                                for (int i = 0; i < hPts.Count; i++)
                                {
                                    Point3d hp = ParsePoint(hPts[i], $"boundary[{i}]");
                                    hb.AddVertexAt(i, new Point2d(hp.X, hp.Y), 0, 0, 0);
                                }
                                hb.Closed = true;

                                string hLayer = p["layer"]?.ToString();
                                if (!string.IsNullOrEmpty(hLayer) && lt.Has(hLayer))
                                    hb.Layer = hLayer;

                                int? hAci = p["color"]?.Value<int>();
                                if (hAci.HasValue && hAci.Value >= 0 && hAci.Value <= 255)
                                    hb.ColorIndex = hAci.Value;

                                JArray hRgb = p["true_color"] as JArray;
                                Autodesk.AutoCAD.Colors.Color hTrueColor = null;
                                if (hRgb != null && hRgb.Count == 3)
                                {
                                    try
                                    {
                                        byte rr = (byte)Math.Max(0, Math.Min(255, hRgb[0].Value<int>()));
                                        byte gg = (byte)Math.Max(0, Math.Min(255, hRgb[1].Value<int>()));
                                        byte bb = (byte)Math.Max(0, Math.Min(255, hRgb[2].Value<int>()));
                                        hTrueColor = Autodesk.AutoCAD.Colors.Color.FromRgb(rr, gg, bb);
                                    }
                                    catch { /* ignore malformed colour */ }
                                }
                                if (hTrueColor != null) hb.Color = hTrueColor;

                                ObjectId hbId = ms.AppendEntity(hb);
                                tr.AddNewlyCreatedDBObject(hb, true);
                                createdHandles.Add(hb.Handle.Value.ToString());

                                Hatch h = new Hatch();
                                string hPattern = p["pattern"]?.ToString() ?? "SOLID";
                                double hScale = p["scale"]?.Value<double>() ?? 1.0;

                                ms.AppendEntity(h);
                                tr.AddNewlyCreatedDBObject(h, true);
                                h.SetHatchPattern(HatchPatternType.PreDefined, hPattern);
                                h.PatternScale = hScale;
                                h.Associative = true;
                                h.AppendLoop(
                                    HatchLoopTypes.Outermost,
                                    new ObjectIdCollection { hbId });
                                h.EvaluateHatch(true);

                                if (!string.IsNullOrEmpty(hLayer) && lt.Has(hLayer))
                                    h.Layer = hLayer;
                                if (hAci.HasValue && hAci.Value >= 0 && hAci.Value <= 255)
                                    h.ColorIndex = hAci.Value;
                                if (hTrueColor != null) h.Color = hTrueColor;

                                createdHandles.Add(h.Handle.Value.ToString());

                                ent = null; // already appended — skip the
                                            // generic post-switch append.
                                break;
                            }

                            default:
                                continue;
                        }

                        if (ent != null)
                        {
                            string layer = p["layer"]?.ToString();
                            if (!string.IsNullOrEmpty(layer) && lt.Has(layer))
                                ent.Layer = layer;

                            int? color = p["color"]?.Value<int>();
                            if (color.HasValue && color.Value >= 0 && color.Value <= 255)
                                ent.ColorIndex = color.Value;

                            ObjectId newId = ms.AppendEntity(ent);
                            tr.AddNewlyCreatedDBObject(ent, true);
                            createdHandles.Add(newId.Handle.Value.ToString());
                        }
                    }
                    catch { /* skip invalid entities */ }
                }

                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["handles"] = createdHandles,
                ["count"] = createdHandles.Count,
                ["requested"] = entities.Count
            });
        }
    }

    public class PlotToPdfCommand : ICommand
    {
        public string MethodName => "plot_to_pdf";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string outputPath = parameters["output_path"]?.ToString();
            if (string.IsNullOrEmpty(outputPath))
                return CommandResult.Fail("Parameter 'output_path' is required");

            // Use -EXPORT command for PDF which is simpler than -PLOT
            string cmd = $"._-EXPORTPDF \"{outputPath}\" ";
            doc.SendStringToExecute(cmd, true, false, false);

            return CommandResult.Ok(new JObject
            {
                ["output_path"] = outputPath,
                ["message"] = "PDF export command sent to AutoCAD"
            });
        }
    }
}
