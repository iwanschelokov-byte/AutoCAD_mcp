using System;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using static AutoCADMCPPlugin.Commands.EntityHelper;

namespace AutoCADMCPPlugin.Commands
{
    public class CreateDimensionStyleCommand : AcadCommand
    {
        public override string MethodName => "create_dimension_style";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string name = parameters["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                return CommandResult.Fail("Parameter 'name' is required");

            Database db = doc.Database;
            bool created = false;

            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DimStyleTable dst = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForWrite);
                DimStyleTableRecord style;

                if (dst.Has(name))
                {
                    style = (DimStyleTableRecord)tr.GetObject(dst[name], OpenMode.ForWrite);
                }
                else
                {
                    style = new DimStyleTableRecord { Name = name };
                    dst.Add(style);
                    tr.AddNewlyCreatedDBObject(style, true);
                    created = true;
                }

                if (parameters["text_height"] != null) style.Dimtxt = parameters["text_height"].Value<double>();
                if (parameters["arrow_size"] != null) style.Dimasz = parameters["arrow_size"].Value<double>();
                if (parameters["extension_offset"] != null) style.Dimexo = parameters["extension_offset"].Value<double>();
                if (parameters["extension_extend"] != null) style.Dimexe = parameters["extension_extend"].Value<double>();
                if (parameters["dim_line_increment"] != null) style.Dimdli = parameters["dim_line_increment"].Value<double>();
                if (parameters["text_gap"] != null) style.Dimgap = parameters["text_gap"].Value<double>();
                if (parameters["text_above"] != null) style.Dimtad = parameters["text_above"].Value<bool>() ? 1 : 0;
                if (parameters["text_inside_horizontal"] != null) style.Dimtih = parameters["text_inside_horizontal"].Value<bool>();
                if (parameters["text_outside_horizontal"] != null) style.Dimtoh = parameters["text_outside_horizontal"].Value<bool>();
                if (parameters["linear_scale_factor"] != null) style.Dimlfac = parameters["linear_scale_factor"].Value<double>();
                if (parameters["decimal_places"] != null) style.Dimdec = parameters["decimal_places"].Value<int>();
                if (parameters["text_color"] != null) style.Dimclrt = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)parameters["text_color"].Value<int>());
                if (parameters["dim_line_color"] != null) style.Dimclrd = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)parameters["dim_line_color"].Value<int>());
                if (parameters["ext_line_color"] != null) style.Dimclre = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)parameters["ext_line_color"].Value<int>());
                if (parameters["suffix"] != null) style.Dimpost = parameters["suffix"].ToString();

                // Set text style if specified
                if (parameters["text_style"] != null)
                {
                    string tsName = parameters["text_style"].ToString();
                    TextStyleTable tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                    if (tst.Has(tsName))
                        style.Dimtxsty = tst[tsName];
                }

                bool setCurrent = parameters["set_current"]?.Value<bool>() ?? false;
                if (setCurrent)
                    db.Dimstyle = dst[name];

                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["name"] = name,
                ["created"] = created,
                ["message"] = created ? $"Dimension style '{name}' created" : $"Dimension style '{name}' updated"
            });
        }
    }

    public class CreateTextStyleCommand : AcadCommand
    {
        public override string MethodName => "create_text_style";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string name = parameters["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                return CommandResult.Fail("Parameter 'name' is required");

            string font = parameters["font"]?.ToString() ?? "Arial";
            double height = parameters["height"]?.Value<double>() ?? 0;
            double widthFactor = parameters["width_factor"]?.Value<double>() ?? 1.0;
            double oblique = parameters["oblique_angle"]?.Value<double>() ?? 0;
            bool setCurrent = parameters["set_current"]?.Value<bool>() ?? false;

            Database db = doc.Database;
            bool created = false;

            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                TextStyleTable tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForWrite);
                TextStyleTableRecord style;

                if (tst.Has(name))
                {
                    style = (TextStyleTableRecord)tr.GetObject(tst[name], OpenMode.ForWrite);
                }
                else
                {
                    style = new TextStyleTableRecord { Name = name };
                    tst.Add(style);
                    tr.AddNewlyCreatedDBObject(style, true);
                    created = true;
                }

                style.FileName = font;
                style.TextSize = height;
                style.XScale = widthFactor;
                style.ObliquingAngle = oblique * Math.PI / 180.0;

                if (setCurrent)
                    db.Textstyle = tst[name];

                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["name"] = name,
                ["font"] = font,
                ["height"] = height,
                ["created"] = created,
                ["message"] = created ? $"Text style '{name}' created" : $"Text style '{name}' updated"
            });
        }
    }

    public class ListDimensionStylesCommand : AcadCommand
    {
        public override string MethodName => "list_dimension_styles";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            Database db = doc.Database;
            JArray styles = new JArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DimStyleTable dst = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in dst)
                {
                    DimStyleTableRecord s = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    styles.Add(new JObject
                    {
                        ["name"] = s.Name,
                        ["text_height"] = s.Dimtxt,
                        ["arrow_size"] = s.Dimasz,
                        ["linear_scale_factor"] = s.Dimlfac,
                        ["decimal_places"] = s.Dimdec,
                        ["is_current"] = (id == db.Dimstyle)
                    });
                }
                tr.Commit();
            }

            return CommandResult.Ok(new JObject { ["styles"] = styles, ["count"] = styles.Count });
        }
    }

    public class ListTextStylesCommand : AcadCommand
    {
        public override string MethodName => "list_text_styles";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            Database db = doc.Database;
            JArray styles = new JArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                TextStyleTable tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in tst)
                {
                    TextStyleTableRecord s = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    styles.Add(new JObject
                    {
                        ["name"] = s.Name,
                        ["font"] = s.FileName,
                        ["height"] = s.TextSize,
                        ["width_factor"] = s.XScale,
                        ["is_current"] = (id == db.Textstyle)
                    });
                }
                tr.Commit();
            }

            return CommandResult.Ok(new JObject { ["styles"] = styles, ["count"] = styles.Count });
        }
    }
}
