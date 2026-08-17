using System;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCADMCPPlugin.Commands
{
    public class ZoomExtentsCommand : AcadCommand
    {
        public override string MethodName => "zoom_extents";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            // Use SendStringToExecute for zoom commands — reliable across all versions
            doc.SendStringToExecute("._ZOOM _E ", true, false, false);

            return CommandResult.Ok("Zoom extents executed");
        }
    }

    public class ZoomWindowCommand : AcadCommand
    {
        public override string MethodName => "zoom_window";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            // Accept both "min"/"max" and "min_point"/"max_point".
            Point3d min = EntityHelper.ParsePoint(
                EntityHelper.Arg(parameters, "min", "min_point"), "min");
            Point3d max = EntityHelper.ParsePoint(
                EntityHelper.Arg(parameters, "max", "max_point"), "max");

            Editor ed = doc.Editor;

            // Use ViewTableRecord for precise zoom window
            using (ViewTableRecord view = ed.GetCurrentView())
            {
                double width = Math.Abs(max.X - min.X);
                double height = Math.Abs(max.Y - min.Y);
                Point2d center = new Point2d(
                    (min.X + max.X) / 2.0,
                    (min.Y + max.Y) / 2.0
                );

                view.CenterPoint = center;
                view.Width = width;
                view.Height = height;
                ed.SetCurrentView(view);
            }

            return CommandResult.Ok(new JObject
            {
                ["min"] = new JArray(min.X, min.Y),
                ["max"] = new JArray(max.X, max.Y),
                ["message"] = "Zoom window applied"
            });
        }
    }
}
