using System;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCADMCPPlugin.Commands
{
    // ========================================================================
    // Helper for common entity creation patterns
    // ========================================================================
    internal static class EntityHelper
    {
        /// <summary>
        /// Lock the active document for write access.
        /// Required when modifying the drawing from a non-command context (e.g., Idle event).
        /// </summary>
        public static DocumentLock LockDoc()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            return doc?.LockDocument();
        }

        /// <summary>
        /// Add an entity to model space within a transaction.
        /// Optionally assigns layer and color.
        /// Returns the ObjectId of the new entity.
        /// </summary>
        public static ObjectId AddToModelSpace(Database db, Entity entity, JObject parameters)
        {
            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // Set layer if specified
                string layer = parameters?["layer"]?.ToString();
                if (!string.IsNullOrEmpty(layer))
                {
                    LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (lt.Has(layer))
                        entity.Layer = layer;
                }

                // Set color if specified (ACI index 0-255)
                int? color = parameters?["color"]?.Value<int>();
                if (color.HasValue && color.Value >= 0 && color.Value <= 255)
                {
                    entity.ColorIndex = color.Value;
                }

                ObjectId id = modelSpace.AppendEntity(entity);
                tr.AddNewlyCreatedDBObject(entity, true);
                tr.Commit();

                return id;
            }
        }

        public static Point3d ParsePoint(JToken token, string name = "point")
        {
            if (token == null)
                throw new ArgumentException($"Parameter '{name}' is required");

            if (token is JArray arr && arr.Count >= 2)
            {
                double x = arr[0].Value<double>();
                double y = arr[1].Value<double>();
                double z = arr.Count >= 3 ? arr[2].Value<double>() : 0.0;
                return new Point3d(x, y, z);
            }

            if (token is JObject obj)
            {
                double x = obj["x"]?.Value<double>() ?? 0;
                double y = obj["y"]?.Value<double>() ?? 0;
                double z = obj["z"]?.Value<double>() ?? 0;
                return new Point3d(x, y, z);
            }

            throw new ArgumentException($"Parameter '{name}' must be [x,y] array or {{x,y,z}} object");
        }

        public static JObject EntityToJson(ObjectId id)
        {
            return new JObject
            {
                ["id"] = id.Handle.Value.ToString(),
                ["object_id"] = id.ToString()
            };
        }
    }

    // ========================================================================
    // CREATE commands
    // ========================================================================

    public class CreateLineCommand : ICommand
    {
        public string MethodName => "create_line";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            Point3d start = EntityHelper.ParsePoint(parameters["start"], "start");
            Point3d end = EntityHelper.ParsePoint(parameters["end"], "end");

            using (Line line = new Line(start, end))
            {
                ObjectId id = EntityHelper.AddToModelSpace(doc.Database, line, parameters);
                var result = EntityHelper.EntityToJson(id);
                result["type"] = "Line";
                result["start"] = new JArray(start.X, start.Y, start.Z);
                result["end"] = new JArray(end.X, end.Y, end.Z);
                return CommandResult.Ok(result);
            }
        }
    }

    public class CreateCircleCommand : ICommand
    {
        public string MethodName => "create_circle";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            Point3d center = EntityHelper.ParsePoint(parameters["center"], "center");
            double radius = parameters["radius"]?.Value<double>() ?? 0;

            if (radius <= 0)
                return CommandResult.Fail("Parameter 'radius' must be positive");

            using (Circle circle = new Circle(center, Vector3d.ZAxis, radius))
            {
                ObjectId id = EntityHelper.AddToModelSpace(doc.Database, circle, parameters);
                var result = EntityHelper.EntityToJson(id);
                result["type"] = "Circle";
                result["center"] = new JArray(center.X, center.Y, center.Z);
                result["radius"] = radius;
                return CommandResult.Ok(result);
            }
        }
    }

    public class CreateArcCommand : ICommand
    {
        public string MethodName => "create_arc";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            Point3d center = EntityHelper.ParsePoint(parameters["center"], "center");
            double radius = parameters["radius"]?.Value<double>() ?? 0;
            double startAngle = parameters["start_angle"]?.Value<double>() ?? 0;
            double endAngle = parameters["end_angle"]?.Value<double>() ?? Math.PI;

            if (radius <= 0)
                return CommandResult.Fail("Parameter 'radius' must be positive");

            // Convert degrees to radians if specified
            bool useDegrees = parameters["degrees"]?.Value<bool>() ?? false;
            if (useDegrees)
            {
                startAngle = startAngle * Math.PI / 180.0;
                endAngle = endAngle * Math.PI / 180.0;
            }

            using (Arc arc = new Arc(center, radius, startAngle, endAngle))
            {
                ObjectId id = EntityHelper.AddToModelSpace(doc.Database, arc, parameters);
                var result = EntityHelper.EntityToJson(id);
                result["type"] = "Arc";
                return CommandResult.Ok(result);
            }
        }
    }

    public class CreatePolylineCommand : ICommand
    {
        public string MethodName => "create_polyline";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            JArray points = parameters["points"] as JArray;
            if (points == null || points.Count < 2)
                return CommandResult.Fail("Parameter 'points' requires at least 2 points");

            bool closed = parameters["closed"]?.Value<bool>() ?? false;

            using (Polyline pline = new Polyline())
            {
                for (int i = 0; i < points.Count; i++)
                {
                    Point3d pt = EntityHelper.ParsePoint(points[i], $"points[{i}]");
                    pline.AddVertexAt(i, new Point2d(pt.X, pt.Y), 0, 0, 0);
                }

                if (closed) pline.Closed = true;

                ObjectId id = EntityHelper.AddToModelSpace(doc.Database, pline, parameters);
                var result = EntityHelper.EntityToJson(id);
                result["type"] = "Polyline";
                result["vertex_count"] = points.Count;
                result["closed"] = closed;
                return CommandResult.Ok(result);
            }
        }
    }

    public class CreateRectangleCommand : ICommand
    {
        public string MethodName => "create_rectangle";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            Point3d corner1 = EntityHelper.ParsePoint(parameters["corner1"], "corner1");
            Point3d corner2 = EntityHelper.ParsePoint(parameters["corner2"], "corner2");

            using (Polyline rect = new Polyline())
            {
                rect.AddVertexAt(0, new Point2d(corner1.X, corner1.Y), 0, 0, 0);
                rect.AddVertexAt(1, new Point2d(corner2.X, corner1.Y), 0, 0, 0);
                rect.AddVertexAt(2, new Point2d(corner2.X, corner2.Y), 0, 0, 0);
                rect.AddVertexAt(3, new Point2d(corner1.X, corner2.Y), 0, 0, 0);
                rect.Closed = true;

                ObjectId id = EntityHelper.AddToModelSpace(doc.Database, rect, parameters);
                var result = EntityHelper.EntityToJson(id);
                result["type"] = "Rectangle";
                result["corner1"] = new JArray(corner1.X, corner1.Y);
                result["corner2"] = new JArray(corner2.X, corner2.Y);
                return CommandResult.Ok(result);
            }
        }
    }

    public class CreateEllipseCommand : ICommand
    {
        public string MethodName => "create_ellipse";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            Point3d center = EntityHelper.ParsePoint(parameters["center"], "center");
            double majorRadius = parameters["major_radius"]?.Value<double>() ?? 0;
            double minorRadius = parameters["minor_radius"]?.Value<double>() ?? 0;

            if (majorRadius <= 0 || minorRadius <= 0)
                return CommandResult.Fail("Both 'major_radius' and 'minor_radius' must be positive");

            double radiusRatio = minorRadius / majorRadius;
            Vector3d majorAxis = new Vector3d(majorRadius, 0, 0);

            using (Ellipse ellipse = new Ellipse(center, Vector3d.ZAxis, majorAxis, radiusRatio, 0, 2 * Math.PI))
            {
                ObjectId id = EntityHelper.AddToModelSpace(doc.Database, ellipse, parameters);
                var result = EntityHelper.EntityToJson(id);
                result["type"] = "Ellipse";
                return CommandResult.Ok(result);
            }
        }
    }

    public class CreateTextCommand : ICommand
    {
        public string MethodName => "create_text";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string text = parameters["text"]?.ToString();
            if (string.IsNullOrEmpty(text))
                return CommandResult.Fail("Parameter 'text' is required");

            Point3d position = EntityHelper.ParsePoint(parameters["position"], "position");
            double height = parameters["height"]?.Value<double>() ?? 2.5;
            double rotation = parameters["rotation"]?.Value<double>() ?? 0;

            using (DBText dbText = new DBText())
            {
                dbText.Position = position;
                dbText.TextString = text;
                dbText.Height = height;
                dbText.Rotation = rotation * Math.PI / 180.0; // degrees to radians

                ObjectId id = EntityHelper.AddToModelSpace(doc.Database, dbText, parameters);
                var result = EntityHelper.EntityToJson(id);
                result["type"] = "Text";
                result["text"] = text;
                return CommandResult.Ok(result);
            }
        }
    }

    public class CreateMTextCommand : ICommand
    {
        public string MethodName => "create_mtext";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string text = parameters["text"]?.ToString();
            if (string.IsNullOrEmpty(text))
                return CommandResult.Fail("Parameter 'text' is required");

            Point3d position = EntityHelper.ParsePoint(parameters["position"], "position");
            double height = parameters["height"]?.Value<double>() ?? 2.5;
            double width = parameters["width"]?.Value<double>() ?? 0;

            using (MText mtext = new MText())
            {
                mtext.Location = position;
                mtext.Contents = text;
                mtext.TextHeight = height;
                if (width > 0) mtext.Width = width;

                ObjectId id = EntityHelper.AddToModelSpace(doc.Database, mtext, parameters);
                var result = EntityHelper.EntityToJson(id);
                result["type"] = "MText";
                result["text"] = text;
                return CommandResult.Ok(result);
            }
        }
    }

    public class CreateHatchCommand : ICommand
    {
        public string MethodName => "create_hatch";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string pattern = parameters["pattern"]?.ToString() ?? "ANSI31";
            double scale = parameters["scale"]?.Value<double>() ?? 1.0;

            // Hatch needs a boundary — accept an array of points for a polyline boundary
            JArray boundaryPoints = parameters["boundary"] as JArray;
            if (boundaryPoints == null || boundaryPoints.Count < 3)
                return CommandResult.Fail("Parameter 'boundary' requires at least 3 points");

            Database db = doc.Database;
            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // Create boundary polyline
                Polyline boundary = new Polyline();
                for (int i = 0; i < boundaryPoints.Count; i++)
                {
                    Point3d pt = EntityHelper.ParsePoint(boundaryPoints[i], $"boundary[{i}]");
                    boundary.AddVertexAt(i, new Point2d(pt.X, pt.Y), 0, 0, 0);
                }
                boundary.Closed = true;

                ObjectId boundaryId = modelSpace.AppendEntity(boundary);
                tr.AddNewlyCreatedDBObject(boundary, true);

                // Create hatch
                Hatch hatch = new Hatch();
                modelSpace.AppendEntity(hatch);
                tr.AddNewlyCreatedDBObject(hatch, true);

                hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);
                hatch.PatternScale = scale;
                hatch.Associative = true;
                hatch.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection { boundaryId });
                hatch.EvaluateHatch(true);

                string layer = parameters["layer"]?.ToString();
                if (!string.IsNullOrEmpty(layer))
                {
                    LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (lt.Has(layer))
                    {
                        hatch.Layer = layer;
                        boundary.Layer = layer;
                    }
                }

                // Optional ACI colour override (0–255). Applied to BOTH the
                // hatch and its boundary polyline so engineers don't see a
                // stray ByLayer outline on top of a coloured fill.
                int? aci = parameters["color"]?.Value<int>();
                if (aci.HasValue && aci.Value >= 0 && aci.Value <= 255)
                {
                    hatch.ColorIndex = aci.Value;
                    boundary.ColorIndex = aci.Value;
                }

                // Optional true-colour [r, g, b] override. Takes precedence
                // over the ACI index when both are provided — pinned colours
                // (e.g. a corporate brand green) need full RGB, not the
                // 256-entry ACI table's nearest match.
                JArray rgb = parameters["true_color"] as JArray;
                if (rgb != null && rgb.Count == 3)
                {
                    try
                    {
                        byte rr = (byte)Math.Max(0, Math.Min(255, rgb[0].Value<int>()));
                        byte gg = (byte)Math.Max(0, Math.Min(255, rgb[1].Value<int>()));
                        byte bb = (byte)Math.Max(0, Math.Min(255, rgb[2].Value<int>()));
                        Color trueColor = Color.FromRgb(rr, gg, bb);
                        hatch.Color = trueColor;
                        boundary.Color = trueColor;
                    }
                    catch { /* ignore malformed colour */ }
                }

                tr.Commit();

                var result = new JObject
                {
                    ["id"] = hatch.ObjectId.Handle.Value.ToString(),
                    ["type"] = "Hatch",
                    ["pattern"] = pattern,
                    ["scale"] = scale
                };
                return CommandResult.Ok(result);
            }
        }
    }
}
