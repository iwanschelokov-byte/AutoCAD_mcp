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
    public class CreateAngularDimensionCommand : AcadCommand
    {
        public override string MethodName => "create_angular_dimension";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            Point3d center = ParsePoint(parameters["center"], "center");
            Point3d pt1 = ParsePoint(parameters["point1"], "point1");
            Point3d pt2 = ParsePoint(parameters["point2"], "point2");
            Point3d arcPt = ParsePoint(parameters["dimension_arc_position"], "dimension_arc_position");

            Database db = doc.Database;
            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                using (Point3AngularDimension dim = new Point3AngularDimension(center, pt1, pt2, arcPt, "", db.Dimstyle))
                {
                    string text = parameters["text"]?.ToString();
                    if (!string.IsNullOrEmpty(text)) dim.DimensionText = text;

                    string layer = parameters["layer"]?.ToString();
                    if (!string.IsNullOrEmpty(layer))
                    {
                        LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        if (lt.Has(layer)) dim.Layer = layer;
                    }

                    ObjectId id = ms.AppendEntity(dim);
                    tr.AddNewlyCreatedDBObject(dim, true);
                    tr.Commit();

                    var result = EntityToJson(id);
                    result["type"] = "AngularDimension";
                    result["measurement"] = dim.Measurement;
                    return CommandResult.Ok(result);
                }
            }
        }
    }

    public class CreateRadialDimensionCommand : AcadCommand
    {
        public override string MethodName => "create_radial_dimension";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            Point3d center = ParsePoint(parameters["center"], "center");
            Point3d chordPt = ParsePoint(parameters["chord_point"], "chord_point");
            double leaderLen = parameters["leader_length"]?.Value<double>() ?? 0;

            Database db = doc.Database;
            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                using (RadialDimension dim = new RadialDimension(center, chordPt, leaderLen, "", db.Dimstyle))
                {
                    string text = parameters["text"]?.ToString();
                    if (!string.IsNullOrEmpty(text)) dim.DimensionText = text;

                    string layer = parameters["layer"]?.ToString();
                    if (!string.IsNullOrEmpty(layer))
                    {
                        LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        if (lt.Has(layer)) dim.Layer = layer;
                    }

                    ObjectId id = ms.AppendEntity(dim);
                    tr.AddNewlyCreatedDBObject(dim, true);
                    tr.Commit();

                    var result = EntityToJson(id);
                    result["type"] = "RadialDimension";
                    result["measurement"] = dim.Measurement;
                    return CommandResult.Ok(result);
                }
            }
        }
    }

    public class CreateDiameterDimensionCommand : AcadCommand
    {
        public override string MethodName => "create_diameter_dimension";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            Point3d center = ParsePoint(parameters["center"], "center");
            Point3d chordPt = ParsePoint(parameters["chord_point"], "chord_point");
            double leaderLen = parameters["leader_length"]?.Value<double>() ?? 0;

            // Far chord point is opposite side of the circle
            Point3d farPt = new Point3d(2 * center.X - chordPt.X, 2 * center.Y - chordPt.Y, 2 * center.Z - chordPt.Z);

            Database db = doc.Database;
            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                using (DiametricDimension dim = new DiametricDimension(chordPt, farPt, leaderLen, "", db.Dimstyle))
                {
                    string text = parameters["text"]?.ToString();
                    if (!string.IsNullOrEmpty(text)) dim.DimensionText = text;

                    string layer = parameters["layer"]?.ToString();
                    if (!string.IsNullOrEmpty(layer))
                    {
                        LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        if (lt.Has(layer)) dim.Layer = layer;
                    }

                    ObjectId id = ms.AppendEntity(dim);
                    tr.AddNewlyCreatedDBObject(dim, true);
                    tr.Commit();

                    var result = EntityToJson(id);
                    result["type"] = "DiameterDimension";
                    result["measurement"] = dim.Measurement;
                    return CommandResult.Ok(result);
                }
            }
        }
    }

    public class CreateLeaderCommand : AcadCommand
    {
        public override string MethodName => "create_leader";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            JArray points = parameters["points"] as JArray;
            if (points == null || points.Count < 2)
                return CommandResult.Fail("Parameter 'points' requires at least 2 points (arrow tip and landing)");

            string text = parameters["text"]?.ToString() ?? "";
            double textHeight = parameters["text_height"]?.Value<double>() ?? 2.5;

            Database db = doc.Database;
            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                MLeader leader = new MLeader();
                leader.SetDatabaseDefaults();
                leader.ContentType = ContentType.MTextContent;

                // Add leader line with vertices
                int leaderIdx = leader.AddLeader();
                int lineIdx = leader.AddLeaderLine(leaderIdx);

                for (int i = 0; i < points.Count; i++)
                {
                    Point3d pt = ParsePoint(points[i], $"points[{i}]");
                    leader.AddFirstVertex(lineIdx, pt);
                }

                // Set text content
                MText mt = new MText();
                mt.SetDatabaseDefaults();
                mt.Contents = text;
                mt.TextHeight = textHeight;
                Point3d lastPt = ParsePoint(points[points.Count - 1], "last_point");
                mt.Location = lastPt;
                leader.MText = mt;

                string layer = parameters["layer"]?.ToString();
                if (!string.IsNullOrEmpty(layer))
                {
                    LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (lt.Has(layer)) leader.Layer = layer;
                }

                ObjectId id = ms.AppendEntity(leader);
                tr.AddNewlyCreatedDBObject(leader, true);
                tr.Commit();

                var result = EntityToJson(id);
                result["type"] = "MLeader";
                result["text"] = text;
                return CommandResult.Ok(result);
            }
        }
    }

    public class CreateSplineCommand : AcadCommand
    {
        public override string MethodName => "create_spline";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            JArray points = parameters["points"] as JArray;
            if (points == null || points.Count < 2)
                return CommandResult.Fail("Parameter 'points' requires at least 2 points");

            bool closed = parameters["closed"]?.Value<bool>() ?? false;

            Point3dCollection pts = new Point3dCollection();
            for (int i = 0; i < points.Count; i++)
                pts.Add(ParsePoint(points[i], $"points[{i}]"));

            // Create fit spline — if closed, add first point again at end
            if (closed && pts.Count >= 3)
                pts.Add(pts[0]);
            Spline spline = new Spline(pts, 3, 0.0);

            ObjectId id = AddToModelSpace(doc.Database, spline, parameters);
            var result = EntityToJson(id);
            result["type"] = "Spline";
            result["point_count"] = points.Count;
            result["closed"] = closed;
            return CommandResult.Ok(result);
        }
    }

    public class CreateTableCommand : AcadCommand
    {
        public override string MethodName => "create_table";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            Point3d position = ParsePoint(parameters["position"], "position");
            int rows = parameters["rows"]?.Value<int>() ?? 3;
            int columns = parameters["columns"]?.Value<int>() ?? 3;
            double rowHeight = parameters["row_height"]?.Value<double>() ?? 500;
            double colWidth = parameters["column_width"]?.Value<double>() ?? 2000;
            string title = parameters["title"]?.ToString();
            JArray data = parameters["data"] as JArray;

            Database db = doc.Database;
            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                Table table = new Table();
                table.SetDatabaseDefaults();
                table.Position = position;

                int totalRows = rows + (string.IsNullOrEmpty(title) ? 0 : 1);
                table.SetSize(totalRows, columns);

                for (int c = 0; c < columns; c++)
                    table.Columns[c].Width = colWidth;
                for (int r = 0; r < totalRows; r++)
                    table.Rows[r].Height = rowHeight;

                int dataStartRow = 0;
                if (!string.IsNullOrEmpty(title))
                {
                    table.MergeCells(CellRange.Create(table, 0, 0, 0, columns - 1));
                    table.Cells[0, 0].TextString = title;
                    table.Cells[0, 0].TextHeight = rowHeight * 0.5;
                    dataStartRow = 1;
                }

                if (data != null)
                {
                    for (int r = 0; r < data.Count && (r + dataStartRow) < totalRows; r++)
                    {
                        JArray row = data[r] as JArray;
                        if (row == null) continue;
                        for (int c = 0; c < row.Count && c < columns; c++)
                        {
                            table.Cells[r + dataStartRow, c].TextString = row[c]?.ToString() ?? "";
                            table.Cells[r + dataStartRow, c].TextHeight = rowHeight * 0.4;
                        }
                    }
                }

                string layer = parameters["layer"]?.ToString();
                if (!string.IsNullOrEmpty(layer))
                {
                    LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (lt.Has(layer)) table.Layer = layer;
                }

                table.GenerateLayout();
                ObjectId id = ms.AppendEntity(table);
                tr.AddNewlyCreatedDBObject(table, true);
                tr.Commit();

                var result = EntityToJson(id);
                result["type"] = "Table";
                // "rows" is what the caller asked for (data rows); "total_rows"
                // includes the title row when one was added.
                result["rows"] = rows;
                result["total_rows"] = totalRows;
                if (!string.IsNullOrEmpty(title)) result["title"] = title;
                result["columns"] = columns;
                return CommandResult.Ok(result);
            }
        }
    }
}
