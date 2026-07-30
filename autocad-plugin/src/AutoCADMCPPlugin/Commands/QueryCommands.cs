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
    public class MeasureDistanceCommand : ICommand
    {
        public string MethodName => "measure_distance";

        public CommandResult Execute(JObject parameters)
        {
            Point3d pt1 = ParsePoint(parameters["point1"], "point1");
            Point3d pt2 = ParsePoint(parameters["point2"], "point2");

            double dx = pt2.X - pt1.X;
            double dy = pt2.Y - pt1.Y;
            double distance = pt1.DistanceTo(pt2);
            double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;

            return CommandResult.Ok(new JObject
            {
                ["distance"] = distance,
                ["dx"] = dx,
                ["dy"] = dy,
                ["angle"] = angle
            });
        }
    }

    public class MeasureAreaCommand : ICommand
    {
        public string MethodName => "measure_area";

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
                if (!Handles.TryResolve(db, handle, out ObjectId id))
                    return CommandResult.Fail($"Entity not found: {handle}");

                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null)
                    return CommandResult.Fail($"Object {handle} is not an entity");

                double area = 0, perimeter = 0;
                string typeName = ent.GetType().Name;

                if (ent is Polyline pline)
                {
                    if (!pline.Closed)
                        return CommandResult.Fail("Polyline must be closed to measure area");
                    area = pline.Area;
                    perimeter = pline.Length;
                }
                else if (ent is Circle circle)
                {
                    area = circle.Area;
                    perimeter = 2 * Math.PI * circle.Radius;
                }
                else if (ent is Ellipse ellipse)
                {
                    area = ellipse.Area;
                    // Approximate perimeter using Ramanujan's formula
                    double a = ellipse.MajorRadius, b = ellipse.MinorRadius;
                    perimeter = Math.PI * (3 * (a + b) - Math.Sqrt((3 * a + b) * (a + 3 * b)));
                }
                else if (ent is Hatch hatch)
                {
                    area = hatch.Area;
                }
                else
                {
                    return CommandResult.Fail($"Cannot measure area of {typeName}. Use closed polyline, circle, ellipse, or hatch.");
                }

                tr.Commit();
                return CommandResult.Ok(new JObject
                {
                    ["area"] = area,
                    ["perimeter"] = perimeter,
                    ["type"] = typeName
                });
            }
        }
    }

    public class GetBoundingBoxCommand : ICommand
    {
        public string MethodName => "get_bounding_box";

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
                if (!Handles.TryResolve(db, handle, out ObjectId id))
                    return CommandResult.Fail($"Entity not found: {handle}");

                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null)
                    return CommandResult.Fail($"Object {handle} is not an entity");

                Extents3d ext = ent.GeometricExtents;
                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["min_point"] = new JArray(ext.MinPoint.X, ext.MinPoint.Y, ext.MinPoint.Z),
                    ["max_point"] = new JArray(ext.MaxPoint.X, ext.MaxPoint.Y, ext.MaxPoint.Z),
                    ["width"] = ext.MaxPoint.X - ext.MinPoint.X,
                    ["height"] = ext.MaxPoint.Y - ext.MinPoint.Y
                });
            }
        }
    }

    public class SelectByWindowCommand : ICommand
    {
        public string MethodName => "select_by_window";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            Point3d minPt = ParsePoint(parameters["min_point"], "min_point");
            Point3d maxPt = ParsePoint(parameters["max_point"], "max_point");
            int limit = parameters["limit"]?.Value<int>() ?? 500;
            int offset = parameters["offset"]?.Value<int>() ?? 0;
            string filterLayer = parameters["layer"]?.ToString();
            string filterType = parameters["type"]?.ToString();
            bool detailed = parameters["detailed"]?.Value<bool>() ?? false;

            // "window" (default) keeps AutoCAD's meaning: only entities fully
            // inside the box. "crossing" also returns anything the box touches —
            // which is what you actually want when picking a title block or a
            // sheet region, because those entities stick out past the frame.
            string mode = (parameters["mode"]?.ToString() ?? "window").Trim().ToLowerInvariant();
            if (mode != "window" && mode != "crossing")
                return CommandResult.Fail($"Unknown mode '{mode}'. Use \"window\" (fully inside) or \"crossing\" (touching).");

            double winMinX = Math.Min(minPt.X, maxPt.X);
            double winMinY = Math.Min(minPt.Y, maxPt.Y);
            double winMaxX = Math.Max(minPt.X, maxPt.X);
            double winMaxY = Math.Max(minPt.Y, maxPt.Y);

            Database db = doc.Database;
            JArray matches = new JArray();
            int total = 0;
            int skippedNoExtents = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    if (!string.IsNullOrEmpty(filterLayer) &&
                        !ent.Layer.Equals(filterLayer, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!EntityInfo.TypeMatches(ent, filterType))
                        continue;

                    if (!EntityInfo.TryExtents(ent, out Extents3d ext))
                    {
                        skippedNoExtents++;
                        continue;
                    }

                    bool hit = mode == "crossing"
                        ? EntityInfo.Crosses(ext, winMinX, winMinY, winMaxX, winMaxY)
                        : EntityInfo.Inside(ext, winMinX, winMinY, winMaxX, winMaxY);
                    if (!hit) continue;

                    // Count every match, emit only the requested page. Without
                    // this the caller could not tell "there are exactly 500"
                    // from "the answer was cut off at 500".
                    total++;
                    if (total <= offset) continue;
                    if (matches.Count < limit)
                        matches.Add(EntityInfo.Summarize(tr, id, ent, detailed));
                }
                tr.Commit();
            }

            var result = new JObject
            {
                ["entities"] = matches,
                ["count"] = matches.Count,
                ["total"] = total,
                ["offset"] = offset,
                ["truncated"] = total > offset + matches.Count,
                ["mode"] = mode
            };
            if (skippedNoExtents > 0) result["skipped_no_extents"] = skippedNoExtents;
            return CommandResult.Ok(result);
        }
    }

    public class SelectByPropertiesCommand : ICommand
    {
        public string MethodName => "select_by_properties";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string filterLayer = parameters["layer"]?.ToString();
            string filterType = parameters["type"]?.ToString();
            int? filterColor = parameters["color"]?.Value<int>();
            string filterLinetype = parameters["linetype"]?.ToString();
            int limit = parameters["limit"]?.Value<int>() ?? 500;
            int offset = parameters["offset"]?.Value<int>() ?? 0;
            bool detailed = parameters["detailed"]?.Value<bool>() ?? false;
            string blockName = parameters["block_name"]?.ToString();

            Database db = doc.Database;
            JArray matches = new JArray();
            int total = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    if (!string.IsNullOrEmpty(filterLayer) && !ent.Layer.Equals(filterLayer, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Matches the .NET name, the AcDb class name, the DXF name
                    // or an alias — "AcDbBlockReference", "BlockReference",
                    // "INSERT" and "block" all select the same entities.
                    if (!EntityInfo.TypeMatches(ent, filterType))
                        continue;
                    if (filterColor.HasValue && ent.ColorIndex != filterColor.Value)
                        continue;
                    if (!string.IsNullOrEmpty(filterLinetype) && !ent.Linetype.Equals(filterLinetype, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrEmpty(blockName))
                    {
                        var bref = ent as BlockReference;
                        if (bref == null) continue;
                        string bn = null;
                        try { bn = bref.Name; } catch { }
                        if (bn == null || !bn.Equals(blockName, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    total++;
                    if (total <= offset) continue;
                    if (matches.Count < limit)
                        matches.Add(EntityInfo.Summarize(tr, id, ent, detailed));
                }
                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["entities"] = matches,
                ["count"] = matches.Count,
                ["total"] = total,
                ["offset"] = offset,
                ["truncated"] = total > offset + matches.Count
            });
        }
    }

    /// <summary>
    /// Search all text entities (DBText, MText, and text inside BlockReferences) for a keyword.
    /// Returns matching text, position, layer, and handle. Case-insensitive.
    /// </summary>
    public class SearchTextCommand : ICommand
    {
        public string MethodName => "search_text";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string keyword = parameters["keyword"]?.ToString();
            if (string.IsNullOrEmpty(keyword))
                return CommandResult.Fail("Parameter 'keyword' is required");

            bool caseSensitive = parameters["case_sensitive"]?.Value<bool>() ?? false;
            int limit = parameters["limit"]?.Value<int>() ?? 100;
            StringComparison cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            Database db = doc.Database;
            JArray matches = new JArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    if (matches.Count >= limit) break;
                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    // Check DBText
                    if (ent is DBText dbt)
                    {
                        if (dbt.TextString != null && dbt.TextString.IndexOf(keyword, cmp) >= 0)
                        {
                            matches.Add(new JObject
                            {
                                ["handle"] = Handles.Format(id),
                                ["type"] = "DBText",
                                ["text"] = dbt.TextString,
                                ["position"] = new JArray(dbt.Position.X, dbt.Position.Y, dbt.Position.Z),
                                ["layer"] = ent.Layer,
                                ["height"] = dbt.Height
                            });
                        }
                    }
                    // Check MText
                    else if (ent is MText mt)
                    {
                        string plainText = mt.Text ?? "";
                        // Strip formatting codes for search
                        string searchable = System.Text.RegularExpressions.Regex.Replace(plainText, @"\\[A-Za-z][^;]*;|\\[Pp]|\\W[^;]*;|\{|\}", "");
                        if (searchable.IndexOf(keyword, cmp) >= 0 || plainText.IndexOf(keyword, cmp) >= 0)
                        {
                            matches.Add(new JObject
                            {
                                ["handle"] = Handles.Format(id),
                                ["type"] = "MText",
                                ["text"] = plainText,
                                ["text_clean"] = searchable.Trim(),
                                ["position"] = new JArray(mt.Location.X, mt.Location.Y, mt.Location.Z),
                                ["layer"] = ent.Layer,
                                ["height"] = mt.TextHeight
                            });
                        }
                    }
                    // Check BlockReference — read attribute values
                    else if (ent is BlockReference bref)
                    {
                        // Check block name itself
                        bool blockNameMatch = bref.Name != null && bref.Name.IndexOf(keyword, cmp) >= 0;

                        // Check attribute values inside the block
                        string matchedAttr = null;
                        foreach (ObjectId attId in bref.AttributeCollection)
                        {
                            AttributeReference att = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                            if (att != null && att.TextString != null && att.TextString.IndexOf(keyword, cmp) >= 0)
                            {
                                matchedAttr = att.TextString;
                                break;
                            }
                        }

                        if (blockNameMatch || matchedAttr != null)
                        {
                            var obj = new JObject
                            {
                                ["handle"] = Handles.Format(id),
                                ["type"] = "BlockReference",
                                ["block_name"] = bref.Name,
                                ["position"] = new JArray(bref.Position.X, bref.Position.Y, bref.Position.Z),
                                ["layer"] = ent.Layer
                            };
                            if (matchedAttr != null)
                                obj["matched_attribute"] = matchedAttr;
                            if (blockNameMatch)
                                obj["matched_block_name"] = true;
                            matches.Add(obj);
                        }
                    }
                }
                tr.Commit();
            }

            return CommandResult.Ok(new JObject { ["matches"] = matches, ["count"] = matches.Count, ["keyword"] = keyword });
        }
    }

    /// <summary>
    /// Find entities nearest to a given point. Optionally filter by type/layer.
    /// Returns entities sorted by distance from the point.
    /// </summary>
    public class FindNearestCommand : ICommand
    {
        public string MethodName => "find_nearest";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            Point3d searchPt = ParsePoint(parameters["point"], "point");
            double radius = parameters["radius"]?.Value<double>() ?? double.MaxValue;
            string filterType = parameters["type"]?.ToString();
            string filterLayer = parameters["layer"]?.ToString();
            int limit = parameters["limit"]?.Value<int>() ?? 20;

            Database db = doc.Database;
            var candidates = new List<Tuple<double, JObject>>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    if (!EntityInfo.TypeMatches(ent, filterType))
                        continue;
                    if (!string.IsNullOrEmpty(filterLayer) && !ent.Layer.Equals(filterLayer, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        Extents3d ext = ent.GeometricExtents;
                        // Use center of bounding box as entity position
                        Point3d center = new Point3d(
                            (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                            (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0,
                            0);
                        double dist = searchPt.DistanceTo(center);

                        if (dist <= radius)
                        {
                            var obj = new JObject
                            {
                                ["handle"] = Handles.Format(id),
                                ["type"] = ent.GetType().Name,
                                ["dxf_type"] = EntityInfo.DxfName(ent),
                                ["layer"] = ent.Layer,
                                ["distance"] = Math.Round(dist, 2),
                                ["center"] = new JArray(Math.Round(center.X, 2), Math.Round(center.Y, 2))
                            };

                            // Add text content if it's a text entity
                            if (ent is DBText dbt) obj["text"] = dbt.TextString;
                            else if (ent is MText mt) obj["text"] = mt.Text;
                            else if (ent is BlockReference br) obj["block_name"] = br.Name;

                            candidates.Add(Tuple.Create(dist, obj));
                        }
                    }
                    catch { }
                }
                tr.Commit();
            }

            // Sort by distance, take top N
            candidates.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            JArray results = new JArray();
            for (int i = 0; i < Math.Min(limit, candidates.Count); i++)
                results.Add(candidates[i].Item2);

            return CommandResult.Ok(new JObject { ["entities"] = results, ["count"] = results.Count });
        }
    }

    /// <summary>
    /// Measure the distance between two entities (center-to-center or closest approach).
    /// </summary>
    public class MeasureBetweenCommand : ICommand
    {
        public string MethodName => "measure_between";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string handle1 = parameters["handle1"]?.ToString();
            string handle2 = parameters["handle2"]?.ToString();
            if (string.IsNullOrEmpty(handle1) || string.IsNullOrEmpty(handle2))
                return CommandResult.Fail("Parameters 'handle1' and 'handle2' are required");

            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (!Handles.TryResolve(db, handle1, out ObjectId id1)) return CommandResult.Fail($"Entity not found: {handle1}");
                if (!Handles.TryResolve(db, handle2, out ObjectId id2)) return CommandResult.Fail($"Entity not found: {handle2}");

                Entity ent1 = tr.GetObject(id1, OpenMode.ForRead) as Entity;
                Entity ent2 = tr.GetObject(id2, OpenMode.ForRead) as Entity;
                if (ent1 == null || ent2 == null)
                    return CommandResult.Fail("Both handles must refer to valid entities");

                Extents3d ext1 = ent1.GeometricExtents;
                Extents3d ext2 = ent2.GeometricExtents;

                Point3d center1 = new Point3d((ext1.MinPoint.X + ext1.MaxPoint.X) / 2.0, (ext1.MinPoint.Y + ext1.MaxPoint.Y) / 2.0, 0);
                Point3d center2 = new Point3d((ext2.MinPoint.X + ext2.MaxPoint.X) / 2.0, (ext2.MinPoint.Y + ext2.MaxPoint.Y) / 2.0, 0);

                double centerDist = center1.DistanceTo(center2);
                double dx = center2.X - center1.X;
                double dy = center2.Y - center1.Y;

                // Try closest point approach for curves
                double closestDist = centerDist;
                Point3d closestPt1 = center1, closestPt2 = center2;

                if (ent1 is Curve c1 && ent2 is Curve c2)
                {
                    try
                    {
                        closestPt1 = c1.GetClosestPointTo(center2, false);
                        closestPt2 = c2.GetClosestPointTo(closestPt1, false);
                        closestDist = closestPt1.DistanceTo(closestPt2);
                    }
                    catch { }
                }

                // Get entity descriptions
                string desc1 = ent1.GetType().Name;
                string desc2 = ent2.GetType().Name;
                if (ent1 is DBText t1) desc1 += ": " + t1.TextString;
                else if (ent1 is MText m1) desc1 += ": " + m1.Text?.Substring(0, Math.Min(40, m1.Text.Length));
                else if (ent1 is BlockReference b1) desc1 += ": " + b1.Name;
                if (ent2 is DBText t2) desc2 += ": " + t2.TextString;
                else if (ent2 is MText m2) desc2 += ": " + m2.Text?.Substring(0, Math.Min(40, m2.Text.Length));
                else if (ent2 is BlockReference b2) desc2 += ": " + b2.Name;

                tr.Commit();
                return CommandResult.Ok(new JObject
                {
                    ["center_distance"] = Math.Round(centerDist, 2),
                    ["closest_distance"] = Math.Round(closestDist, 2),
                    ["dx"] = Math.Round(dx, 2),
                    ["dy"] = Math.Round(dy, 2),
                    ["entity1"] = new JObject { ["handle"] = handle1, ["type"] = desc1, ["center"] = new JArray(Math.Round(center1.X, 2), Math.Round(center1.Y, 2)) },
                    ["entity2"] = new JObject { ["handle"] = handle2, ["type"] = desc2, ["center"] = new JArray(Math.Round(center2.X, 2), Math.Round(center2.Y, 2)) }
                });
            }
        }
    }

    public class FindIntersectionsCommand : ICommand
    {
        public string MethodName => "find_intersections";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string handle1 = parameters["handle1"]?.ToString();
            string handle2 = parameters["handle2"]?.ToString();
            if (string.IsNullOrEmpty(handle1) || string.IsNullOrEmpty(handle2))
                return CommandResult.Fail("Parameters 'handle1' and 'handle2' are required");

            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (!Handles.TryResolve(db, handle1, out ObjectId id1)) return CommandResult.Fail($"Entity not found: {handle1}");
                if (!Handles.TryResolve(db, handle2, out ObjectId id2)) return CommandResult.Fail($"Entity not found: {handle2}");

                Entity ent1 = tr.GetObject(id1, OpenMode.ForRead) as Entity;
                Entity ent2 = tr.GetObject(id2, OpenMode.ForRead) as Entity;

                if (!(ent1 is Curve) || !(ent2 is Curve))
                    return CommandResult.Fail("Both entities must be curve-type (line, arc, circle, polyline, etc.)");

                Point3dCollection points = new Point3dCollection();
                ent1.IntersectWith(ent2, Intersect.OnBothOperands, points, IntPtr.Zero, IntPtr.Zero);

                JArray pts = new JArray();
                foreach (Point3d pt in points)
                    pts.Add(new JArray(pt.X, pt.Y, pt.Z));

                tr.Commit();
                return CommandResult.Ok(new JObject { ["points"] = pts, ["count"] = pts.Count });
            }
        }
    }
}
