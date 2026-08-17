using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCADMCPPlugin.Commands
{
    internal static class AnnotationHelper
    {
        /// <summary>Resolve a dimension style by name, falling back to the current one.</summary>
        public static ObjectId DimStyle(Transaction tr, Database db, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return db.Dimstyle;
            var dst = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            return dst.Has(name) ? dst[name] : db.Dimstyle;
        }

        public static ObjectId TextStyle(Transaction tr, Database db, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return db.Textstyle;
            var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            return tst.Has(name) ? tst[name] : db.Textstyle;
        }
    }

    // ========================================================================
    // Multileaders
    // ========================================================================

    public class CreateMultileaderCommand : AcadCommand
    {
        public override string MethodName => "create_multileader";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var arrowToken = EntityHelper.Arg(parameters, "arrow_point", "start", "point");
            var textToken = EntityHelper.Arg(parameters, "text_point", "landing", "end");
            string text = EntityHelper.ArgString(parameters, "text", "content");

            if (arrowToken == null || textToken == null)
                return CommandResult.BadParam(
                    "Parameters 'arrow_point' (where the arrow lands) and 'text_point' are required");
            if (string.IsNullOrEmpty(text))
                return CommandResult.BadParam("Parameter 'text' is required");

            Point3d arrow = EntityHelper.ParsePoint(arrowToken, "arrow_point");
            Point3d textPt = EntityHelper.ParsePoint(textToken, "text_point");
            double height = parameters["height"]?.Value<double>() ?? 0;

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                var ml = new MLeader();
                ml.SetDatabaseDefaults(db);
                ml.ContentType = ContentType.MTextContent;

                // Style must be applied before the content so it inherits correctly.
                string styleName = EntityHelper.ArgString(parameters, "style", "mleader_style");
                if (!string.IsNullOrWhiteSpace(styleName))
                {
                    var dict = (DBDictionary)tr.GetObject(db.MLeaderStyleDictionaryId, OpenMode.ForRead);
                    if (!dict.Contains(styleName))
                        return CommandResult.NotFound($"Multileader style '{styleName}' not found");
                    ml.MLeaderStyle = dict.GetAt(styleName);
                }

                var mtext = new MText();
                mtext.SetDatabaseDefaults(db);
                mtext.Contents = text;
                mtext.Location = textPt;
                if (height > 0) mtext.TextHeight = height;

                string textStyle = parameters["text_style"]?.ToString();
                if (!string.IsNullOrWhiteSpace(textStyle))
                    mtext.TextStyleId = AnnotationHelper.TextStyle(tr, db, textStyle);

                ml.MText = mtext;

                // A leader cluster, then a line within it, then its vertices.
                int leaderIndex = ml.AddLeader();
                int lineIndex = ml.AddLeaderLine(leaderIndex);
                ml.AddFirstVertex(lineIndex, arrow);
                ml.AddLastVertex(lineIndex, textPt);

                string layer = parameters["layer"]?.ToString();
                if (!string.IsNullOrEmpty(layer))
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (lt.Has(layer)) ml.Layer = layer;
                }

                ObjectId id = space.AppendEntity(ml);
                tr.AddNewlyCreatedDBObject(ml, true);
                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["id"] = id.Handle.Value.ToString(),
                    ["type"] = "MLeader",
                    ["text"] = text,
                    ["arrow_point"] = new JArray(arrow.X, arrow.Y, arrow.Z),
                    ["text_point"] = new JArray(textPt.X, textPt.Y, textPt.Z)
                });
            }
        }
    }

    public class ListMleaderStylesCommand : AcadCommand
    {
        public override string MethodName => "list_mleader_styles";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var results = new JArray();

            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(
                    doc.Database.MLeaderStyleDictionaryId, OpenMode.ForRead);

                foreach (DBDictionaryEntry entry in dict)
                {
                    var style = tr.GetObject(entry.Value, OpenMode.ForRead) as MLeaderStyle;
                    if (style == null) continue;
                    results.Add(new JObject
                    {
                        ["name"] = entry.Key,
                        ["text_height"] = SafeD(() => style.TextHeight),
                        ["arrow_size"] = SafeD(() => style.ArrowSize),
                        ["landing_gap"] = SafeD(() => style.LandingGap),
                        ["content_type"] = style.ContentType.ToString()
                    });
                }
                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["mleader_styles"] = results,
                ["count"] = results.Count
            });
        }

        private static double SafeD(Func<double> f) { try { return f(); } catch { return 0; } }
    }

    public class CreateMleaderStyleCommand : AcadCommand
    {
        public override string MethodName => "create_mleader_style";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = EntityHelper.ArgString(parameters, "name", "style_name");
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.BadParam("Parameter 'name' is required");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(db.MLeaderStyleDictionaryId, OpenMode.ForWrite);
                if (dict.Contains(name))
                    return CommandResult.BadParam($"Multileader style '{name}' already exists");

                // MLeaderStyle has no SetDatabaseDefaults; its constructor already
                // seeds sensible defaults, and PostMLeaderStyleToDb finishes the job.
                var style = new MLeaderStyle();

                double h = parameters["text_height"]?.Value<double>() ?? 0;
                if (h > 0) style.TextHeight = h;

                double arrow = parameters["arrow_size"]?.Value<double>() ?? 0;
                if (arrow > 0) style.ArrowSize = arrow;

                double gap = parameters["landing_gap"]?.Value<double>() ?? -1;
                if (gap >= 0) style.LandingGap = gap;

                string textStyle = parameters["text_style"]?.ToString();
                if (!string.IsNullOrWhiteSpace(textStyle))
                    style.TextStyleId = AnnotationHelper.TextStyle(tr, db, textStyle);

                style.PostMLeaderStyleToDb(db, name);
                tr.AddNewlyCreatedDBObject(style, true);
                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["name"] = name,
                    ["text_height"] = style.TextHeight,
                    ["arrow_size"] = style.ArrowSize
                });
            }
        }
    }

    // ========================================================================
    // Additional dimension types
    // ========================================================================

    public class CreateOrdinateDimensionCommand : AcadCommand
    {
        public override string MethodName => "create_ordinate_dimension";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var pointToken = EntityHelper.Arg(parameters, "point", "defining_point");
            var leaderToken = EntityHelper.Arg(parameters, "leader_end", "leader_point", "end");
            if (pointToken == null || leaderToken == null)
                return CommandResult.BadParam("Parameters 'point' and 'leader_end' are required");

            Point3d defining = EntityHelper.ParsePoint(pointToken, "point");
            Point3d leaderEnd = EntityHelper.ParsePoint(leaderToken, "leader_end");

            // "x" measures along the X axis (a vertical ordinate line), "y" the other.
            string axis = (parameters["axis"]?.ToString() ?? "x").Trim().ToLowerInvariant();
            if (axis != "x" && axis != "y")
                return CommandResult.BadParam("'axis' must be 'x' or 'y'");

            string dimText = parameters["text"]?.ToString() ?? "";
            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId styleId = AnnotationHelper.DimStyle(tr, db, parameters["style"]?.ToString());
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                using (var dim = new OrdinateDimension(axis == "x", defining, leaderEnd, dimText, styleId))
                {
                    dim.SetDatabaseDefaults(db);

                    string layer = parameters["layer"]?.ToString();
                    if (!string.IsNullOrEmpty(layer))
                    {
                        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        if (lt.Has(layer)) dim.Layer = layer;
                    }

                    ObjectId id = space.AppendEntity(dim);
                    tr.AddNewlyCreatedDBObject(dim, true);
                    tr.Commit();

                    return CommandResult.Ok(new JObject
                    {
                        ["id"] = id.Handle.Value.ToString(),
                        ["type"] = "OrdinateDimension",
                        ["axis"] = axis,
                        ["point"] = new JArray(defining.X, defining.Y, defining.Z),
                        ["leader_end"] = new JArray(leaderEnd.X, leaderEnd.Y, leaderEnd.Z)
                    });
                }
            }
        }
    }

    public class CreateArcLengthDimensionCommand : AcadCommand
    {
        public override string MethodName => "create_arclength_dimension";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var centerT = EntityHelper.Arg(parameters, "center");
            var p1T = EntityHelper.Arg(parameters, "start", "point1", "xline1");
            var p2T = EntityHelper.Arg(parameters, "end", "point2", "xline2");
            var arcT = EntityHelper.Arg(parameters, "arc_point", "text_point", "position");

            if (centerT == null || p1T == null || p2T == null || arcT == null)
                return CommandResult.BadParam(
                    "Parameters 'center', 'start', 'end' and 'arc_point' are required");

            Point3d center = EntityHelper.ParsePoint(centerT, "center");
            Point3d p1 = EntityHelper.ParsePoint(p1T, "start");
            Point3d p2 = EntityHelper.ParsePoint(p2T, "end");
            Point3d arcPt = EntityHelper.ParsePoint(arcT, "arc_point");

            string dimText = parameters["text"]?.ToString() ?? "";
            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId styleId = AnnotationHelper.DimStyle(tr, db, parameters["style"]?.ToString());
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                using (var dim = new ArcDimension(center, p1, p2, arcPt, dimText, styleId))
                {
                    dim.SetDatabaseDefaults(db);

                    string layer = parameters["layer"]?.ToString();
                    if (!string.IsNullOrEmpty(layer))
                    {
                        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        if (lt.Has(layer)) dim.Layer = layer;
                    }

                    ObjectId id = space.AppendEntity(dim);
                    tr.AddNewlyCreatedDBObject(dim, true);
                    tr.Commit();

                    return CommandResult.Ok(new JObject
                    {
                        ["id"] = id.Handle.Value.ToString(),
                        ["type"] = "ArcDimension",
                        ["center"] = new JArray(center.X, center.Y, center.Z)
                    });
                }
            }
        }
    }

    public class CreateToleranceCommand : AcadCommand
    {
        public override string MethodName => "create_tolerance";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string codes = EntityHelper.ArgString(parameters, "text", "codes", "content");
            if (string.IsNullOrEmpty(codes))
                return CommandResult.BadParam(
                    "Parameter 'text' is required — the GD&T frame content, " +
                    "e.g. \"{\\\\Fgdt;j}%%v{\\\\Fgdt;n}0.05%%v%%v%%v%%v%%v\"");

            var posT = EntityHelper.Arg(parameters, "position", "point", "location");
            if (posT == null)
                return CommandResult.BadParam("Parameter 'position' is required");

            Point3d pos = EntityHelper.ParsePoint(posT, "position");
            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                using (var fcf = new FeatureControlFrame(codes, pos, Vector3d.ZAxis, Vector3d.XAxis))
                {
                    fcf.SetDatabaseDefaults(db);

                    double h = parameters["height"]?.Value<double>() ?? 0;
                    if (h > 0)
                    {
                        try { fcf.Dimtxt = h; } catch { }
                    }

                    string layer = parameters["layer"]?.ToString();
                    if (!string.IsNullOrEmpty(layer))
                    {
                        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        if (lt.Has(layer)) fcf.Layer = layer;
                    }

                    ObjectId id = space.AppendEntity(fcf);
                    tr.AddNewlyCreatedDBObject(fcf, true);
                    tr.Commit();

                    return CommandResult.Ok(new JObject
                    {
                        ["id"] = id.Handle.Value.ToString(),
                        ["type"] = "FeatureControlFrame",
                        ["position"] = new JArray(pos.X, pos.Y, pos.Z)
                    });
                }
            }
        }
    }

    public class EditDimensionTextCommand : AcadCommand
    {
        public override string MethodName => "edit_dimension_text";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string handle = EntityHelper.EntityIdArg(parameters);
            if (string.IsNullOrWhiteSpace(handle))
                return CommandResult.BadParam("Parameter 'id' is required");

            var textToken = EntityHelper.Arg(parameters, "text", "override");
            if (textToken == null)
                return CommandResult.BadParam(
                    "Parameter 'text' is required. Use \"\" to clear the override and " +
                    "restore the measured value, or include <> to embed it.");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId id = EntityHelper.ResolveHandle(db, handle);
                if (id.IsNull) return CommandResult.NotFound($"Entity '{handle}' not found");

                var dim = tr.GetObject(id, OpenMode.ForWrite) as Dimension;
                if (dim == null) return CommandResult.BadParam($"Entity '{handle}' is not a dimension");

                dim.DimensionText = textToken.ToString();
                double measured = SafeMeasurement(dim);
                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["id"] = handle,
                    ["dimension_text"] = dim.DimensionText ?? "",
                    ["measurement"] = measured
                });
            }
        }

        private static double SafeMeasurement(Dimension d)
        {
            try { return d.Measurement; } catch { return 0; }
        }
    }

    public class UpdateDimensionsCommand : AcadCommand
    {
        public override string MethodName => "update_dimensions";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var idsToken = EntityHelper.Arg(parameters, "ids", "entity_ids");
            string styleName = parameters["style"]?.ToString();
            Database db = doc.Database;

            int updated = 0;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId styleId = ObjectId.Null;
                if (!string.IsNullOrWhiteSpace(styleName))
                {
                    var dst = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
                    if (!dst.Has(styleName))
                        return CommandResult.NotFound($"Dimension style '{styleName}' not found");
                    styleId = dst[styleName];
                }

                List<ObjectId> ids;
                if (idsToken != null)
                {
                    string err;
                    if (!ModifyHelper.TryResolveAll(db, idsToken, out ids, out err))
                        return CommandResult.NotFound(err);
                }
                else
                {
                    // No list given: update every dimension in the current space.
                    ids = new List<ObjectId>();
                    var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                    foreach (ObjectId id in space) ids.Add(id);
                }

                foreach (ObjectId id in ids)
                {
                    var dim = tr.GetObject(id, OpenMode.ForWrite) as Dimension;
                    if (dim == null) continue;

                    if (!styleId.IsNull) dim.DimensionStyle = styleId;

                    // Forces the dimension block to regenerate from current settings.
                    dim.RecomputeDimensionBlock(true);
                    updated++;
                }

                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["success"] = true,
                ["updated"] = updated,
                ["style"] = styleName ?? "(unchanged)"
            });
        }
    }

    // ========================================================================
    // Annotation scaling
    // ========================================================================

    public class ListAnnotationScalesCommand : AcadCommand
    {
        public override string MethodName => "list_annotation_scales";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var results = new JArray();
            Database db = doc.Database;

            ObjectContextCollection occ = db.ObjectContextManager
                                            .GetContextCollection("ACDB_ANNOTATIONSCALES");
            if (occ == null)
                return CommandResult.Unsupported("This drawing has no annotation scale collection");

            foreach (ObjectContext ctx in occ)
            {
                var scale = ctx as AnnotationScale;
                if (scale == null) continue;
                results.Add(new JObject
                {
                    ["name"] = scale.Name,
                    ["paper_units"] = scale.PaperUnits,
                    ["drawing_units"] = scale.DrawingUnits,
                    ["scale"] = scale.DrawingUnits != 0
                        ? scale.PaperUnits / scale.DrawingUnits
                        : 0
                });
            }

            string current = "";
            try { current = db.Cannoscale?.Name ?? ""; } catch { }

            return CommandResult.Ok(new JObject
            {
                ["annotation_scales"] = results,
                ["count"] = results.Count,
                ["current"] = current
            });
        }
    }

    public class SetAnnotationScaleCommand : AcadCommand
    {
        public override string MethodName => "set_annotation_scale";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = EntityHelper.ArgString(parameters, "name", "scale");
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.BadParam(
                    "Parameter 'name' is required, e.g. \"1:100\" (see list_annotation_scales)");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            {
                ObjectContextCollection occ = db.ObjectContextManager
                                                .GetContextCollection("ACDB_ANNOTATIONSCALES");
                if (occ == null)
                    return CommandResult.Unsupported("This drawing has no annotation scale collection");

                ObjectContext ctx = occ.GetContext(name);
                if (ctx == null)
                    return CommandResult.NotFound(
                        $"Annotation scale '{name}' not found. Use list_annotation_scales to see valid names.");

                db.Cannoscale = (AnnotationScale)ctx;

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["current"] = name
                });
            }
        }
    }

    public class AddAnnotationScaleToEntityCommand : AcadCommand
    {
        public override string MethodName => "add_annotation_scale_to_entity";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var idsToken = EntityHelper.Arg(parameters, "ids", "entity_ids", "id");
            if (idsToken == null)
                return CommandResult.BadParam("Parameter 'ids' is required");

            string scaleName = EntityHelper.ArgString(parameters, "scale", "name");
            if (string.IsNullOrWhiteSpace(scaleName))
                return CommandResult.BadParam("Parameter 'scale' is required, e.g. \"1:100\"");

            bool remove = parameters["remove"]?.Value<bool>() ?? false;
            var arr = idsToken as JArray ?? new JArray(idsToken);
            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectContextCollection occ = db.ObjectContextManager
                                                .GetContextCollection("ACDB_ANNOTATIONSCALES");
                if (occ == null)
                    return CommandResult.Unsupported("This drawing has no annotation scale collection");

                ObjectContext ctx = occ.GetContext(scaleName);
                if (ctx == null)
                    return CommandResult.NotFound($"Annotation scale '{scaleName}' not found");

                List<ObjectId> ids;
                string err;
                if (!ModifyHelper.TryResolveAll(db, arr, out ids, out err))
                    return CommandResult.NotFound(err);

                int changed = 0;
                var skipped = new JArray();

                foreach (ObjectId id in ids)
                {
                    var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;

                    // Only annotative entities carry per-scale representations.
                    if (!ent.Annotative.Equals(AnnotativeStates.True))
                    {
                        try { ent.Annotative = AnnotativeStates.True; }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                            skipped.Add(id.Handle.Value.ToString());
                            continue;
                        }
                    }

                    try
                    {
                        if (remove) ent.RemoveContext(ctx);
                        else ent.AddContext(ctx);
                        changed++;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        skipped.Add(id.Handle.Value.ToString());
                    }
                }

                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["scale"] = scaleName,
                    ["action"] = remove ? "removed" : "added",
                    ["changed"] = changed,
                    ["skipped"] = skipped
                });
            }
        }
    }

    // ========================================================================
    // Tables
    // ========================================================================

    public class GetTableDataCommand : AcadCommand
    {
        public override string MethodName => "get_table_data";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string handle = EntityHelper.EntityIdArg(parameters);
            if (string.IsNullOrWhiteSpace(handle))
                return CommandResult.BadParam("Parameter 'id' is required");

            Database db = doc.Database;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId id = EntityHelper.ResolveHandle(db, handle);
                if (id.IsNull) return CommandResult.NotFound($"Entity '{handle}' not found");

                var table = tr.GetObject(id, OpenMode.ForRead) as Table;
                if (table == null) return CommandResult.BadParam($"Entity '{handle}' is not a table");

                var rows = new JArray();
                for (int r = 0; r < table.Rows.Count; r++)
                {
                    var row = new JArray();
                    for (int c = 0; c < table.Columns.Count; c++)
                    {
                        string val;
                        try { val = table.Cells[r, c].TextString ?? ""; }
                        catch { val = ""; }
                        row.Add(val);
                    }
                    rows.Add(row);
                }

                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["id"] = handle,
                    ["rows"] = table.Rows.Count,
                    ["columns"] = table.Columns.Count,
                    ["data"] = rows,
                    ["position"] = new JArray(table.Position.X, table.Position.Y, table.Position.Z)
                });
            }
        }
    }

    public class SetTableCellCommand : AcadCommand
    {
        public override string MethodName => "set_table_cell";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string handle = EntityHelper.EntityIdArg(parameters);
            if (string.IsNullOrWhiteSpace(handle))
                return CommandResult.BadParam("Parameter 'id' is required");

            Database db = doc.Database;

            // Accept either a single cell or a batch of them.
            var cells = EntityHelper.Arg(parameters, "cells") as JArray;
            int? row = parameters["row"]?.Value<int>();
            int? col = parameters["column"]?.Value<int>() ?? parameters["col"]?.Value<int>();
            var textToken = EntityHelper.Arg(parameters, "text", "value");

            if (cells == null && (row == null || col == null || textToken == null))
                return CommandResult.BadParam(
                    "Provide either 'row'+'column'+'text', or 'cells' as an array of " +
                    "{row, column, text} objects");

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId id = EntityHelper.ResolveHandle(db, handle);
                if (id.IsNull) return CommandResult.NotFound($"Entity '{handle}' not found");

                var table = tr.GetObject(id, OpenMode.ForWrite) as Table;
                if (table == null) return CommandResult.BadParam($"Entity '{handle}' is not a table");

                var updates = new List<Tuple<int, int, string>>();
                if (cells != null)
                {
                    foreach (var c in cells)
                    {
                        var o = c as JObject;
                        if (o == null) continue;
                        int cr = o["row"]?.Value<int>() ?? -1;
                        int cc = o["column"]?.Value<int>() ?? o["col"]?.Value<int>() ?? -1;
                        string ct = o["text"]?.ToString() ?? o["value"]?.ToString() ?? "";
                        updates.Add(Tuple.Create(cr, cc, ct));
                    }
                }
                else
                {
                    updates.Add(Tuple.Create(row.Value, col.Value, textToken.ToString()));
                }

                int applied = 0;
                foreach (var u in updates)
                {
                    if (u.Item1 < 0 || u.Item1 >= table.Rows.Count ||
                        u.Item2 < 0 || u.Item2 >= table.Columns.Count)
                    {
                        return CommandResult.BadParam(
                            $"Cell ({u.Item1},{u.Item2}) is outside the table " +
                            $"({table.Rows.Count} rows x {table.Columns.Count} columns)");
                    }

                    table.Cells[u.Item1, u.Item2].TextString = u.Item3;
                    applied++;
                }

                double? textHeight = parameters["text_height"]?.Value<double>();
                if (textHeight.HasValue && cells == null)
                {
                    try { table.Cells[row.Value, col.Value].TextHeight = textHeight.Value; }
                    catch { }
                }

                table.GenerateLayout();
                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["id"] = handle,
                    ["cells_updated"] = applied
                });
            }
        }
    }

    public class MergeTableCellsCommand : AcadCommand
    {
        public override string MethodName => "merge_table_cells";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string handle = EntityHelper.EntityIdArg(parameters);
            if (string.IsNullOrWhiteSpace(handle))
                return CommandResult.BadParam("Parameter 'id' is required");

            int topRow = parameters["top_row"]?.Value<int>() ?? -1;
            int bottomRow = parameters["bottom_row"]?.Value<int>() ?? -1;
            int leftCol = parameters["left_column"]?.Value<int>() ?? -1;
            int rightCol = parameters["right_column"]?.Value<int>() ?? -1;

            if (topRow < 0 || bottomRow < 0 || leftCol < 0 || rightCol < 0)
                return CommandResult.BadParam(
                    "Parameters 'top_row', 'bottom_row', 'left_column' and 'right_column' are required");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId id = EntityHelper.ResolveHandle(db, handle);
                if (id.IsNull) return CommandResult.NotFound($"Entity '{handle}' not found");

                var table = tr.GetObject(id, OpenMode.ForWrite) as Table;
                if (table == null) return CommandResult.BadParam($"Entity '{handle}' is not a table");

                if (bottomRow >= table.Rows.Count || rightCol >= table.Columns.Count)
                    return CommandResult.BadParam(
                        $"Range exceeds the table ({table.Rows.Count} rows x {table.Columns.Count} columns)");

                try
                {
                    var range = CellRange.Create(table, topRow, leftCol, bottomRow, rightCol);
                    bool unmerge = parameters["unmerge"]?.Value<bool>() ?? false;
                    if (unmerge) table.UnmergeCells(range);
                    else table.MergeCells(range);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    return CommandResult.BadParam($"Merge failed: {ex.Message}");
                }

                table.GenerateLayout();
                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["id"] = handle,
                    ["range"] = new JArray(topRow, leftCol, bottomRow, rightCol)
                });
            }
        }
    }

    public class ListTableStylesCommand : AcadCommand
    {
        public override string MethodName => "list_table_styles";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var results = new JArray();

            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(
                    doc.Database.TableStyleDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dict)
                {
                    var style = tr.GetObject(entry.Value, OpenMode.ForRead) as TableStyle;
                    if (style == null) continue;
                    results.Add(new JObject
                    {
                        ["name"] = entry.Key,
                        ["description"] = style.Description ?? ""
                    });
                }
                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["table_styles"] = results,
                ["count"] = results.Count
            });
        }
    }

    // ========================================================================
    // MText editing, wipeout, revision cloud
    // ========================================================================

    public class EditMtextCommand : AcadCommand
    {
        public override string MethodName => "edit_mtext";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string handle = EntityHelper.EntityIdArg(parameters);
            if (string.IsNullOrWhiteSpace(handle))
                return CommandResult.BadParam("Parameter 'id' is required");

            Database db = doc.Database;
            var applied = new JArray();

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId id = EntityHelper.ResolveHandle(db, handle);
                if (id.IsNull) return CommandResult.NotFound($"Entity '{handle}' not found");

                var obj = tr.GetObject(id, OpenMode.ForWrite);

                // Handle both MText and single-line DBText for convenience.
                var mtext = obj as MText;
                var dbtext = obj as DBText;
                if (mtext == null && dbtext == null)
                    return CommandResult.BadParam($"Entity '{handle}' is not text or mtext");

                var textToken = EntityHelper.Arg(parameters, "text", "contents");
                if (textToken != null)
                {
                    if (mtext != null) mtext.Contents = textToken.ToString();
                    else dbtext.TextString = textToken.ToString();
                    applied.Add("text");
                }

                double? height = parameters["height"]?.Value<double>();
                if (height.HasValue && height.Value > 0)
                {
                    if (mtext != null) mtext.TextHeight = height.Value;
                    else dbtext.Height = height.Value;
                    applied.Add("height");
                }

                double? width = parameters["width"]?.Value<double>();
                if (width.HasValue && mtext != null)
                {
                    mtext.Width = width.Value;
                    applied.Add("width");
                }

                double? rotation = parameters["rotation"]?.Value<double>();
                if (rotation.HasValue)
                {
                    double rad = rotation.Value * Math.PI / 180.0;
                    if (mtext != null) mtext.Rotation = rad;
                    else dbtext.Rotation = rad;
                    applied.Add("rotation");
                }

                string style = parameters["text_style"]?.ToString();
                if (!string.IsNullOrWhiteSpace(style))
                {
                    ObjectId sid = AnnotationHelper.TextStyle(tr, db, style);
                    if (mtext != null) mtext.TextStyleId = sid;
                    else dbtext.TextStyleId = sid;
                    applied.Add("text_style");
                }

                if (applied.Count == 0)
                    return CommandResult.BadParam(
                        "Provide at least one of: text, height, width, rotation, text_style");

                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["id"] = handle,
                    ["applied"] = applied
                });
            }
        }
    }

    public class CreateWipeoutCommand : AcadCommand
    {
        public override string MethodName => "create_wipeout";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var ptsToken = EntityHelper.Arg(parameters, "points", "boundary") as JArray;
            if (ptsToken == null || ptsToken.Count < 3)
                return CommandResult.BadParam(
                    "Parameter 'points' needs at least 3 [x,y] points forming the mask boundary");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                var pts = new Point2dCollection();
                foreach (var t in ptsToken)
                {
                    Point3d p = EntityHelper.ParsePoint(t, "points[]");
                    pts.Add(new Point2d(p.X, p.Y));
                }

                // A wipeout boundary must be explicitly closed.
                if (!pts[0].IsEqualTo(pts[pts.Count - 1])) pts.Add(pts[0]);

                var wipeout = new Wipeout();
                wipeout.SetDatabaseDefaults(db);

                try
                {
                    wipeout.SetFrom(pts, Vector3d.ZAxis);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    wipeout.Dispose();
                    return CommandResult.BadParam(
                        $"Could not build the wipeout boundary: {ex.Message}. " +
                        "Points must form a simple, non-self-intersecting polygon.");
                }

                string layer = parameters["layer"]?.ToString();
                if (!string.IsNullOrEmpty(layer))
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (lt.Has(layer)) wipeout.Layer = layer;
                }

                // Frame visibility is global (WIPEOUTFRAME system variable), not a
                // per-entity property — use set_system_variable to control it.

                ObjectId id = space.AppendEntity(wipeout);
                tr.AddNewlyCreatedDBObject(wipeout, true);
                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["id"] = id.Handle.Value.ToString(),
                    ["type"] = "Wipeout",
                    ["vertex_count"] = pts.Count
                });
            }
        }
    }

    public class CreateRevisionCloudCommand : AcadCommand
    {
        public override string MethodName => "create_revision_cloud";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var minT = EntityHelper.Arg(parameters, "min", "corner1");
            var maxT = EntityHelper.Arg(parameters, "max", "corner2");
            if (minT == null || maxT == null)
                return CommandResult.BadParam(
                    "Parameters 'min' and 'max' (opposite corners of the cloud) are required");

            Point3d min = EntityHelper.ParsePoint(minT, "min");
            Point3d max = EntityHelper.ParsePoint(maxT, "max");

            double x0 = Math.Min(min.X, max.X), x1 = Math.Max(min.X, max.X);
            double y0 = Math.Min(min.Y, max.Y), y1 = Math.Max(min.Y, max.Y);
            double w = x1 - x0, h = y1 - y0;

            if (w <= 0 || h <= 0)
                return CommandResult.BadParam("'min' and 'max' must define a non-empty rectangle");

            double arcLength = parameters["arc_length"]?.Value<double>() ?? 0;
            if (arcLength <= 0) arcLength = Math.Min(w, h) / 6.0;   // AutoCAD-ish default
            if (arcLength <= 0)
                return CommandResult.BadParam("'arc_length' must be positive");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                // Walk the rectangle perimeter, emitting one outward bulge per step.
                var corners = new[]
                {
                    new Point2d(x0, y0), new Point2d(x1, y0),
                    new Point2d(x1, y1), new Point2d(x0, y1)
                };

                var verts = new List<Point2d>();
                for (int i = 0; i < 4; i++)
                {
                    Point2d a = corners[i];
                    Point2d b = corners[(i + 1) % 4];
                    double side = a.GetDistanceTo(b);
                    int n = Math.Max(1, (int)Math.Round(side / arcLength));
                    for (int k = 0; k < n; k++)
                    {
                        double t = (double)k / n;
                        verts.Add(new Point2d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
                    }
                }

                var pl = new Polyline(verts.Count);
                // Bulge 0.5 ~= a shallow outward arc, which is what reads as a cloud.
                const double bulge = 0.5;
                for (int i = 0; i < verts.Count; i++)
                    pl.AddVertexAt(i, verts[i], bulge, 0, 0);
                pl.Closed = true;
                pl.Elevation = min.Z;
                pl.SetDatabaseDefaults(db);

                string layer = parameters["layer"]?.ToString();
                if (!string.IsNullOrEmpty(layer))
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (lt.Has(layer)) pl.Layer = layer;
                }

                int? color = parameters["color"]?.Value<int>();
                if (color.HasValue && color.Value >= 0 && color.Value <= 255)
                    pl.ColorIndex = color.Value;

                ObjectId id = space.AppendEntity(pl);
                tr.AddNewlyCreatedDBObject(pl, true);
                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["id"] = id.Handle.Value.ToString(),
                    ["type"] = "RevisionCloud",
                    ["segments"] = verts.Count,
                    ["arc_length"] = arcLength,
                    ["min"] = new JArray(x0, y0),
                    ["max"] = new JArray(x1, y1)
                });
            }
        }
    }
}
