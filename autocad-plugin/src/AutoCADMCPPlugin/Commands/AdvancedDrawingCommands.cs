using System;
using System.Collections.Generic;
using Newtonsoft.Json;
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
    public class PurgeDrawingCommand : AcadCommand
    {
        public override string MethodName => "purge_drawing";

        public override CommandResult Execute(JObject parameters)
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

    public class SetUnitsCommand : AcadCommand
    {
        public override string MethodName => "set_units";

        public override CommandResult Execute(JObject parameters)
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

    public class DeleteLayerCommand : AcadCommand
    {
        public override string MethodName => "delete_layer";

        public override CommandResult Execute(JObject parameters)
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

    public class RenameLayerCommand : AcadCommand
    {
        public override string MethodName => "rename_layer";

        public override CommandResult Execute(JObject parameters)
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

    public class CreateBlockCommand : AcadCommand
    {
        public override string MethodName => "create_block";

        public override CommandResult Execute(JObject parameters)
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
                // Origin MUST stay at (0,0,0): the cloned geometry below is
                // already translated by -basePt, so the base point is baked
                // into the definition. Setting btr.Origin = basePt as well
                // applies the base point a second time and every insert lands
                // at (insertion point - basePt) instead of the insertion point.
                ObjectId blockId = bt.Add(btr);
                tr.AddNewlyCreatedDBObject(btr, true);

                int entityCount = 0;
                foreach (var hToken in handles)
                {
                    if (!Handles.TryResolve(db, hToken.ToString(), out ObjectId entId)) continue;

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
                    ["base_point"] = new JArray(basePt.X, basePt.Y, basePt.Z),
                    ["message"] = $"Block '{name}' created with {entityCount} entities"
                });
            }
        }
    }

    /// <summary>
    /// Create many entities in one transaction.
    ///
    /// Each element of "entities" may be written in either shape:
    ///   nested - {"type": "line", "params": {"start": [0,0], "end": [10,0]}}
    ///   flat   - {"type": "line", "start": [0,0], "end": [10,0]}
    /// Both are accepted, and a mixture is accepted too: keys inside "params"
    /// win over keys of the same name at the top level. Earlier builds only
    /// understood the nested shape and silently produced count=0 for the flat
    /// one - every element that cannot be built is now reported in "skipped"
    /// with the reason, so a zero count always says why.
    /// </summary>
    public class BulkCreateCommand : AcadCommand
    {
        public override string MethodName => "bulk_create";

        private const string SupportedTypes =
            "line, circle, arc, polyline, rectangle, text, mtext, ellipse, hatch";

        /// <summary>First present, non-null token among the given key names.</summary>
        private static JToken Pick(JObject p, params string[] keys)
        {
            foreach (string k in keys)
            {
                JToken t = p[k];
                if (t != null && t.Type != JTokenType.Null) return t;
            }
            return null;
        }

        private static JObject Skip(int index, string type, string reason)
        {
            return new JObject
            {
                ["index"] = index,
                ["type"] = type ?? "",
                ["reason"] = reason
            };
        }

        /// <summary>
        /// Flatten one element into a single parameter bag. Top-level keys and
        /// nested "params" keys are merged, with "params" taking precedence.
        /// </summary>
        private static JObject Flatten(JObject el)
        {
            JObject nested = el["params"] as JObject;
            if (nested == null) return el;

            JObject merged = (JObject)el.DeepClone();
            merged.Remove("params");
            merged.Merge(nested, new JsonMergeSettings
            {
                MergeArrayHandling = MergeArrayHandling.Replace,
                MergeNullValueHandling = MergeNullValueHandling.Ignore
            });
            return merged;
        }

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            JArray entities = parameters["entities"] as JArray;
            if (entities == null || entities.Count == 0)
                return CommandResult.Fail("Parameter 'entities' array is required");

            Database db = doc.Database;
            JArray createdHandles = new JArray();
            JArray skipped = new JArray();
            JArray warnings = new JArray();

            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

                for (int index = 0; index < entities.Count; index++)
                {
                    JObject el = entities[index] as JObject;
                    if (el == null)
                    {
                        skipped.Add(Skip(index, null, "element is not a JSON object"));
                        continue;
                    }

                    string type = el["type"]?.ToString()?.Trim().ToLower();
                    JObject p = Flatten(el);

                    Entity ent = null;
                    try
                    {
                        switch (type)
                        {
                            case "line":
                                Point3d ls = ParsePoint(Pick(p, "start", "start_point", "point1", "from"), "start");
                                Point3d le = ParsePoint(Pick(p, "end", "end_point", "point2", "to"), "end");
                                ent = new Line(ls, le);
                                break;

                            case "circle":
                                Point3d cc = ParsePoint(Pick(p, "center", "center_point"), "center");
                                double cr = Pick(p, "radius", "r")?.Value<double>() ?? 1;
                                ent = new Circle(cc, Vector3d.ZAxis, cr);
                                break;

                            case "arc":
                                Point3d ac = ParsePoint(Pick(p, "center", "center_point"), "center");
                                double ar = Pick(p, "radius", "r")?.Value<double>() ?? 1;
                                double asa = (p["start_angle"]?.Value<double>() ?? 0) * Math.PI / 180.0;
                                double aea = (p["end_angle"]?.Value<double>() ?? 180) * Math.PI / 180.0;
                                ent = new Arc(ac, ar, asa, aea);
                                break;

                            case "polyline":
                                JArray pts = Pick(p, "points", "vertices", "pts") as JArray;
                                if (pts == null || pts.Count < 2)
                                {
                                    skipped.Add(Skip(index, type,
                                        "'points' must be an array of at least 2 points"));
                                    continue;
                                }
                                Polyline pl = new Polyline();
                                for (int i = 0; i < pts.Count; i++)
                                {
                                    Point3d pp = ParsePoint(pts[i], $"pt{i}");
                                    pl.AddVertexAt(i, new Point2d(pp.X, pp.Y), 0, 0, 0);
                                }
                                if (Pick(p, "closed", "close")?.Value<bool>() == true) pl.Closed = true;
                                ent = pl;
                                break;

                            case "rectangle":
                                Point3d rc1 = ParsePoint(Pick(p, "corner1", "point1", "min_point", "start"), "corner1");
                                Point3d rc2 = ParsePoint(Pick(p, "corner2", "point2", "max_point", "end"), "corner2");
                                Polyline rect = new Polyline();
                                rect.AddVertexAt(0, new Point2d(rc1.X, rc1.Y), 0, 0, 0);
                                rect.AddVertexAt(1, new Point2d(rc2.X, rc1.Y), 0, 0, 0);
                                rect.AddVertexAt(2, new Point2d(rc2.X, rc2.Y), 0, 0, 0);
                                rect.AddVertexAt(3, new Point2d(rc1.X, rc2.Y), 0, 0, 0);
                                rect.Closed = true;
                                ent = rect;
                                break;

                            case "text":
                                JToken tpos = Pick(p, "position", "insertion_point", "point", "start_point");
                                DBText txt = new DBText();
                                txt.Position = ParsePoint(tpos, "position");
                                txt.TextString = Pick(p, "text", "contents", "value")?.ToString() ?? "";
                                txt.Height = Pick(p, "height", "text_height")?.Value<double>() ?? 2.5;
                                double rot = Pick(p, "rotation", "angle")?.Value<double>() ?? 0;
                                txt.Rotation = rot * Math.PI / 180.0;
                                string tjust = Pick(p, "justification", "justify", "alignment")?.ToString() ?? "";
                                if (tjust == "middle-center")
                                {
                                    txt.HorizontalMode = TextHorizontalMode.TextCenter;
                                    txt.VerticalMode = TextVerticalMode.TextVerticalMid;
                                    txt.AlignmentPoint = ParsePoint(tpos, "position");
                                }
                                ent = txt;
                                break;

                            case "mtext":
                                MText mt = new MText();
                                mt.Location = ParsePoint(Pick(p, "position", "insertion_point", "point", "start_point"), "position");
                                mt.Contents = Pick(p, "text", "contents", "value")?.ToString() ?? "";
                                mt.TextHeight = Pick(p, "height", "text_height")?.Value<double>() ?? 2.5;
                                double w = p["width"]?.Value<double>() ?? 0;
                                if (w > 0) mt.Width = w;
                                string mjust = Pick(p, "justification", "justify", "alignment")?.ToString() ?? "";
                                if (mjust == "middle-center")
                                    mt.Attachment = AttachmentPoint.MiddleCenter;
                                ent = mt;
                                break;

                            case "ellipse":
                                Point3d ec = ParsePoint(Pick(p, "center", "center_point"), "center");
                                double emaj = Pick(p, "major_radius", "major_axis")?.Value<double>() ?? 1;
                                double emin = Pick(p, "minor_radius", "minor_axis")?.Value<double>() ?? 0.5;
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
                                JArray hPts = Pick(p, "boundary", "points", "vertices") as JArray;
                                if (hPts == null || hPts.Count < 3)
                                {
                                    skipped.Add(Skip(index, type,
                                        "'boundary' must be an array of at least 3 points"));
                                    continue;
                                }

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
                                createdHandles.Add(Handles.Format(hb));

                                Hatch h = new Hatch();
                                string hPattern = Pick(p, "pattern", "pattern_name", "hatch_pattern")?.ToString() ?? "SOLID";
                                double hScale = Pick(p, "scale", "pattern_scale")?.Value<double>() ?? 1.0;

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

                                createdHandles.Add(Handles.Format(h));

                                ent = null; // already appended — skip the
                                            // generic post-switch append.
                                break;
                            }

                            default:
                                skipped.Add(Skip(index, type, string.IsNullOrEmpty(type)
                                    ? "'type' is missing; supported: " + SupportedTypes
                                    : $"unsupported type '{type}'; supported: {SupportedTypes}"));
                                continue;
                        }

                        if (ent != null)
                        {
                            string layer = p["layer"]?.ToString();
                            if (!string.IsNullOrEmpty(layer))
                            {
                                if (lt.Has(layer)) ent.Layer = layer;
                                else warnings.Add(Skip(index, type,
                                    $"layer '{layer}' does not exist - the entity was created on the current layer instead"));
                            }

                            int? color = p["color"]?.Value<int>();
                            if (color.HasValue && color.Value >= 0 && color.Value <= 255)
                                ent.ColorIndex = color.Value;

                            ObjectId newId = ms.AppendEntity(ent);
                            tr.AddNewlyCreatedDBObject(ent, true);
                            createdHandles.Add(Handles.Format(newId));
                        }
                    }
                    catch (System.Exception ex)
                    {
                        // Never swallow a parse/build failure: an element that
                        // did not become an entity has to say why it did not.
                        skipped.Add(Skip(index, type, ex.Message));
                    }
                }

                tr.Commit();
            }

            // One element is not always one entity: a hatch contributes its
            // boundary polyline as well, so the two counts are reported
            // separately rather than one being passed off as the other.
            int elementsCreated = entities.Count - skipped.Count;

            var result = new JObject
            {
                ["handles"] = createdHandles,
                ["count"] = createdHandles.Count,
                ["elements_created"] = elementsCreated,
                ["requested"] = entities.Count,
                ["skipped_count"] = skipped.Count,
                ["skipped"] = skipped
            };
            if (warnings.Count > 0) result["warnings"] = warnings;

            if (skipped.Count == 0)
                result["message"] = $"All {entities.Count} elements created " +
                                    $"({createdHandles.Count} entities).";
            else if (createdHandles.Count == 0)
                result["message"] = $"Nothing was created: all {entities.Count} elements were skipped. " +
                                    "See 'skipped' for the reason of each. Elements may be written flat " +
                                    "({\"type\":\"line\",\"start\":[...],\"end\":[...]}) or nested under \"params\".";
            else
                result["message"] = $"{elementsCreated} of {entities.Count} elements created " +
                                    $"({createdHandles.Count} entities); {skipped.Count} skipped - " +
                                    "see 'skipped'.";

            return CommandResult.Ok(result);
        }
    }

    // PlotToPdfCommand used to live here. It now has a file of its own,
    // Commands/PlotCommands.cs, because a plot that actually reaches disk needs
    // the PlottingServices API rather than a one-line SendStringToExecute.
}
