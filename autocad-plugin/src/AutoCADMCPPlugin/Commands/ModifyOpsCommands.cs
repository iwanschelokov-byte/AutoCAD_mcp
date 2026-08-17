using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCADMCPPlugin.Commands
{
    internal static class ModifyHelper
    {
        /// <summary>Resolve a list of handles, reporting the first one that fails.</summary>
        public static bool TryResolveAll(Database db, JToken token, out List<ObjectId> ids, out string error)
        {
            ids = new List<ObjectId>();
            error = null;

            var arr = token as JArray;
            if (arr == null)
            {
                error = "Expected an array of entity handles";
                return false;
            }

            foreach (var t in arr)
            {
                string h = t.ToString();
                ObjectId id = EntityHelper.ResolveHandle(db, h);
                if (id.IsNull)
                {
                    error = $"Entity '{h}' not found";
                    return false;
                }
                ids.Add(id);
            }
            return true;
        }

        public static BlockTableRecord GetSpaceOf(Transaction tr, ObjectId entityId)
        {
            var ent = (Entity)tr.GetObject(entityId, OpenMode.ForRead);
            return (BlockTableRecord)tr.GetObject(ent.BlockId, OpenMode.ForWrite);
        }
    }

    // ========================================================================
    // Break / split
    // ========================================================================

    public class BreakEntityCommand : AcadCommand
    {
        public override string MethodName => "break_entity";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string handle = EntityHelper.EntityIdArg(parameters);
            if (string.IsNullOrWhiteSpace(handle))
                return CommandResult.BadParam("Parameter 'id' is required");

            var pointsToken = EntityHelper.Arg(parameters, "points", "at") as JArray;
            if (pointsToken == null || pointsToken.Count == 0)
                return CommandResult.BadParam("Parameter 'points' must be an array of [x,y] break points");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId id = EntityHelper.ResolveHandle(db, handle);
                if (id.IsNull) return CommandResult.NotFound($"Entity '{handle}' not found");

                var curve = tr.GetObject(id, OpenMode.ForWrite) as Curve;
                if (curve == null) return CommandResult.BadParam($"Entity '{handle}' is not a curve");

                var pts = new Point3dCollection();
                foreach (var p in pointsToken)
                {
                    Point3d raw = EntityHelper.ParsePoint(p, "points[]");
                    // Snap the requested point onto the curve so the split is exact.
                    pts.Add(curve.GetClosestPointTo(raw, false));
                }

                DBObjectCollection pieces;
                try
                {
                    pieces = curve.GetSplitCurves(pts);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    return CommandResult.BadParam($"Could not split this curve: {ex.Message}");
                }

                if (pieces.Count == 0)
                    return CommandResult.BadParam("Break points did not produce any segments");

                var btr = ModifyHelper.GetSpaceOf(tr, id);
                var handles = new JArray();
                foreach (DBObject obj in pieces)
                {
                    var ent = obj as Entity;
                    if (ent == null) continue;
                    ObjectId newId = btr.AppendEntity(ent);
                    tr.AddNewlyCreatedDBObject(ent, true);
                    handles.Add(newId.Handle.Value.ToString());
                }

                curve.Erase();
                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["original"] = handle,
                    ["pieces"] = handles,
                    ["count"] = handles.Count
                });
            }
        }
    }

    // ========================================================================
    // Polyline editing
    // ========================================================================

    public class ReversePolylineCommand : AcadCommand
    {
        public override string MethodName => "reverse_polyline";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string handle = EntityHelper.EntityIdArg(parameters);
            if (string.IsNullOrWhiteSpace(handle))
                return CommandResult.BadParam("Parameter 'id' is required");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId id = EntityHelper.ResolveHandle(db, handle);
                if (id.IsNull) return CommandResult.NotFound($"Entity '{handle}' not found");

                var curve = tr.GetObject(id, OpenMode.ForWrite) as Curve;
                if (curve == null) return CommandResult.BadParam($"Entity '{handle}' is not a curve");

                curve.ReverseCurve();
                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["id"] = handle,
                    ["message"] = "Curve direction reversed"
                });
            }
        }
    }

    public class PolylineEditCommand : AcadCommand
    {
        public override string MethodName => "polyline_edit";

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

                var pl = tr.GetObject(id, OpenMode.ForWrite) as Polyline;
                if (pl == null) return CommandResult.BadParam($"Entity '{handle}' is not a lightweight polyline");

                bool? close = parameters["closed"]?.Value<bool>();
                if (close.HasValue)
                {
                    pl.Closed = close.Value;
                    applied.Add(close.Value ? "closed" : "opened");
                }

                double? width = parameters["width"]?.Value<double>();
                if (width.HasValue)
                {
                    if (width.Value < 0) return CommandResult.BadParam("'width' cannot be negative");
                    pl.ConstantWidth = width.Value;
                    applied.Add("width");
                }

                double? elevation = parameters["elevation"]?.Value<double>();
                if (elevation.HasValue)
                {
                    pl.Elevation = elevation.Value;
                    applied.Add("elevation");
                }

                // Add a vertex at a position, optionally at a specific index.
                var addVertex = EntityHelper.Arg(parameters, "add_vertex");
                if (addVertex != null)
                {
                    Point3d p = EntityHelper.ParsePoint(addVertex, "add_vertex");
                    int index = parameters["index"]?.Value<int>() ?? pl.NumberOfVertices;
                    if (index < 0 || index > pl.NumberOfVertices)
                        return CommandResult.BadParam(
                            $"'index' must be between 0 and {pl.NumberOfVertices}");
                    pl.AddVertexAt(index, new Point2d(p.X, p.Y), 0, 0, 0);
                    applied.Add("add_vertex");
                }

                int? removeIndex = parameters["remove_vertex"]?.Value<int>();
                if (removeIndex.HasValue)
                {
                    if (pl.NumberOfVertices <= 2)
                        return CommandResult.BadParam("A polyline must keep at least 2 vertices");
                    if (removeIndex.Value < 0 || removeIndex.Value >= pl.NumberOfVertices)
                        return CommandResult.BadParam(
                            $"'remove_vertex' must be between 0 and {pl.NumberOfVertices - 1}");
                    pl.RemoveVertexAt(removeIndex.Value);
                    applied.Add("remove_vertex");
                }

                if (applied.Count == 0)
                {
                    return CommandResult.BadParam(
                        "Provide at least one edit: closed, width, elevation, add_vertex, remove_vertex");
                }

                var result = new JObject
                {
                    ["success"] = true,
                    ["id"] = handle,
                    ["applied"] = applied,
                    ["vertex_count"] = pl.NumberOfVertices,
                    ["closed"] = pl.Closed,
                    ["length"] = SafeLength(pl)
                };

                tr.Commit();
                return CommandResult.Ok(result);
            }
        }

        private static double SafeLength(Polyline pl)
        {
            try { return pl.Length; } catch { return 0; }
        }
    }

    // ========================================================================
    // Draw order
    // ========================================================================

    public class SetDrawOrderCommand : AcadCommand
    {
        public override string MethodName => "set_draworder";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var idsToken = EntityHelper.Arg(parameters, "ids", "entity_ids");
            if (idsToken == null)
                return CommandResult.BadParam("Parameter 'ids' (array of handles) is required");

            string position = (parameters["position"]?.ToString() ?? "top").Trim().ToLowerInvariant();

            Database db = doc.Database;
            List<ObjectId> ids;
            string err;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (!ModifyHelper.TryResolveAll(db, idsToken, out ids, out err))
                    return CommandResult.NotFound(err);
                if (ids.Count == 0)
                    return CommandResult.BadParam("'ids' must contain at least one handle");

                var btr = ModifyHelper.GetSpaceOf(tr, ids[0]);
                var dot = (DrawOrderTable)tr.GetObject(btr.DrawOrderTableId, OpenMode.ForWrite);

                var col = new ObjectIdCollection(ids.ToArray());

                switch (position)
                {
                    case "top":
                    case "front":
                        dot.MoveToTop(col);
                        break;
                    case "bottom":
                    case "back":
                        dot.MoveToBottom(col);
                        break;
                    case "above":
                    case "below":
                        {
                            string refHandle = EntityHelper.ArgString(parameters, "reference_id", "relative_to");
                            if (string.IsNullOrWhiteSpace(refHandle))
                                return CommandResult.BadParam(
                                    $"position '{position}' also needs 'reference_id'");
                            ObjectId refId = EntityHelper.ResolveHandle(db, refHandle);
                            if (refId.IsNull)
                                return CommandResult.NotFound($"Reference entity '{refHandle}' not found");
                            if (position == "above") dot.MoveAbove(col, refId);
                            else dot.MoveBelow(col, refId);
                            break;
                        }
                    default:
                        return CommandResult.BadParam(
                            "position must be one of: top, bottom, above, below");
                }

                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["count"] = ids.Count,
                    ["position"] = position
                });
            }
        }
    }

    // ========================================================================
    // Flatten
    // ========================================================================

    public class FlattenEntitiesCommand : AcadCommand
    {
        public override string MethodName => "flatten_entities";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var idsToken = EntityHelper.Arg(parameters, "ids", "entity_ids");
            double targetZ = parameters["z"]?.Value<double>() ?? 0.0;

            Database db = doc.Database;
            int flattened = 0;
            var skipped = new JArray();

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                List<ObjectId> ids;
                if (idsToken != null)
                {
                    string err;
                    if (!ModifyHelper.TryResolveAll(db, idsToken, out ids, out err))
                        return CommandResult.NotFound(err);
                }
                else
                {
                    // No explicit list: flatten everything in model space.
                    ids = new List<ObjectId>();
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms) ids.Add(id);
                }

                foreach (ObjectId id in ids)
                {
                    var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;

                    try
                    {
                        var pl = ent as Polyline;
                        if (pl != null)
                        {
                            pl.Elevation = targetZ;
                            flattened++;
                            continue;
                        }

                        Extents3d ext = ent.GeometricExtents;
                        double dz = targetZ - ext.MinPoint.Z;
                        if (Math.Abs(dz) > 1e-12)
                        {
                            ent.TransformBy(Matrix3d.Displacement(new Vector3d(0, 0, dz)));
                            flattened++;
                        }
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        skipped.Add(id.Handle.Value.ToString());
                    }
                }

                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["success"] = true,
                ["flattened"] = flattened,
                ["skipped"] = skipped,
                ["z"] = targetZ
            });
        }
    }

    // ========================================================================
    // Divide / measure — point or block placement along a curve
    // ========================================================================

    public abstract class CurvePlacementCommand : AcadCommand
    {
        /// <summary>Produce the distances along the curve at which to place markers.</summary>
        protected abstract List<double> GetDistances(Curve curve, JObject parameters, out string error);

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string handle = EntityHelper.EntityIdArg(parameters);
            if (string.IsNullOrWhiteSpace(handle))
                return CommandResult.BadParam("Parameter 'id' is required");

            string blockName = EntityHelper.ArgString(parameters, "block", "block_name");
            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId id = EntityHelper.ResolveHandle(db, handle);
                if (id.IsNull) return CommandResult.NotFound($"Entity '{handle}' not found");

                var curve = tr.GetObject(id, OpenMode.ForRead) as Curve;
                if (curve == null) return CommandResult.BadParam($"Entity '{handle}' is not a curve");

                string error;
                List<double> distances = GetDistances(curve, parameters, out error);
                if (error != null) return CommandResult.BadParam(error);

                ObjectId blockId = ObjectId.Null;
                if (!string.IsNullOrWhiteSpace(blockName))
                {
                    blockId = BlockHelper.FindBlockDef(tr, db, blockName);
                    if (blockId.IsNull) return CommandResult.NotFound($"Block '{blockName}' not found");
                }

                var btr = ModifyHelper.GetSpaceOf(tr, id);
                var placed = new JArray();

                foreach (double d in distances)
                {
                    Point3d pt;
                    try { pt = curve.GetPointAtDist(d); }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }

                    Entity marker;
                    if (blockId.IsNull)
                    {
                        marker = new DBPoint(pt);
                    }
                    else
                    {
                        marker = new BlockReference(pt, blockId);
                    }

                    string layer = parameters["layer"]?.ToString();
                    if (!string.IsNullOrEmpty(layer))
                    {
                        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        if (lt.Has(layer)) marker.Layer = layer;
                    }

                    ObjectId newId = btr.AppendEntity(marker);
                    tr.AddNewlyCreatedDBObject(marker, true);

                    placed.Add(new JObject
                    {
                        ["id"] = newId.Handle.Value.ToString(),
                        ["distance"] = d,
                        ["position"] = new JArray(pt.X, pt.Y, pt.Z)
                    });
                }

                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["curve"] = handle,
                    ["placed"] = placed,
                    ["count"] = placed.Count,
                    ["marker"] = blockId.IsNull ? "point" : blockName
                });
            }
        }

        protected static double CurveLength(Curve curve)
        {
            return curve.GetDistanceAtParameter(curve.EndParam) -
                   curve.GetDistanceAtParameter(curve.StartParam);
        }
    }

    public class DivideEntityCommand : CurvePlacementCommand
    {
        public override string MethodName => "divide_entity";

        protected override List<double> GetDistances(Curve curve, JObject parameters, out string error)
        {
            error = null;
            var list = new List<double>();

            int segments = parameters["segments"]?.Value<int>() ?? 0;
            if (segments < 2)
            {
                error = "Parameter 'segments' must be 2 or more";
                return list;
            }

            double total = CurveLength(curve);
            double step = total / segments;

            // Interior division points only — matches AutoCAD's DIVIDE.
            for (int i = 1; i < segments; i++) list.Add(step * i);
            return list;
        }
    }

    public class MeasureEntityCommand : CurvePlacementCommand
    {
        public override string MethodName => "measure_entity";

        // The "measure_" prefix reads as read-only to the classifier, but this
        // command places markers in the drawing, so correct that explicitly.
        public override bool IsWrite => true;

        protected override List<double> GetDistances(Curve curve, JObject parameters, out string error)
        {
            error = null;
            var list = new List<double>();

            double interval = parameters["interval"]?.Value<double>() ?? 0;
            if (interval <= 0)
            {
                error = "Parameter 'interval' must be positive";
                return list;
            }

            double total = CurveLength(curve);
            if (interval > total)
            {
                error = $"'interval' ({interval}) exceeds the curve length ({total:0.###})";
                return list;
            }

            for (double d = interval; d < total - 1e-9; d += interval) list.Add(d);
            return list;
        }
    }

    // ========================================================================
    // Region / boundary
    // ========================================================================

    public class CreateRegionCommand : AcadCommand
    {
        public override string MethodName => "create_region";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var idsToken = EntityHelper.Arg(parameters, "ids", "entity_ids");
            if (idsToken == null)
                return CommandResult.BadParam("Parameter 'ids' (array of closed-loop curve handles) is required");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                List<ObjectId> ids;
                string err;
                if (!ModifyHelper.TryResolveAll(db, idsToken, out ids, out err))
                    return CommandResult.NotFound(err);
                if (ids.Count == 0)
                    return CommandResult.BadParam("'ids' must contain at least one curve");

                var curves = new DBObjectCollection();
                foreach (ObjectId id in ids)
                {
                    var c = tr.GetObject(id, OpenMode.ForRead) as Curve;
                    if (c == null)
                        return CommandResult.BadParam($"Entity '{id.Handle.Value}' is not a curve");
                    curves.Add(c);
                }

                DBObjectCollection regions;
                try
                {
                    regions = Region.CreateFromCurves(curves);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    return CommandResult.BadParam(
                        $"Could not build a region — the curves must form a closed planar loop: {ex.Message}");
                }

                if (regions.Count == 0)
                    return CommandResult.BadParam("The supplied curves did not form a closed region");

                var btr = ModifyHelper.GetSpaceOf(tr, ids[0]);
                var created = new JArray();
                bool erase = parameters["erase_source"]?.Value<bool>() ?? false;

                foreach (DBObject obj in regions)
                {
                    var region = obj as Region;
                    if (region == null) continue;

                    string layer = parameters["layer"]?.ToString();
                    if (!string.IsNullOrEmpty(layer))
                    {
                        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        if (lt.Has(layer)) region.Layer = layer;
                    }

                    ObjectId newId = btr.AppendEntity(region);
                    tr.AddNewlyCreatedDBObject(region, true);
                    created.Add(new JObject
                    {
                        ["id"] = newId.Handle.Value.ToString(),
                        ["area"] = SafeArea(region)
                    });
                }

                if (erase)
                {
                    foreach (ObjectId id in ids)
                    {
                        var e = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                        if (e != null) e.Erase();
                    }
                }

                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["regions"] = created,
                    ["count"] = created.Count,
                    ["source_erased"] = erase
                });
            }
        }

        private static double SafeArea(Region r)
        {
            try { return r.Area; } catch { return 0; }
        }
    }

    public class CreateBoundaryCommand : AcadCommand
    {
        public override string MethodName => "create_boundary";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var seedToken = EntityHelper.Arg(parameters, "point", "seed", "position");
            if (seedToken == null)
                return CommandResult.BadParam("Parameter 'point' (a seed point inside the area) is required");

            Point3d seed = EntityHelper.ParsePoint(seedToken, "point");
            bool detectIslands = parameters["detect_islands"]?.Value<bool>() ?? true;

            Database db = doc.Database;
            Editor ed = doc.Editor;

            using (EntityHelper.LockDoc())
            {
                DBObjectCollection traced;
                try
                {
                    traced = ed.TraceBoundary(seed, detectIslands);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    return CommandResult.BadParam(
                        $"Could not trace a boundary at that point: {ex.Message}. " +
                        "The seed point must sit inside a fully enclosed area.");
                }

                if (traced == null || traced.Count == 0)
                    return CommandResult.BadParam(
                        "No enclosed boundary found at that point. Check that the area is fully closed.");

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                    var created = new JArray();
                    string layer = parameters["layer"]?.ToString();

                    foreach (DBObject obj in traced)
                    {
                        var ent = obj as Entity;
                        if (ent == null) continue;

                        if (!string.IsNullOrEmpty(layer))
                        {
                            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                            if (lt.Has(layer)) ent.Layer = layer;
                        }

                        ObjectId newId = space.AppendEntity(ent);
                        tr.AddNewlyCreatedDBObject(ent, true);

                        var o = new JObject { ["id"] = newId.Handle.Value.ToString() };
                        var pl = ent as Polyline;
                        if (pl != null)
                        {
                            o["length"] = SafeLen(pl);
                            o["area"] = SafeAreaOf(pl);
                        }
                        created.Add(o);
                    }

                    tr.Commit();

                    return CommandResult.Ok(new JObject
                    {
                        ["success"] = true,
                        ["boundaries"] = created,
                        ["count"] = created.Count,
                        ["seed"] = new JArray(seed.X, seed.Y, seed.Z)
                    });
                }
            }
        }

        private static double SafeLen(Polyline pl) { try { return pl.Length; } catch { return 0; } }
        private static double SafeAreaOf(Polyline pl) { try { return pl.Area; } catch { return 0; } }
    }

    // ========================================================================
    // Fillet (two lines)
    // ========================================================================

    public class FilletEntitiesCommand : AcadCommand
    {
        public override string MethodName => "fillet_entities";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string h1 = EntityHelper.ArgString(parameters, "id1", "first", "entity1");
            string h2 = EntityHelper.ArgString(parameters, "id2", "second", "entity2");
            double radius = parameters["radius"]?.Value<double>() ?? 0;

            if (string.IsNullOrWhiteSpace(h1) || string.IsNullOrWhiteSpace(h2))
                return CommandResult.BadParam("Parameters 'id1' and 'id2' are required");
            if (radius <= 0)
                return CommandResult.BadParam("Parameter 'radius' must be positive");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId id1 = EntityHelper.ResolveHandle(db, h1);
                ObjectId id2 = EntityHelper.ResolveHandle(db, h2);
                if (id1.IsNull) return CommandResult.NotFound($"Entity '{h1}' not found");
                if (id2.IsNull) return CommandResult.NotFound($"Entity '{h2}' not found");

                var l1 = tr.GetObject(id1, OpenMode.ForWrite) as Line;
                var l2 = tr.GetObject(id2, OpenMode.ForWrite) as Line;
                if (l1 == null || l2 == null)
                {
                    return CommandResult.Unsupported(
                        "fillet_entities currently supports two Line entities. " +
                        "For arcs, splines and polyline corners use AutoCAD's FILLET command via execute_command.");
                }

                // Intersection of the two infinite lines.
                var pts = new Point3dCollection();
                l1.IntersectWith(l2, Intersect.ExtendBoth, pts, IntPtr.Zero, IntPtr.Zero);
                if (pts.Count == 0)
                    return CommandResult.BadParam("The two lines are parallel and cannot be filleted");

                Point3d corner = pts[0];

                // Unit vectors pointing from the corner toward each line's far end.
                Vector3d u1 = FarEnd(l1, corner) - corner;
                Vector3d u2 = FarEnd(l2, corner) - corner;
                if (u1.Length < 1e-9 || u2.Length < 1e-9)
                    return CommandResult.BadParam("Degenerate line geometry at the corner");

                u1 = u1.GetNormal();
                u2 = u2.GetNormal();

                double angle = u1.GetAngleTo(u2);
                if (angle < 1e-6 || Math.Abs(angle - Math.PI) < 1e-6)
                    return CommandResult.BadParam("The lines are collinear and cannot be filleted");

                // Tangent distance from the corner along each leg.
                double tangentDist = radius / Math.Tan(angle / 2.0);

                if (tangentDist > l1.Length || tangentDist > l2.Length)
                {
                    return CommandResult.BadParam(
                        $"Radius {radius} is too large for these lines " +
                        $"(needs {tangentDist:0.###} of run on each leg).");
                }

                Point3d t1 = corner + u1 * tangentDist;
                Point3d t2 = corner + u2 * tangentDist;

                // Arc centre lies along the angle bisector.
                Vector3d bisector = (u1 + u2).GetNormal();
                double centreDist = radius / Math.Sin(angle / 2.0);
                Point3d centre = corner + bisector * centreDist;

                // Trim each line back to its tangent point.
                MoveNearEnd(l1, corner, t1);
                MoveNearEnd(l2, corner, t2);

                Vector3d normal = u1.CrossProduct(u2).GetNormal();
                double start = (t1 - centre).AngleOnPlane(new Plane(centre, normal));
                double end = (t2 - centre).AngleOnPlane(new Plane(centre, normal));

                var arc = new Arc(centre, normal, radius, start, end);

                var btr = ModifyHelper.GetSpaceOf(tr, id1);
                arc.Layer = l1.Layer;
                ObjectId arcId = btr.AppendEntity(arc);
                tr.AddNewlyCreatedDBObject(arc, true);

                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["arc_id"] = arcId.Handle.Value.ToString(),
                    ["radius"] = radius,
                    ["center"] = new JArray(centre.X, centre.Y, centre.Z),
                    ["tangent_points"] = new JArray(
                        new JArray(t1.X, t1.Y, t1.Z),
                        new JArray(t2.X, t2.Y, t2.Z))
                });
            }
        }

        private static Point3d FarEnd(Line l, Point3d corner)
        {
            return l.StartPoint.DistanceTo(corner) > l.EndPoint.DistanceTo(corner)
                ? l.StartPoint : l.EndPoint;
        }

        private static void MoveNearEnd(Line l, Point3d corner, Point3d newPoint)
        {
            if (l.StartPoint.DistanceTo(corner) <= l.EndPoint.DistanceTo(corner))
                l.StartPoint = newPoint;
            else
                l.EndPoint = newPoint;
        }
    }

    // ========================================================================
    // Duplicate removal (OVERKILL-style)
    // ========================================================================

    public class OverkillCommand : AcadCommand
    {
        public override string MethodName => "overkill";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            double tol = parameters["tolerance"]?.Value<double>() ?? 1e-6;
            bool ignoreLayer = parameters["ignore_layer"]?.Value<bool>() ?? false;

            Database db = doc.Database;
            var erased = new JArray();

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                var seen = new Dictionary<string, ObjectId>();

                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null || ent.IsErased) continue;

                    string sig = Signature(ent, tol, ignoreLayer);
                    if (sig == null) continue; // unsupported type — leave it alone

                    if (seen.ContainsKey(sig))
                    {
                        var dup = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                        dup.Erase();
                        erased.Add(id.Handle.Value.ToString());
                    }
                    else
                    {
                        seen[sig] = id;
                    }
                }

                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["success"] = true,
                ["erased"] = erased,
                ["erased_count"] = erased.Count,
                ["tolerance"] = tol,
                ["message"] = $"Removed {erased.Count} exact duplicate(s) from model space."
            });
        }

        /// <summary>
        /// Build a geometry signature so identical overlapping entities collapse to
        /// the same key. Returns null for types this command will not judge.
        /// </summary>
        private static string Signature(Entity ent, double tol, bool ignoreLayer)
        {
            int digits = Math.Max(0, (int)Math.Round(-Math.Log10(Math.Max(tol, 1e-12))));
            string prefix = ignoreLayer ? "" : ent.Layer + "|";

            var line = ent as Line;
            if (line != null)
            {
                // Direction-independent: a line and its reverse are duplicates.
                string a = P(line.StartPoint, digits);
                string b = P(line.EndPoint, digits);
                string lo = string.CompareOrdinal(a, b) <= 0 ? a : b;
                string hi = string.CompareOrdinal(a, b) <= 0 ? b : a;
                return $"{prefix}LINE|{lo}|{hi}";
            }

            var circle = ent as Circle;
            if (circle != null)
                return $"{prefix}CIRCLE|{P(circle.Center, digits)}|{R(circle.Radius, digits)}";

            var arc = ent as Arc;
            if (arc != null)
                return $"{prefix}ARC|{P(arc.Center, digits)}|{R(arc.Radius, digits)}|" +
                       $"{R(arc.StartAngle, digits)}|{R(arc.EndAngle, digits)}";

            var pt = ent as DBPoint;
            if (pt != null)
                return $"{prefix}POINT|{P(pt.Position, digits)}";

            var pl = ent as Polyline;
            if (pl != null)
            {
                var sb = new System.Text.StringBuilder($"{prefix}LWPOLYLINE|{pl.Closed}|");
                for (int i = 0; i < pl.NumberOfVertices; i++)
                    sb.Append(P(pl.GetPoint3dAt(i), digits)).Append(';');
                return sb.ToString();
            }

            return null;
        }

        private static string P(Point3d p, int d)
        {
            return Math.Round(p.X, d).ToString("R") + "," +
                   Math.Round(p.Y, d).ToString("R") + "," +
                   Math.Round(p.Z, d).ToString("R");
        }

        private static string R(double v, int d)
        {
            return Math.Round(v, d).ToString("R");
        }
    }
}
