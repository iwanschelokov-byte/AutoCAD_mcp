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
    // ========================================================================
    // Entity Query commands
    // ========================================================================

    public class ListEntitiesCommand : ICommand
    {
        public string MethodName => "list_entities";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string filterLayer = parameters?["layer"]?.ToString();
            string filterType = parameters?["type"]?.ToString();
            int limit = parameters?["limit"]?.Value<int>() ?? 500;
            int offset = parameters?["offset"]?.Value<int>() ?? 0;
            bool detailed = parameters?["detailed"]?.Value<bool>() ?? false;

            // Optional spatial filter. Supplying both points restricts the
            // listing to a region, so a drawing with tens of thousands of
            // entities can be read one sheet at a time instead of being
            // truncated at an arbitrary limit.
            bool hasWindow = parameters?["min_point"] != null && parameters?["max_point"] != null;
            double winMinX = 0, winMinY = 0, winMaxX = 0, winMaxY = 0;
            string mode = (parameters?["mode"]?.ToString() ?? "crossing").Trim().ToLowerInvariant();
            if (hasWindow)
            {
                if (mode != "window" && mode != "crossing")
                    return CommandResult.Fail($"Unknown mode '{mode}'. Use \"window\" (fully inside) or \"crossing\" (touching).");
                Point3d a = EntityHelper.ParsePoint(parameters["min_point"], "min_point");
                Point3d b = EntityHelper.ParsePoint(parameters["max_point"], "max_point");
                winMinX = Math.Min(a.X, b.X); winMinY = Math.Min(a.Y, b.Y);
                winMaxX = Math.Max(a.X, b.X); winMaxY = Math.Max(a.Y, b.Y);
            }

            Database db = doc.Database;
            JArray entities = new JArray();
            int total = 0;
            var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in modelSpace)
                {
                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    if (!string.IsNullOrEmpty(filterLayer) &&
                        !ent.Layer.Equals(filterLayer, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!EntityInfo.TypeMatches(ent, filterType))
                        continue;

                    if (hasWindow)
                    {
                        if (!EntityInfo.TryExtents(ent, out Extents3d ext)) continue;
                        bool hit = mode == "window"
                            ? EntityInfo.Inside(ext, winMinX, winMinY, winMaxX, winMaxY)
                            : EntityInfo.Crosses(ext, winMinX, winMinY, winMaxX, winMaxY);
                        if (!hit) continue;
                    }

                    total++;
                    string typeName = ent.GetType().Name;
                    byType.TryGetValue(typeName, out int n);
                    byType[typeName] = n + 1;

                    if (total <= offset) continue;
                    if (entities.Count < limit)
                        entities.Add(EntityInfo.Summarize(tr, id, ent, detailed));
                }

                tr.Commit();
            }

            var counts = new JObject();
            foreach (var kv in byType) counts[kv.Key] = kv.Value;

            var result = new JObject
            {
                ["entities"] = entities,
                ["count"] = entities.Count,
                ["total"] = total,
                ["offset"] = offset,
                // Explicit, so a caller can tell "that is all of them" from
                // "there are more, ask for the next page".
                ["truncated"] = total > offset + entities.Count,
                ["by_type"] = counts
            };
            if (hasWindow) result["mode"] = mode;
            return CommandResult.Ok(result);
        }
    }

    /// <summary>
    /// Read several entities in one call. Reading a selection of 40 handles used
    /// to mean 40 round trips through get_entity.
    /// </summary>
    public class GetEntitiesCommand : ICommand
    {
        public string MethodName => "get_entities";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            JToken handlesToken = parameters?["handles"];
            if (handlesToken == null)
                return CommandResult.Fail("Parameter 'handles' is required (array of handle strings)");

            var handles = new List<string>();
            if (handlesToken is JArray arr)
            {
                foreach (JToken t in arr)
                {
                    string s = t?.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) handles.Add(s.Trim());
                }
            }
            else
            {
                foreach (string s in handlesToken.ToString().Split(','))
                    if (!string.IsNullOrWhiteSpace(s)) handles.Add(s.Trim());
            }

            if (handles.Count == 0)
                return CommandResult.Fail("Parameter 'handles' is empty");

            bool detailed = parameters?["detailed"]?.Value<bool>() ?? true;

            Database db = doc.Database;
            var found = new JArray();
            var notFound = new JArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (string handle in handles)
                {
                    if (!TryResolve(db, handle, out ObjectId id)) { notFound.Add(handle); continue; }

                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) { notFound.Add(handle); continue; }

                    found.Add(EntityInfo.Summarize(tr, id, ent, detailed));
                }
                tr.Commit();
            }

            var result = new JObject
            {
                ["entities"] = found,
                ["count"] = found.Count,
                ["requested"] = handles.Count
            };
            if (notFound.Count > 0) result["not_found"] = notFound;
            return CommandResult.Ok(result);
        }

        /// <summary>
        /// Resolve a handle string. The plugin emits handles as decimal (that is
        /// what Handle.Value.ToString() produces), but AutoCAD's own UI shows
        /// them in hex, so accept either rather than failing on a handle the
        /// user copied out of the properties palette.
        /// </summary>
        private static bool TryResolve(Database db, string handle, out ObjectId id)
        {
            id = ObjectId.Null;

            try
            {
                if (db.TryGetObjectId(new Handle(Convert.ToInt64(handle)), out id) && !id.IsNull)
                    return true;
            }
            catch { }

            try
            {
                if (db.TryGetObjectId(new Handle(Convert.ToInt64(handle, 16)), out id) && !id.IsNull)
                    return true;
            }
            catch { }

            return false;
        }
    }

    public class GetEntityCommand : ICommand
    {
        public string MethodName => "get_entity";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string handle = parameters["handle"]?.ToString();
            if (string.IsNullOrEmpty(handle))
                return CommandResult.Fail("Parameter 'handle' is required");

            Database db = doc.Database;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId id;
                try
                {
                    Handle h = new Handle(Convert.ToInt64(handle));
                    if (!db.TryGetObjectId(h, out id))
                        return CommandResult.Fail($"Entity not found with handle: {handle}");
                }
                catch
                {
                    return CommandResult.Fail($"Invalid handle format: {handle}");
                }

                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null)
                    return CommandResult.Fail($"Object with handle {handle} is not an entity");

                // Same description used by list_entities / select_by_* so that a
                // handle looks identical wherever it turns up.
                JObject result = EntityInfo.Summarize(tr, id, ent, true);

                tr.Commit();
                return CommandResult.Ok(result);
            }
        }
    }

    // ========================================================================
    // Entity Modification commands
    // ========================================================================

    public class EraseEntityCommand : ICommand
    {
        public string MethodName => "erase_entity";

        public CommandResult Execute(JObject parameters)
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

                ent.Erase();
                tr.Commit();
            }

            return CommandResult.Ok($"Entity {handle} erased");
        }
    }

    public class MoveEntityCommand : ICommand
    {
        public string MethodName => "move_entity";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string handle = parameters["handle"]?.ToString();
            if (string.IsNullOrEmpty(handle))
                return CommandResult.Fail("Parameter 'handle' is required");

            Point3d from = EntityHelper.ParsePoint(parameters["from"], "from");
            Point3d to = EntityHelper.ParsePoint(parameters["to"], "to");
            Vector3d displacement = to - from;

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

                ent.TransformBy(Matrix3d.Displacement(displacement));
                tr.Commit();
            }

            return CommandResult.Ok($"Entity {handle} moved");
        }
    }

    public class CopyEntityCommand : ICommand
    {
        public string MethodName => "copy_entity";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string handle = parameters["handle"]?.ToString();
            if (string.IsNullOrEmpty(handle))
                return CommandResult.Fail("Parameter 'handle' is required");

            Point3d from = EntityHelper.ParsePoint(parameters["from"], "from");
            Point3d to = EntityHelper.ParsePoint(parameters["to"], "to");
            Vector3d displacement = to - from;

            Database db = doc.Database;
            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Handle h = new Handle(Convert.ToInt64(handle));
                if (!db.TryGetObjectId(h, out ObjectId id))
                    return CommandResult.Fail($"Entity not found: {handle}");

                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null)
                    return CommandResult.Fail($"Object {handle} is not an entity");

                Entity clone = ent.Clone() as Entity;
                clone.TransformBy(Matrix3d.Displacement(displacement));

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                ObjectId newId = modelSpace.AppendEntity(clone);
                tr.AddNewlyCreatedDBObject(clone, true);
                tr.Commit();

                var result = EntityHelper.EntityToJson(newId);
                result["type"] = clone.GetType().Name;
                result["message"] = $"Entity {handle} copied";
                return CommandResult.Ok(result);
            }
        }
    }

    public class RotateEntityCommand : ICommand
    {
        public string MethodName => "rotate_entity";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string handle = parameters["handle"]?.ToString();
            if (string.IsNullOrEmpty(handle))
                return CommandResult.Fail("Parameter 'handle' is required");

            Point3d basePoint = EntityHelper.ParsePoint(parameters["base_point"], "base_point");
            double angle = parameters["angle"]?.Value<double>() ?? 0;
            double radians = angle * Math.PI / 180.0;

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

                ent.TransformBy(Matrix3d.Rotation(radians, Vector3d.ZAxis, basePoint));
                tr.Commit();
            }

            return CommandResult.Ok($"Entity {handle} rotated {angle} degrees");
        }
    }

    public class ScaleEntityCommand : ICommand
    {
        public string MethodName => "scale_entity";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string handle = parameters["handle"]?.ToString();
            if (string.IsNullOrEmpty(handle))
                return CommandResult.Fail("Parameter 'handle' is required");

            Point3d basePoint = EntityHelper.ParsePoint(parameters["base_point"], "base_point");
            double factor = parameters["factor"]?.Value<double>() ?? 1.0;

            if (factor <= 0)
                return CommandResult.Fail("Parameter 'factor' must be positive");

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

                ent.TransformBy(Matrix3d.Scaling(factor, basePoint));
                tr.Commit();
            }

            return CommandResult.Ok($"Entity {handle} scaled by {factor}");
        }
    }

    public class MirrorEntityCommand : ICommand
    {
        public string MethodName => "mirror_entity";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string handle = parameters["handle"]?.ToString();
            if (string.IsNullOrEmpty(handle))
                return CommandResult.Fail("Parameter 'handle' is required");

            Point3d mirrorPt1 = EntityHelper.ParsePoint(parameters["mirror_line_start"], "mirror_line_start");
            Point3d mirrorPt2 = EntityHelper.ParsePoint(parameters["mirror_line_end"], "mirror_line_end");
            bool eraseSource = parameters["erase_source"]?.Value<bool>() ?? false;

            Database db = doc.Database;
            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Handle h = new Handle(Convert.ToInt64(handle));
                if (!db.TryGetObjectId(h, out ObjectId id))
                    return CommandResult.Fail($"Entity not found: {handle}");

                Entity ent = tr.GetObject(id, eraseSource ? OpenMode.ForWrite : OpenMode.ForRead) as Entity;
                if (ent == null)
                    return CommandResult.Fail($"Object {handle} is not an entity");

                // Create mirrored copy
                Line3d mirrorLine = new Line3d(mirrorPt1, mirrorPt2);
                Matrix3d mirrorMatrix = Matrix3d.Mirroring(mirrorLine);

                Entity mirrored = ent.Clone() as Entity;
                mirrored.TransformBy(mirrorMatrix);

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                ObjectId newId = modelSpace.AppendEntity(mirrored);
                tr.AddNewlyCreatedDBObject(mirrored, true);

                if (eraseSource)
                    ent.Erase();

                tr.Commit();

                var result = EntityHelper.EntityToJson(newId);
                result["type"] = mirrored.GetType().Name;
                result["source_erased"] = eraseSource;
                return CommandResult.Ok(result);
            }
        }
    }
}
