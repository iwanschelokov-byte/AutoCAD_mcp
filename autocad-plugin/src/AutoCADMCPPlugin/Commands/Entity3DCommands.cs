using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCADMCPPlugin.Commands
{
    // ========================================================================
    // Remaining 2D entity types
    // ========================================================================

    public class CreatePointCommand : AcadCommand
    {
        public override string MethodName => "create_point";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            Point3d p = EntityHelper.ParsePoint(
                EntityHelper.Arg(parameters, "position", "point", "location"), "position");

            using (var pt = new DBPoint(p))
            {
                ObjectId id = EntityHelper.AddToModelSpace(doc.Database, pt, parameters);
                var result = EntityHelper.EntityToJson(id);
                result["type"] = "Point";
                result["position"] = new JArray(p.X, p.Y, p.Z);
                return CommandResult.Ok(result);
            }
        }
    }

    public class CreateXlineCommand : AcadCommand
    {
        public override string MethodName => "create_xline";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            Point3d basePt = EntityHelper.ParsePoint(
                EntityHelper.Arg(parameters, "point", "base", "position"), "point");

            Vector3d dir;
            var through = EntityHelper.Arg(parameters, "through", "second_point");
            if (through != null)
            {
                Point3d p2 = EntityHelper.ParsePoint(through, "through");
                dir = p2 - basePt;
            }
            else
            {
                double angle = parameters["angle"]?.Value<double>() ?? 0;
                double rad = angle * Math.PI / 180.0;
                dir = new Vector3d(Math.Cos(rad), Math.Sin(rad), 0);
            }

            if (dir.Length < 1e-12)
                return CommandResult.BadParam("Direction is degenerate — 'through' equals 'point'");

            using (var xline = new Xline())
            {
                xline.BasePoint = basePt;
                xline.UnitDir = dir.GetNormal();
                ObjectId id = EntityHelper.AddToModelSpace(doc.Database, xline, parameters);
                var result = EntityHelper.EntityToJson(id);
                result["type"] = "Xline";
                result["base"] = new JArray(basePt.X, basePt.Y, basePt.Z);
                return CommandResult.Ok(result);
            }
        }
    }

    public class CreateRayCommand : AcadCommand
    {
        public override string MethodName => "create_ray";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            Point3d basePt = EntityHelper.ParsePoint(
                EntityHelper.Arg(parameters, "point", "base", "start", "position"), "point");

            Vector3d dir;
            var through = EntityHelper.Arg(parameters, "through", "second_point", "end");
            if (through != null)
            {
                Point3d p2 = EntityHelper.ParsePoint(through, "through");
                dir = p2 - basePt;
            }
            else
            {
                double angle = parameters["angle"]?.Value<double>() ?? 0;
                double rad = angle * Math.PI / 180.0;
                dir = new Vector3d(Math.Cos(rad), Math.Sin(rad), 0);
            }

            if (dir.Length < 1e-12)
                return CommandResult.BadParam("Direction is degenerate");

            using (var ray = new Ray())
            {
                ray.BasePoint = basePt;
                ray.UnitDir = dir.GetNormal();
                ObjectId id = EntityHelper.AddToModelSpace(doc.Database, ray, parameters);
                var result = EntityHelper.EntityToJson(id);
                result["type"] = "Ray";
                result["base"] = new JArray(basePt.X, basePt.Y, basePt.Z);
                return CommandResult.Ok(result);
            }
        }
    }

    public class CreatePolygonCommand : AcadCommand
    {
        public override string MethodName => "create_polygon";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            Point3d center = EntityHelper.ParsePoint(
                EntityHelper.Arg(parameters, "center", "position"), "center");

            int sides = parameters["sides"]?.Value<int>() ?? 0;
            double radius = parameters["radius"]?.Value<double>() ?? 0;

            if (sides < 3) return CommandResult.BadParam("Parameter 'sides' must be 3 or more");
            if (radius <= 0) return CommandResult.BadParam("Parameter 'radius' must be positive");

            // "inscribed" matches AutoCAD's POLYGON default; circumscribed pushes
            // the radius out to the edge midpoint instead of the vertex.
            bool inscribed = (parameters["mode"]?.ToString() ?? "inscribed")
                                .Equals("inscribed", StringComparison.OrdinalIgnoreCase);
            double effR = inscribed ? radius : radius / Math.Cos(Math.PI / sides);

            double startAngle = (parameters["rotation"]?.Value<double>() ?? 0) * Math.PI / 180.0;

            using (var pl = new Polyline(sides))
            {
                for (int i = 0; i < sides; i++)
                {
                    double a = startAngle + 2.0 * Math.PI * i / sides;
                    pl.AddVertexAt(i, new Point2d(
                        center.X + effR * Math.Cos(a),
                        center.Y + effR * Math.Sin(a)), 0, 0, 0);
                }
                pl.Closed = true;
                pl.Elevation = center.Z;

                ObjectId id = EntityHelper.AddToModelSpace(doc.Database, pl, parameters);
                var result = EntityHelper.EntityToJson(id);
                result["type"] = "Polygon";
                result["sides"] = sides;
                result["center"] = new JArray(center.X, center.Y, center.Z);
                result["radius"] = radius;
                return CommandResult.Ok(result);
            }
        }
    }

    public class CreateDonutCommand : AcadCommand
    {
        public override string MethodName => "create_donut";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            Point3d center = EntityHelper.ParsePoint(
                EntityHelper.Arg(parameters, "center", "position"), "center");

            double outer = parameters["outer_diameter"]?.Value<double>() ?? 0;
            double inner = parameters["inner_diameter"]?.Value<double>() ?? 0;

            if (outer <= 0) return CommandResult.BadParam("Parameter 'outer_diameter' must be positive");
            if (inner < 0) return CommandResult.BadParam("Parameter 'inner_diameter' cannot be negative");
            if (inner >= outer)
                return CommandResult.BadParam("'inner_diameter' must be smaller than 'outer_diameter'");

            // A donut is a closed 2-vertex polyline of two 180° bulges whose
            // constant width spans the ring thickness.
            double width = (outer - inner) / 2.0;
            double midRadius = (outer + inner) / 4.0;

            using (var pl = new Polyline(2))
            {
                pl.AddVertexAt(0, new Point2d(center.X - midRadius, center.Y), 1.0, width, width);
                pl.AddVertexAt(1, new Point2d(center.X + midRadius, center.Y), 1.0, width, width);
                pl.Closed = true;
                pl.Elevation = center.Z;

                ObjectId id = EntityHelper.AddToModelSpace(doc.Database, pl, parameters);
                var result = EntityHelper.EntityToJson(id);
                result["type"] = "Donut";
                result["center"] = new JArray(center.X, center.Y, center.Z);
                result["outer_diameter"] = outer;
                result["inner_diameter"] = inner;
                return CommandResult.Ok(result);
            }
        }
    }

    public class Create3dPolylineCommand : AcadCommand
    {
        public override string MethodName => "create_3d_polyline";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var ptsToken = EntityHelper.Arg(parameters, "points", "vertices") as JArray;
            if (ptsToken == null || ptsToken.Count < 2)
                return CommandResult.BadParam("Parameter 'points' needs at least 2 [x,y,z] points");

            bool closed = parameters["closed"]?.Value<bool>() ?? false;

            var pts = new Point3dCollection();
            foreach (var t in ptsToken)
                pts.Add(EntityHelper.ParsePoint(t, "points[]"));

            using (var pl = new Polyline3d(Poly3dType.SimplePoly, pts, closed))
            {
                ObjectId id = EntityHelper.AddToModelSpace(doc.Database, pl, parameters);
                var result = EntityHelper.EntityToJson(id);
                result["type"] = "Polyline3d";
                result["vertex_count"] = pts.Count;
                result["closed"] = closed;
                return CommandResult.Ok(result);
            }
        }
    }

    // ========================================================================
    // 3D solids
    // ========================================================================

    internal static class SolidHelper
    {
        /// <summary>
        /// Primitive solids are built at the origin, so every creation ends by
        /// displacing the solid to the requested centre.
        /// </summary>
        public static ObjectId Place(Database db, Solid3d solid, Point3d center, JObject parameters)
        {
            if (center != Point3d.Origin)
                solid.TransformBy(Matrix3d.Displacement(center - Point3d.Origin));
            return EntityHelper.AddToModelSpace(db, solid, parameters);
        }

        public static JObject Describe(ObjectId id, Solid3d solid, string type, Point3d center)
        {
            var o = EntityHelper.EntityToJson(id);
            o["type"] = type;
            o["center"] = new JArray(center.X, center.Y, center.Z);
            try
            {
                var mp = solid.MassProperties;
                o["volume"] = mp.Volume;
                o["centroid"] = new JArray(mp.Centroid.X, mp.Centroid.Y, mp.Centroid.Z);
            }
            catch { }
            return o;
        }
    }

    public class CreateBoxCommand : AcadCommand
    {
        public override string MethodName => "create_box";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            Point3d center = EntityHelper.ParsePoint(
                EntityHelper.Arg(parameters, "center", "position"), "center");

            double l = parameters["length"]?.Value<double>() ?? 0;
            double w = parameters["width"]?.Value<double>() ?? 0;
            double h = parameters["height"]?.Value<double>() ?? 0;

            if (l <= 0 || w <= 0 || h <= 0)
                return CommandResult.BadParam("'length', 'width' and 'height' must all be positive");

            using (var solid = new Solid3d())
            {
                solid.CreateBox(l, w, h);
                ObjectId id = SolidHelper.Place(doc.Database, solid, center, parameters);
                return CommandResult.Ok(SolidHelper.Describe(id, solid, "Box", center));
            }
        }
    }

    public class CreateSphereCommand : AcadCommand
    {
        public override string MethodName => "create_sphere";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            Point3d center = EntityHelper.ParsePoint(
                EntityHelper.Arg(parameters, "center", "position"), "center");
            double r = parameters["radius"]?.Value<double>() ?? 0;
            if (r <= 0) return CommandResult.BadParam("Parameter 'radius' must be positive");

            using (var solid = new Solid3d())
            {
                solid.CreateSphere(r);
                ObjectId id = SolidHelper.Place(doc.Database, solid, center, parameters);
                var o = SolidHelper.Describe(id, solid, "Sphere", center);
                o["radius"] = r;
                return CommandResult.Ok(o);
            }
        }
    }

    public class CreateCylinderCommand : AcadCommand
    {
        public override string MethodName => "create_cylinder";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            Point3d center = EntityHelper.ParsePoint(
                EntityHelper.Arg(parameters, "center", "position"), "center");
            double r = parameters["radius"]?.Value<double>() ?? 0;
            double h = parameters["height"]?.Value<double>() ?? 0;
            if (r <= 0) return CommandResult.BadParam("Parameter 'radius' must be positive");
            if (h <= 0) return CommandResult.BadParam("Parameter 'height' must be positive");

            using (var solid = new Solid3d())
            {
                // A frustum with equal top and bottom radii is a cylinder.
                solid.CreateFrustum(h, r, r, r);
                ObjectId id = SolidHelper.Place(doc.Database, solid, center, parameters);
                var o = SolidHelper.Describe(id, solid, "Cylinder", center);
                o["radius"] = r;
                o["height"] = h;
                return CommandResult.Ok(o);
            }
        }
    }

    public class CreateConeCommand : AcadCommand
    {
        public override string MethodName => "create_cone";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            Point3d center = EntityHelper.ParsePoint(
                EntityHelper.Arg(parameters, "center", "position"), "center");
            double r = parameters["radius"]?.Value<double>() ?? 0;
            double h = parameters["height"]?.Value<double>() ?? 0;
            double topR = parameters["top_radius"]?.Value<double>() ?? 0;

            if (r <= 0) return CommandResult.BadParam("Parameter 'radius' must be positive");
            if (h <= 0) return CommandResult.BadParam("Parameter 'height' must be positive");
            if (topR < 0) return CommandResult.BadParam("'top_radius' cannot be negative");

            using (var solid = new Solid3d())
            {
                solid.CreateFrustum(h, r, r, topR);
                ObjectId id = SolidHelper.Place(doc.Database, solid, center, parameters);
                var o = SolidHelper.Describe(id, solid, topR > 0 ? "Frustum" : "Cone", center);
                o["radius"] = r;
                o["height"] = h;
                o["top_radius"] = topR;
                return CommandResult.Ok(o);
            }
        }
    }

    public class CreateWedgeCommand : AcadCommand
    {
        public override string MethodName => "create_wedge";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            Point3d center = EntityHelper.ParsePoint(
                EntityHelper.Arg(parameters, "center", "position"), "center");

            double l = parameters["length"]?.Value<double>() ?? 0;
            double w = parameters["width"]?.Value<double>() ?? 0;
            double h = parameters["height"]?.Value<double>() ?? 0;

            if (l <= 0 || w <= 0 || h <= 0)
                return CommandResult.BadParam("'length', 'width' and 'height' must all be positive");

            using (var solid = new Solid3d())
            {
                solid.CreateWedge(l, w, h);
                ObjectId id = SolidHelper.Place(doc.Database, solid, center, parameters);
                return CommandResult.Ok(SolidHelper.Describe(id, solid, "Wedge", center));
            }
        }
    }

    public class CreateTorusCommand : AcadCommand
    {
        public override string MethodName => "create_torus";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            Point3d center = EntityHelper.ParsePoint(
                EntityHelper.Arg(parameters, "center", "position"), "center");

            double major = parameters["major_radius"]?.Value<double>() ?? 0;
            double minor = parameters["minor_radius"]?.Value<double>() ?? 0;

            if (major <= 0) return CommandResult.BadParam("'major_radius' must be positive");
            if (minor <= 0) return CommandResult.BadParam("'minor_radius' must be positive");

            using (var solid = new Solid3d())
            {
                solid.CreateTorus(major, minor);
                ObjectId id = SolidHelper.Place(doc.Database, solid, center, parameters);
                var o = SolidHelper.Describe(id, solid, "Torus", center);
                o["major_radius"] = major;
                o["minor_radius"] = minor;
                return CommandResult.Ok(o);
            }
        }
    }

    public class ExtrudeProfileCommand : AcadCommand
    {
        public override string MethodName => "extrude_profile";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var idsToken = EntityHelper.Arg(parameters, "ids", "entity_ids", "id");
            if (idsToken == null)
                return CommandResult.BadParam("Parameter 'ids' (closed profile curves) is required");

            double height = parameters["height"]?.Value<double>() ?? 0;
            if (Math.Abs(height) < 1e-12)
                return CommandResult.BadParam("Parameter 'height' must be non-zero");

            double taper = (parameters["taper_angle"]?.Value<double>() ?? 0) * Math.PI / 180.0;
            bool eraseSource = parameters["erase_source"]?.Value<bool>() ?? true;

            // Accept a single handle as well as an array.
            var arr = idsToken as JArray ?? new JArray(idsToken);
            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                List<ObjectId> ids;
                string err;
                if (!ModifyHelper.TryResolveAll(db, arr, out ids, out err))
                    return CommandResult.NotFound(err);

                var curves = new DBObjectCollection();
                foreach (ObjectId id in ids)
                {
                    var c = tr.GetObject(id, OpenMode.ForRead) as Curve;
                    if (c == null) return CommandResult.BadParam($"Entity '{id.Handle.Value}' is not a curve");
                    curves.Add(c);
                }

                DBObjectCollection regions;
                try { regions = Region.CreateFromCurves(curves); }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    return CommandResult.BadParam($"Profile must be a closed planar loop: {ex.Message}");
                }

                if (regions.Count == 0)
                    return CommandResult.BadParam("The profile curves did not form a closed region");

                var btr = ModifyHelper.GetSpaceOf(tr, ids[0]);
                var created = new JArray();

                foreach (DBObject obj in regions)
                {
                    var region = obj as Region;
                    if (region == null) continue;

                    var solid = new Solid3d();
                    try
                    {
                        solid.Extrude(region, height, taper);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex)
                    {
                        solid.Dispose();
                        region.Dispose();
                        return CommandResult.BadParam(
                            $"Extrude failed: {ex.Message}. A large taper angle can collapse the solid.");
                    }

                    ObjectId newId = btr.AppendEntity(solid);
                    tr.AddNewlyCreatedDBObject(solid, true);
                    created.Add(SolidHelper.Describe(newId, solid, "Solid3d", Point3d.Origin));
                    region.Dispose();
                }

                if (eraseSource)
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
                    ["solids"] = created,
                    ["count"] = created.Count,
                    ["height"] = height
                });
            }
        }
    }

    public class RevolveProfileCommand : AcadCommand
    {
        public override string MethodName => "revolve_profile";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var idsToken = EntityHelper.Arg(parameters, "ids", "entity_ids", "id");
            if (idsToken == null)
                return CommandResult.BadParam("Parameter 'ids' (closed profile curves) is required");

            Point3d axisPt = EntityHelper.ParsePoint(
                EntityHelper.Arg(parameters, "axis_point", "axis_origin"), "axis_point");

            Vector3d axisDir;
            var dirToken = EntityHelper.Arg(parameters, "axis_direction", "axis_vector");
            if (dirToken != null)
            {
                Point3d d = EntityHelper.ParsePoint(dirToken, "axis_direction");
                axisDir = new Vector3d(d.X, d.Y, d.Z);
            }
            else
            {
                axisDir = Vector3d.ZAxis;
            }

            if (axisDir.Length < 1e-12)
                return CommandResult.BadParam("'axis_direction' cannot be a zero vector");
            axisDir = axisDir.GetNormal();

            double angle = (parameters["angle"]?.Value<double>() ?? 360.0) * Math.PI / 180.0;
            bool eraseSource = parameters["erase_source"]?.Value<bool>() ?? true;

            var arr = idsToken as JArray ?? new JArray(idsToken);
            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                List<ObjectId> ids;
                string err;
                if (!ModifyHelper.TryResolveAll(db, arr, out ids, out err))
                    return CommandResult.NotFound(err);

                var curves = new DBObjectCollection();
                foreach (ObjectId id in ids)
                {
                    var c = tr.GetObject(id, OpenMode.ForRead) as Curve;
                    if (c == null) return CommandResult.BadParam($"Entity '{id.Handle.Value}' is not a curve");
                    curves.Add(c);
                }

                DBObjectCollection regions;
                try { regions = Region.CreateFromCurves(curves); }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    return CommandResult.BadParam($"Profile must be a closed planar loop: {ex.Message}");
                }

                if (regions.Count == 0)
                    return CommandResult.BadParam("The profile curves did not form a closed region");

                var btr = ModifyHelper.GetSpaceOf(tr, ids[0]);
                var created = new JArray();

                foreach (DBObject obj in regions)
                {
                    var region = obj as Region;
                    if (region == null) continue;

                    var solid = new Solid3d();
                    try
                    {
                        solid.Revolve(region, axisPt, axisDir, angle);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex)
                    {
                        solid.Dispose();
                        region.Dispose();
                        return CommandResult.BadParam(
                            $"Revolve failed: {ex.Message}. The axis must not pass through the profile.");
                    }

                    ObjectId newId = btr.AppendEntity(solid);
                    tr.AddNewlyCreatedDBObject(solid, true);
                    created.Add(SolidHelper.Describe(newId, solid, "Solid3d", Point3d.Origin));
                    region.Dispose();
                }

                if (eraseSource)
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
                    ["solids"] = created,
                    ["count"] = created.Count,
                    ["angle"] = parameters["angle"]?.Value<double>() ?? 360.0
                });
            }
        }
    }

    public class BooleanSolidsCommand : AcadCommand
    {
        public override string MethodName => "boolean_solids";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string targetHandle = EntityHelper.ArgString(parameters, "target", "id1", "id");
            var othersToken = EntityHelper.Arg(parameters, "others", "ids", "id2");
            string op = (parameters["operation"]?.ToString() ?? "union").Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(targetHandle))
                return CommandResult.BadParam("Parameter 'target' (the solid to modify) is required");
            if (othersToken == null)
                return CommandResult.BadParam("Parameter 'others' (solids to combine with) is required");

            BooleanOperationType boolType;
            switch (op)
            {
                case "union":
                case "unite":
                    boolType = BooleanOperationType.BoolUnite; break;
                case "subtract":
                case "difference":
                    boolType = BooleanOperationType.BoolSubtract; break;
                case "intersect":
                case "intersection":
                    boolType = BooleanOperationType.BoolIntersect; break;
                default:
                    return CommandResult.BadParam("'operation' must be union, subtract or intersect");
            }

            var arr = othersToken as JArray ?? new JArray(othersToken);
            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId targetId = EntityHelper.ResolveHandle(db, targetHandle);
                if (targetId.IsNull) return CommandResult.NotFound($"Entity '{targetHandle}' not found");

                var target = tr.GetObject(targetId, OpenMode.ForWrite) as Solid3d;
                if (target == null)
                    return CommandResult.BadParam($"Entity '{targetHandle}' is not a 3D solid");

                List<ObjectId> ids;
                string err;
                if (!ModifyHelper.TryResolveAll(db, arr, out ids, out err))
                    return CommandResult.NotFound(err);

                int combined = 0;
                foreach (ObjectId id in ids)
                {
                    if (id == targetId) continue;

                    var other = tr.GetObject(id, OpenMode.ForWrite) as Solid3d;
                    if (other == null)
                        return CommandResult.BadParam($"Entity '{id.Handle.Value}' is not a 3D solid");

                    try
                    {
                        // BooleanOperation consumes 'other' into 'target'.
                        target.BooleanOperation(boolType, other);
                        combined++;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex)
                    {
                        return CommandResult.BadParam(
                            $"Boolean {op} failed on '{id.Handle.Value}': {ex.Message}. " +
                            "Solids must overlap for subtract/intersect.");
                    }
                }

                var result = new JObject
                {
                    ["success"] = true,
                    ["id"] = targetHandle,
                    ["operation"] = op,
                    ["combined_count"] = combined
                };
                try
                {
                    var mp = target.MassProperties;
                    result["volume"] = mp.Volume;
                }
                catch { }

                tr.Commit();
                return CommandResult.Ok(result);
            }
        }
    }

    public class GetSolidPropertiesCommand : AcadCommand
    {
        public override string MethodName => "get_solid_properties";

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

                var solid = tr.GetObject(id, OpenMode.ForRead) as Solid3d;
                if (solid == null)
                    return CommandResult.BadParam($"Entity '{handle}' is not a 3D solid");

                var result = new JObject
                {
                    ["id"] = handle,
                    ["type"] = "Solid3d"
                };

                try
                {
                    var mp = solid.MassProperties;
                    result["volume"] = mp.Volume;
                    result["centroid"] = new JArray(mp.Centroid.X, mp.Centroid.Y, mp.Centroid.Z);
                    // NB: the AutoCAD API misspells this member as "Intertia".
                    // The JSON key stays correctly spelled for callers.
                    result["moments_of_inertia"] = new JArray(
                        mp.MomentsOfIntertia.X, mp.MomentsOfIntertia.Y, mp.MomentsOfIntertia.Z);
                    result["products_of_inertia"] = new JArray(
                        mp.ProductsOfIntertia.X, mp.ProductsOfIntertia.Y, mp.ProductsOfIntertia.Z);
                    result["principal_moments"] = new JArray(
                        mp.PrincipalMoments.X, mp.PrincipalMoments.Y, mp.PrincipalMoments.Z);
                    result["radii_of_gyration"] = new JArray(
                        mp.RadiiOfGyration.X, mp.RadiiOfGyration.Y, mp.RadiiOfGyration.Z);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    result["mass_properties_error"] = ex.Message;
                }

                try
                {
                    Extents3d ext = solid.GeometricExtents;
                    result["bounding_box"] = new JObject
                    {
                        ["min"] = new JArray(ext.MinPoint.X, ext.MinPoint.Y, ext.MinPoint.Z),
                        ["max"] = new JArray(ext.MaxPoint.X, ext.MaxPoint.Y, ext.MaxPoint.Z)
                    };
                }
                catch { }

                tr.Commit();
                return CommandResult.Ok(result);
            }
        }
    }
}
