using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using static AutoCADMCPPlugin.Commands.EntityHelper;

namespace AutoCADMCPPlugin.Commands
{
    public class SetEntityPropertiesCommand : AcadCommand
    {
        public override string MethodName => "set_entity_properties";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string handle = parameters["handle"]?.ToString();
            if (string.IsNullOrEmpty(handle))
                return CommandResult.Fail("Parameter 'handle' is required");

            Database db = doc.Database;
            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Handle h = new Handle(Convert.ToInt64(handle));
                if (!db.TryGetObjectId(h, out ObjectId id))
                    return CommandResult.Fail($"Entity not found: {handle}");

                Entity ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                if (ent == null)
                    return CommandResult.Fail($"Object {handle} is not an entity");

                if (parameters["layer"] != null)
                {
                    string layerName = parameters["layer"].ToString();
                    LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (lt.Has(layerName)) ent.Layer = layerName;
                }
                if (parameters["color"] != null)
                    ent.ColorIndex = parameters["color"].Value<int>();
                if (parameters["linetype"] != null)
                {
                    string ltName = parameters["linetype"].ToString();
                    LinetypeTable ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                    if (ltt.Has(ltName)) ent.Linetype = ltName;
                }
                if (parameters["lineweight"] != null)
                    ent.LineWeight = (LineWeight)parameters["lineweight"].Value<int>();
                if (parameters["thickness"] != null)
                {
                    double thickness = parameters["thickness"].Value<double>();
                    if (ent is Autodesk.AutoCAD.DatabaseServices.Line ln) ln.Thickness = thickness;
                    else if (ent is Autodesk.AutoCAD.DatabaseServices.Polyline pl) pl.Thickness = thickness;
                    else if (ent is Circle cir) cir.Thickness = thickness;
                    else if (ent is Arc arc) arc.Thickness = thickness;
                }

                tr.Commit();
            }

            return CommandResult.Ok($"Entity {handle} properties updated");
        }
    }

    public class OffsetEntityCommand : AcadCommand
    {
        public override string MethodName => "offset_entity";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string handle = parameters["handle"]?.ToString();
            double distance = parameters["distance"]?.Value<double>() ?? 0;
            if (string.IsNullOrEmpty(handle) || distance == 0)
                return CommandResult.Fail("Parameters 'handle' and 'distance' are required");

            string side = parameters["side"]?.ToString() ?? "both";

            Database db = doc.Database;
            JArray newHandles = new JArray();

            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Handle h = new Handle(Convert.ToInt64(handle));
                if (!db.TryGetObjectId(h, out ObjectId id))
                    return CommandResult.Fail($"Entity not found: {handle}");

                Curve curve = tr.GetObject(id, OpenMode.ForRead) as Curve;
                if (curve == null)
                    return CommandResult.Fail("Entity must be a curve (line, arc, polyline, circle, etc.)");

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                double[] offsets;
                if (side == "left") offsets = new[] { distance };
                else if (side == "right") offsets = new[] { -distance };
                else offsets = new[] { distance, -distance };

                foreach (double d in offsets)
                {
                    try
                    {
                        DBObjectCollection offsetCurves = curve.GetOffsetCurves(d);
                        foreach (DBObject obj in offsetCurves)
                        {
                            Entity offsetEnt = obj as Entity;
                            if (offsetEnt != null)
                            {
                                offsetEnt.Layer = curve.Layer;
                                ObjectId newId = ms.AppendEntity(offsetEnt);
                                tr.AddNewlyCreatedDBObject(offsetEnt, true);
                                newHandles.Add(newId.Handle.Value.ToString());
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        return CommandResult.Fail($"Offset failed: {ex.Message}");
                    }
                }

                tr.Commit();
            }

            return CommandResult.Ok(new JObject { ["handles"] = newHandles, ["count"] = newHandles.Count });
        }
    }

    public class ExplodeEntityCommand : AcadCommand
    {
        public override string MethodName => "explode_entity";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string handle = parameters["handle"]?.ToString();
            if (string.IsNullOrEmpty(handle))
                return CommandResult.Fail("Parameter 'handle' is required");

            bool eraseOriginal = parameters["erase_original"]?.Value<bool>() ?? true;
            Database db = doc.Database;
            JArray newHandles = new JArray();

            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Handle h = new Handle(Convert.ToInt64(handle));
                if (!db.TryGetObjectId(h, out ObjectId id))
                    return CommandResult.Fail($"Entity not found: {handle}");

                Entity ent = tr.GetObject(id, eraseOriginal ? OpenMode.ForWrite : OpenMode.ForRead) as Entity;
                if (ent == null)
                    return CommandResult.Fail($"Object {handle} is not an entity");

                DBObjectCollection pieces = new DBObjectCollection();
                ent.Explode(pieces);

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                foreach (DBObject obj in pieces)
                {
                    Entity piece = obj as Entity;
                    if (piece != null)
                    {
                        ObjectId newId = ms.AppendEntity(piece);
                        tr.AddNewlyCreatedDBObject(piece, true);
                        newHandles.Add(newId.Handle.Value.ToString());
                    }
                }

                if (eraseOriginal) ent.Erase();
                tr.Commit();
            }

            return CommandResult.Ok(new JObject { ["handles"] = newHandles, ["count"] = newHandles.Count });
        }
    }

    public class ArrayRectangularCommand : AcadCommand
    {
        public override string MethodName => "array_rectangular";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string handle = parameters["handle"]?.ToString();
            int rows = parameters["rows"]?.Value<int>() ?? 1;
            int columns = parameters["columns"]?.Value<int>() ?? 1;
            double rowSpacing = parameters["row_spacing"]?.Value<double>() ?? 0;
            double colSpacing = parameters["column_spacing"]?.Value<double>() ?? 0;

            if (string.IsNullOrEmpty(handle))
                return CommandResult.Fail("Parameter 'handle' is required");

            Database db = doc.Database;
            JArray newHandles = new JArray();

            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Handle h = new Handle(Convert.ToInt64(handle));
                if (!db.TryGetObjectId(h, out ObjectId id))
                    return CommandResult.Fail($"Entity not found: {handle}");

                Entity source = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (source == null) return CommandResult.Fail("Not an entity");

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < columns; c++)
                    {
                        if (r == 0 && c == 0) continue; // skip original position

                        Entity clone = source.Clone() as Entity;
                        Vector3d disp = new Vector3d(c * colSpacing, r * rowSpacing, 0);
                        clone.TransformBy(Matrix3d.Displacement(disp));
                        ObjectId newId = ms.AppendEntity(clone);
                        tr.AddNewlyCreatedDBObject(clone, true);
                        newHandles.Add(newId.Handle.Value.ToString());
                    }
                }

                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["handles"] = newHandles,
                ["count"] = newHandles.Count,
                ["total"] = rows * columns
            });
        }
    }

    public class ArrayPolarCommand : AcadCommand
    {
        public override string MethodName => "array_polar";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string handle = parameters["handle"]?.ToString();
            Point3d center = ParsePoint(parameters["center"], "center");
            int count = parameters["count"]?.Value<int>() ?? 4;
            double totalAngle = parameters["total_angle"]?.Value<double>() ?? 360;
            bool rotateItems = parameters["rotate_items"]?.Value<bool>() ?? true;

            if (string.IsNullOrEmpty(handle) || count < 2)
                return CommandResult.Fail("Parameters 'handle' and 'count' (>=2) are required");

            Database db = doc.Database;
            JArray newHandles = new JArray();
            double angleStep = (totalAngle / count) * Math.PI / 180.0;

            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Handle h = new Handle(Convert.ToInt64(handle));
                if (!db.TryGetObjectId(h, out ObjectId id))
                    return CommandResult.Fail($"Entity not found: {handle}");

                Entity source = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (source == null) return CommandResult.Fail("Not an entity");

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                for (int i = 1; i < count; i++)
                {
                    double angle = angleStep * i;
                    Entity clone = source.Clone() as Entity;

                    if (rotateItems)
                        clone.TransformBy(Matrix3d.Rotation(angle, Vector3d.ZAxis, center));
                    else
                    {
                        // Move without rotating
                        Extents3d ext = source.GeometricExtents;
                        Point3d entCenter = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2, 0);
                        double radius = center.DistanceTo(entCenter);
                        double baseAngle = Math.Atan2(entCenter.Y - center.Y, entCenter.X - center.X);
                        double newAngle = baseAngle + angle;
                        Point3d newCenter = new Point3d(center.X + radius * Math.Cos(newAngle), center.Y + radius * Math.Sin(newAngle), 0);
                        Vector3d disp = newCenter - entCenter;
                        clone.TransformBy(Matrix3d.Displacement(disp));
                    }

                    ObjectId newId = ms.AppendEntity(clone);
                    tr.AddNewlyCreatedDBObject(clone, true);
                    newHandles.Add(newId.Handle.Value.ToString());
                }

                tr.Commit();
            }

            return CommandResult.Ok(new JObject { ["handles"] = newHandles, ["count"] = newHandles.Count });
        }
    }

    public class JoinEntitiesCommand : AcadCommand
    {
        public override string MethodName => "join_entities";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            JArray handles = parameters["handles"] as JArray;
            if (handles == null || handles.Count < 2)
                return CommandResult.Fail("Parameter 'handles' requires at least 2 entity handles");

            Database db = doc.Database;
            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Get first entity
                Handle h0 = new Handle(Convert.ToInt64(handles[0].ToString()));
                if (!db.TryGetObjectId(h0, out ObjectId baseId))
                    return CommandResult.Fail($"Entity not found: {handles[0]}");

                Entity baseEnt = tr.GetObject(baseId, OpenMode.ForWrite) as Entity;
                Polyline basePline = baseEnt as Polyline;

                if (basePline == null)
                    return CommandResult.Fail("First entity must be a Polyline");

                int joined = 0;
                for (int i = 1; i < handles.Count; i++)
                {
                    Handle hi = new Handle(Convert.ToInt64(handles[i].ToString()));
                    if (!db.TryGetObjectId(hi, out ObjectId joinId)) continue;

                    Entity joinEnt = tr.GetObject(joinId, OpenMode.ForWrite) as Entity;
                    if (joinEnt == null) continue;

                    try
                    {
                        basePline.JoinEntity(joinEnt);
                        joinEnt.Erase();
                        joined++;
                    }
                    catch { /* not joinable */ }
                }

                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["handle"] = baseId.Handle.Value.ToString(),
                    ["joined"] = joined,
                    ["vertex_count"] = basePline.NumberOfVertices
                });
            }
        }
    }

    public class BulkEraseCommand : AcadCommand
    {
        public override string MethodName => "bulk_erase";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            JArray handleList = parameters["handles"] as JArray;
            string filterLayer = parameters["layer"]?.ToString();
            string filterType = parameters["type"]?.ToString();

            Database db = doc.Database;
            int erased = 0;

            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (handleList != null && handleList.Count > 0)
                {
                    foreach (var hToken in handleList)
                    {
                        try
                        {
                            Handle h = new Handle(Convert.ToInt64(hToken.ToString()));
                            if (db.TryGetObjectId(h, out ObjectId id))
                            {
                                Entity ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                                if (ent != null) { ent.Erase(); erased++; }
                            }
                        }
                        catch { }
                    }
                }
                else if (!string.IsNullOrEmpty(filterLayer) || !string.IsNullOrEmpty(filterType))
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    foreach (ObjectId id in ms)
                    {
                        Entity ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                        if (ent == null) continue;

                        bool match = true;
                        if (!string.IsNullOrEmpty(filterLayer) && !ent.Layer.Equals(filterLayer, StringComparison.OrdinalIgnoreCase))
                            match = false;
                        if (!string.IsNullOrEmpty(filterType) && !ent.GetType().Name.Equals(filterType, StringComparison.OrdinalIgnoreCase))
                            match = false;

                        if (match) { ent.Erase(); erased++; }
                    }
                }
                else
                {
                    return CommandResult.Fail("Provide 'handles' array or 'layer'/'type' filter");
                }

                tr.Commit();
            }

            return CommandResult.Ok(new JObject { ["erased"] = erased });
        }
    }

    public class UndoCommand : AcadCommand
    {
        public override string MethodName => "undo_last";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            int count = parameters["count"]?.Value<int>() ?? 1;
            doc.SendStringToExecute("._UNDO " + count + " ", true, false, false);

            return CommandResult.Ok($"Undo {count} operation(s)");
        }
    }
}
