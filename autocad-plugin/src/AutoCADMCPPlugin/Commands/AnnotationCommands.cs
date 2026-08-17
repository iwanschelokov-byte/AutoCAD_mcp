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
    public class CreateLinearDimensionCommand : AcadCommand
    {
        public override string MethodName => "create_linear_dimension";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            Point3d pt1 = EntityHelper.ParsePoint(parameters["point1"], "point1");
            Point3d pt2 = EntityHelper.ParsePoint(parameters["point2"], "point2");
            Point3d dimLinePos = EntityHelper.ParsePoint(parameters["dimension_line_position"], "dimension_line_position");
            double rotation = (parameters["rotation"]?.Value<double>() ?? 0) * Math.PI / 180.0;

            Database db = doc.Database;

            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                using (RotatedDimension dim = new RotatedDimension())
                {
                    dim.XLine1Point = pt1;
                    dim.XLine2Point = pt2;
                    dim.DimLinePoint = dimLinePos;
                    dim.Rotation = rotation;
                    dim.DimensionStyle = db.Dimstyle;

                    // Override text if specified
                    string dimText = parameters["text"]?.ToString();
                    if (!string.IsNullOrEmpty(dimText))
                        dim.DimensionText = dimText;

                    string layer = parameters["layer"]?.ToString();
                    if (!string.IsNullOrEmpty(layer))
                    {
                        LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        if (lt.Has(layer))
                            dim.Layer = layer;
                    }

                    ObjectId id = modelSpace.AppendEntity(dim);
                    tr.AddNewlyCreatedDBObject(dim, true);
                    tr.Commit();

                    var result = EntityHelper.EntityToJson(id);
                    result["type"] = "RotatedDimension";
                    result["measurement"] = dim.Measurement;
                    return CommandResult.Ok(result);
                }
            }
        }
    }

    public class CreateAlignedDimensionCommand : AcadCommand
    {
        public override string MethodName => "create_aligned_dimension";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            Point3d pt1 = EntityHelper.ParsePoint(parameters["point1"], "point1");
            Point3d pt2 = EntityHelper.ParsePoint(parameters["point2"], "point2");
            Point3d dimLinePos = EntityHelper.ParsePoint(parameters["dimension_line_position"], "dimension_line_position");

            Database db = doc.Database;

            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                using (AlignedDimension dim = new AlignedDimension())
                {
                    dim.XLine1Point = pt1;
                    dim.XLine2Point = pt2;
                    dim.DimLinePoint = dimLinePos;
                    dim.DimensionStyle = db.Dimstyle;

                    string dimText = parameters["text"]?.ToString();
                    if (!string.IsNullOrEmpty(dimText))
                        dim.DimensionText = dimText;

                    string layer = parameters["layer"]?.ToString();
                    if (!string.IsNullOrEmpty(layer))
                    {
                        LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        if (lt.Has(layer))
                            dim.Layer = layer;
                    }

                    ObjectId id = modelSpace.AppendEntity(dim);
                    tr.AddNewlyCreatedDBObject(dim, true);
                    tr.Commit();

                    var result = EntityHelper.EntityToJson(id);
                    result["type"] = "AlignedDimension";
                    result["measurement"] = dim.Measurement;
                    return CommandResult.Ok(result);
                }
            }
        }
    }
}
