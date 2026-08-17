using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCADMCPPlugin.Commands
{
    // ========================================================================
    // Shared helpers for layout / paper-space work
    // ========================================================================
    internal static class LayoutHelper
    {
        /// <summary>Resolve a layout by name (case-insensitive). Returns ObjectId.Null if absent.</summary>
        public static ObjectId FindLayout(Transaction tr, Database db, string name)
        {
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in dict)
            {
                if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
                    return entry.Value;
            }
            return ObjectId.Null;
        }

        public static JObject LayoutToJson(Layout lay)
        {
            var o = new JObject
            {
                ["name"] = lay.LayoutName,
                ["tab_order"] = lay.TabOrder,
                ["is_model"] = lay.ModelType,
                ["plot_device"] = SafeStr(() => lay.PlotConfigurationName),
                ["paper_size"] = SafeStr(() => lay.CanonicalMediaName),
                ["plot_type"] = SafeStr(() => lay.PlotType.ToString()),
                ["plot_rotation"] = SafeStr(() => lay.PlotRotation.ToString()),
                ["use_standard_scale"] = lay.UseStandardScale,
                ["handle"] = lay.ObjectId.Handle.Value.ToString()
            };
            try
            {
                var min = lay.PlotPaperMargins.MinPoint;
                var max = lay.PlotPaperMargins.MaxPoint;
                o["paper_size_mm"] = new JArray(lay.PlotPaperSize.X, lay.PlotPaperSize.Y);
                o["margins_mm"] = new JArray(min.X, min.Y, max.X, max.Y);
            }
            catch { }
            return o;
        }

        private static string SafeStr(Func<string> f)
        {
            try { return f() ?? ""; } catch { return ""; }
        }

        /// <summary>Parse a "W x H" style scale ratio, e.g. 1:100 → paper 1, drawing 100.</summary>
        public static bool TryParseScale(JToken token, out double paper, out double drawing)
        {
            paper = 1; drawing = 1;
            if (token == null) return false;

            if (token is JArray arr && arr.Count >= 2)
            {
                paper = arr[0].Value<double>();
                drawing = arr[1].Value<double>();
                return drawing != 0;
            }

            string s = token.ToString();
            if (string.IsNullOrWhiteSpace(s)) return false;

            char[] seps = { ':', '/' };
            var parts = s.Split(seps, 2);
            if (parts.Length == 2 &&
                double.TryParse(parts[0].Trim(), out paper) &&
                double.TryParse(parts[1].Trim(), out drawing))
            {
                return drawing != 0;
            }

            // Bare number means "1 paper unit = N drawing units"
            if (double.TryParse(s.Trim(), out drawing))
            {
                paper = 1;
                return drawing != 0;
            }
            return false;
        }
    }

    // ========================================================================
    // Layout CRUD
    // ========================================================================

    public class ListLayoutsCommand : AcadCommand
    {
        public override string MethodName => "list_layouts";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            bool includeModel = parameters["include_model"]?.Value<bool>() ?? false;
            var results = new JArray();

            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(doc.Database.LayoutDictionaryId, OpenMode.ForRead);
                var layouts = new System.Collections.Generic.List<Layout>();
                foreach (DBDictionaryEntry entry in dict)
                {
                    var lay = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    if (lay.ModelType && !includeModel) continue;
                    layouts.Add(lay);
                }

                foreach (var lay in layouts.OrderBy(l => l.TabOrder))
                    results.Add(LayoutHelper.LayoutToJson(lay));

                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["layouts"] = results,
                ["count"] = results.Count,
                ["current"] = LayoutManager.Current.CurrentLayout
            });
        }
    }

    public class CreateLayoutCommand : AcadCommand
    {
        public override string MethodName => "create_layout";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = parameters["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.BadParam("Parameter 'name' is required");

            using (EntityHelper.LockDoc())
            {
                var lm = LayoutManager.Current;
                foreach (var existing in EnumerateLayoutNames(doc.Database))
                {
                    if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
                        return CommandResult.BadParam($"Layout '{name}' already exists");
                }

                ObjectId id = lm.CreateLayout(name);

                bool setCurrent = parameters["set_current"]?.Value<bool>() ?? false;
                if (setCurrent) lm.CurrentLayout = name;

                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var lay = (Layout)tr.GetObject(id, OpenMode.ForRead);
                    var data = LayoutHelper.LayoutToJson(lay);
                    data["message"] = $"Layout '{name}' created";
                    tr.Commit();
                    return CommandResult.Ok(data);
                }
            }
        }

        private static System.Collections.Generic.IEnumerable<string> EnumerateLayoutNames(Database db)
        {
            var names = new System.Collections.Generic.List<string>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry e in dict) names.Add(e.Key);
                tr.Commit();
            }
            return names;
        }
    }

    public class DeleteLayoutCommand : AcadCommand
    {
        public override string MethodName => "delete_layout";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = parameters["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.BadParam("Parameter 'name' is required");

            if (string.Equals(name, "Model", StringComparison.OrdinalIgnoreCase))
                return CommandResult.BadParam("The Model tab cannot be deleted");

            using (EntityHelper.LockDoc())
            {
                var lm = LayoutManager.Current;
                if (lm.LayoutCount <= 1)
                    return CommandResult.BadParam("Cannot delete the last remaining layout");

                try
                {
                    lm.DeleteLayout(name);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    return CommandResult.NotFound($"Layout '{name}' not found");
                }

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["deleted"] = name,
                    ["message"] = $"Layout '{name}' deleted"
                });
            }
        }
    }

    public class RenameLayoutCommand : AcadCommand
    {
        public override string MethodName => "rename_layout";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string oldName = parameters["name"]?.ToString() ?? parameters["old_name"]?.ToString();
            string newName = parameters["new_name"]?.ToString();
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
                return CommandResult.BadParam("Parameters 'name' and 'new_name' are required");

            using (EntityHelper.LockDoc())
            {
                try
                {
                    LayoutManager.Current.RenameLayout(oldName, newName);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    return CommandResult.Fail(ErrorCode.NotFound,
                        $"Could not rename layout '{oldName}': {ex.Message}");
                }

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["old_name"] = oldName,
                    ["new_name"] = newName
                });
            }
        }
    }

    public class SetCurrentLayoutCommand : AcadCommand
    {
        public override string MethodName => "set_current_layout";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = parameters["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.BadParam("Parameter 'name' is required");

            using (EntityHelper.LockDoc())
            {
                try
                {
                    LayoutManager.Current.CurrentLayout = name;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    return CommandResult.NotFound($"Layout '{name}' not found");
                }

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["current"] = LayoutManager.Current.CurrentLayout
                });
            }
        }
    }

    public class CopyLayoutCommand : AcadCommand
    {
        public override string MethodName => "copy_layout";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string source = parameters["name"]?.ToString() ?? parameters["source"]?.ToString();
            string target = parameters["new_name"]?.ToString() ?? parameters["target"]?.ToString();
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                return CommandResult.BadParam("Parameters 'name' and 'new_name' are required");

            using (EntityHelper.LockDoc())
            {
                try
                {
                    LayoutManager.Current.CopyLayout(source, target);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    return CommandResult.Fail(ErrorCode.NotFound,
                        $"Could not copy layout '{source}': {ex.Message}");
                }

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["source"] = source,
                    ["new_name"] = target
                });
            }
        }
    }

    // ========================================================================
    // Page setup
    // ========================================================================

    public class GetPageSetupCommand : AcadCommand
    {
        public override string MethodName => "get_page_setup";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = parameters["layout"]?.ToString() ?? LayoutManager.Current.CurrentLayout;

            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                ObjectId id = LayoutHelper.FindLayout(tr, doc.Database, name);
                if (id.IsNull) return CommandResult.NotFound($"Layout '{name}' not found");

                var lay = (Layout)tr.GetObject(id, OpenMode.ForRead);
                var data = LayoutHelper.LayoutToJson(lay);
                data["plot_style_table"] = lay.CurrentStyleSheet ?? "";
                data["plot_centered"] = lay.PlotCentered;
                data["scale_to_fit"] = lay.StdScaleType == StdScaleType.ScaleToFit;
                tr.Commit();
                return CommandResult.Ok(data);
            }
        }
    }

    public class SetPageSetupCommand : AcadCommand
    {
        public override string MethodName => "set_page_setup";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = parameters["layout"]?.ToString() ?? LayoutManager.Current.CurrentLayout;

            using (EntityHelper.LockDoc())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                ObjectId id = LayoutHelper.FindLayout(tr, doc.Database, name);
                if (id.IsNull) return CommandResult.NotFound($"Layout '{name}' not found");

                var lay = (Layout)tr.GetObject(id, OpenMode.ForWrite);
                var psv = PlotSettingsValidator.Current;

                string device = parameters["device"]?.ToString();
                string media = parameters["paper_size"]?.ToString();

                try
                {
                    if (!string.IsNullOrWhiteSpace(device))
                    {
                        psv.SetPlotConfigurationName(lay, device,
                            string.IsNullOrWhiteSpace(media) ? null : media);
                    }
                    else if (!string.IsNullOrWhiteSpace(media))
                    {
                        psv.RefreshLists(lay);
                        psv.SetCanonicalMediaName(lay, media);
                    }

                    string plotType = parameters["plot_type"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(plotType))
                    {
                        PlotType pt;
                        if (!Enum.TryParse(plotType, true, out pt))
                            return CommandResult.BadParam(
                                "plot_type must be one of: Display, Extents, Limits, View, Window, Layout");
                        psv.SetPlotType(lay, pt);
                    }

                    bool? centered = parameters["centered"]?.Value<bool>();
                    if (centered.HasValue) psv.SetPlotCentered(lay, centered.Value);

                    string rotation = parameters["rotation"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(rotation))
                    {
                        PlotRotation pr;
                        string norm = rotation.Trim();
                        if (norm == "0") norm = "Degrees000";
                        else if (norm == "90") norm = "Degrees090";
                        else if (norm == "180") norm = "Degrees180";
                        else if (norm == "270") norm = "Degrees270";
                        if (!Enum.TryParse(norm, true, out pr))
                            return CommandResult.BadParam("rotation must be 0, 90, 180 or 270");
                        psv.SetPlotRotation(lay, pr);
                    }

                    bool fit = parameters["scale_to_fit"]?.Value<bool>() ?? false;
                    if (fit)
                    {
                        psv.SetUseStandardScale(lay, true);
                        psv.SetStdScaleType(lay, StdScaleType.ScaleToFit);
                    }
                    else
                    {
                        double paper, drawing;
                        if (LayoutHelper.TryParseScale(parameters["scale"], out paper, out drawing))
                        {
                            psv.SetUseStandardScale(lay, false);
                            psv.SetCustomPrintScale(lay, new CustomScale(paper, drawing));
                        }
                    }

                    string styleSheet = parameters["plot_style_table"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(styleSheet))
                        psv.SetCurrentStyleSheet(lay, styleSheet);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    return CommandResult.BadParam($"Page setup rejected by AutoCAD: {ex.Message}");
                }

                var data = LayoutHelper.LayoutToJson(lay);
                data["message"] = $"Page setup updated for layout '{lay.LayoutName}'";
                tr.Commit();
                return CommandResult.Ok(data);
            }
        }
    }

    // ========================================================================
    // Viewports (paper space)
    // ========================================================================

    public class ListViewportsCommand : AcadCommand
    {
        public override string MethodName => "list_viewports";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string layoutName = parameters["layout"]?.ToString() ?? LayoutManager.Current.CurrentLayout;
            var results = new JArray();

            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                ObjectId layId = LayoutHelper.FindLayout(tr, doc.Database, layoutName);
                if (layId.IsNull) return CommandResult.NotFound($"Layout '{layoutName}' not found");

                var lay = (Layout)tr.GetObject(layId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);

                foreach (ObjectId entId in btr)
                {
                    var vp = tr.GetObject(entId, OpenMode.ForRead) as Viewport;
                    if (vp == null) continue;

                    var o = new JObject
                    {
                        ["id"] = vp.ObjectId.Handle.Value.ToString(),
                        ["number"] = vp.Number,
                        ["center"] = new JArray(vp.CenterPoint.X, vp.CenterPoint.Y, vp.CenterPoint.Z),
                        ["width"] = vp.Width,
                        ["height"] = vp.Height,
                        ["on"] = vp.On,
                        ["locked"] = vp.Locked,
                        ["layer"] = vp.Layer,
                        ["view_center"] = new JArray(vp.ViewCenter.X, vp.ViewCenter.Y),
                        ["view_height"] = vp.ViewHeight
                    };
                    try { o["custom_scale"] = vp.CustomScale; } catch { }
                    try { o["standard_scale"] = vp.StandardScale.ToString(); } catch { }
                    results.Add(o);
                }
                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["layout"] = layoutName,
                ["viewports"] = results,
                ["count"] = results.Count
            });
        }
    }

    public class CreateViewportCommand : AcadCommand
    {
        public override string MethodName => "create_viewport";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            Point3d center = EntityHelper.ParsePoint(parameters["center"], "center");
            double width = parameters["width"]?.Value<double>() ?? 0;
            double height = parameters["height"]?.Value<double>() ?? 0;
            if (width <= 0 || height <= 0)
                return CommandResult.BadParam("Parameters 'width' and 'height' must be positive");

            string layoutName = parameters["layout"]?.ToString() ?? LayoutManager.Current.CurrentLayout;

            using (EntityHelper.LockDoc())
            {
                var lm = LayoutManager.Current;

                // A viewport can only be switched on while its layout is current.
                string previous = lm.CurrentLayout;
                bool switched = false;
                if (!string.Equals(previous, layoutName, StringComparison.OrdinalIgnoreCase))
                {
                    try { lm.CurrentLayout = layoutName; switched = true; }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        return CommandResult.NotFound($"Layout '{layoutName}' not found");
                    }
                }

                try
                {
                    // TILEMODE 0 == paper space, required for viewport activation.
                    Application.SetSystemVariable("TILEMODE", 0);

                    using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        ObjectId layId = LayoutHelper.FindLayout(tr, doc.Database, layoutName);
                        if (layId.IsNull) return CommandResult.NotFound($"Layout '{layoutName}' not found");

                        var lay = (Layout)tr.GetObject(layId, OpenMode.ForRead);
                        var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForWrite);

                        var vp = new Viewport
                        {
                            CenterPoint = center,
                            Width = width,
                            Height = height
                        };

                        string layer = parameters["layer"]?.ToString();
                        if (!string.IsNullOrEmpty(layer))
                        {
                            var lt = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                            if (lt.Has(layer)) vp.Layer = layer;
                        }

                        ObjectId vpId = btr.AppendEntity(vp);
                        tr.AddNewlyCreatedDBObject(vp, true);

                        // Must be appended to the database before it can be turned on.
                        vp.On = true;

                        double paper, drawing;
                        if (LayoutHelper.TryParseScale(parameters["scale"], out paper, out drawing))
                        {
                            vp.CustomScale = paper / drawing;
                        }

                        var viewCenter = parameters["view_center"];
                        if (viewCenter != null)
                        {
                            Point3d vc = EntityHelper.ParsePoint(viewCenter, "view_center");
                            vp.ViewCenter = new Point2d(vc.X, vc.Y);
                        }

                        bool locked = parameters["locked"]?.Value<bool>() ?? false;
                        if (locked) vp.Locked = true;

                        var result = new JObject
                        {
                            ["id"] = vpId.Handle.Value.ToString(),
                            ["type"] = "Viewport",
                            ["layout"] = layoutName,
                            ["center"] = new JArray(center.X, center.Y, center.Z),
                            ["width"] = width,
                            ["height"] = height,
                            ["locked"] = vp.Locked
                        };
                        try { result["custom_scale"] = vp.CustomScale; } catch { }

                        tr.Commit();
                        return CommandResult.Ok(result);
                    }
                }
                finally
                {
                    if (switched)
                    {
                        try { lm.CurrentLayout = previous; } catch { }
                    }
                }
            }
        }
    }

    public class SetViewportScaleCommand : AcadCommand
    {
        public override string MethodName => "set_viewport_scale";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string handle = parameters["id"]?.ToString() ?? parameters["viewport_id"]?.ToString();
            if (string.IsNullOrWhiteSpace(handle))
                return CommandResult.BadParam("Parameter 'id' (viewport handle) is required");

            double paper, drawing;
            if (!LayoutHelper.TryParseScale(parameters["scale"], out paper, out drawing))
                return CommandResult.BadParam("Parameter 'scale' is required, e.g. \"1:100\" or [1,100]");

            using (EntityHelper.LockDoc())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                ObjectId id = EntityHelper.ResolveHandle(doc.Database, handle);
                if (id.IsNull) return CommandResult.NotFound($"Viewport '{handle}' not found");

                var vp = tr.GetObject(id, OpenMode.ForWrite) as Viewport;
                if (vp == null) return CommandResult.BadParam($"Entity '{handle}' is not a viewport");

                bool wasLocked = vp.Locked;
                if (wasLocked) vp.Locked = false;
                vp.CustomScale = paper / drawing;
                if (wasLocked) vp.Locked = true;

                tr.Commit();
                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["id"] = handle,
                    ["custom_scale"] = paper / drawing,
                    ["scale"] = $"{paper}:{drawing}"
                });
            }
        }
    }

    public class LockViewportCommand : AcadCommand
    {
        public override string MethodName => "lock_viewport";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string handle = parameters["id"]?.ToString() ?? parameters["viewport_id"]?.ToString();
            if (string.IsNullOrWhiteSpace(handle))
                return CommandResult.BadParam("Parameter 'id' (viewport handle) is required");

            bool locked = parameters["locked"]?.Value<bool>() ?? true;

            using (EntityHelper.LockDoc())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                ObjectId id = EntityHelper.ResolveHandle(doc.Database, handle);
                if (id.IsNull) return CommandResult.NotFound($"Viewport '{handle}' not found");

                var vp = tr.GetObject(id, OpenMode.ForWrite) as Viewport;
                if (vp == null) return CommandResult.BadParam($"Entity '{handle}' is not a viewport");

                vp.Locked = locked;
                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["id"] = handle,
                    ["locked"] = locked
                });
            }
        }
    }

    // ========================================================================
    // Plotting
    // ========================================================================

}
