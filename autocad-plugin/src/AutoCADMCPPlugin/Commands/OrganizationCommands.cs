using System;
using System.Collections;
using System.Collections.Generic;
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
    // Groups
    // ========================================================================

    public class CreateGroupCommand : AcadCommand
    {
        public override string MethodName => "create_group";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = EntityHelper.ArgString(parameters, "name", "group_name");
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.BadParam("Parameter 'name' is required");

            var idsToken = EntityHelper.Arg(parameters, "ids", "entity_ids");
            if (idsToken == null)
                return CommandResult.BadParam("Parameter 'ids' (array of entity handles) is required");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                List<ObjectId> ids;
                string err;
                if (!ModifyHelper.TryResolveAll(db, idsToken, out ids, out err))
                    return CommandResult.NotFound(err);
                if (ids.Count == 0)
                    return CommandResult.BadParam("'ids' must contain at least one entity");

                var groupDict = (DBDictionary)tr.GetObject(db.GroupDictionaryId, OpenMode.ForWrite);
                if (groupDict.Contains(name))
                    return CommandResult.BadParam($"Group '{name}' already exists");

                var group = new Group(parameters["description"]?.ToString() ?? "", true);
                groupDict.SetAt(name, group);
                tr.AddNewlyCreatedDBObject(group, true);

                group.Append(new ObjectIdCollection(ids.ToArray()));
                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["name"] = name,
                    ["entity_count"] = ids.Count
                });
            }
        }
    }

    public class ListGroupsCommand : AcadCommand
    {
        public override string MethodName => "list_groups";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var results = new JArray();

            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dict)
                {
                    var group = tr.GetObject(entry.Value, OpenMode.ForRead) as Group;
                    if (group == null) continue;

                    results.Add(new JObject
                    {
                        ["name"] = entry.Key,
                        ["description"] = group.Description ?? "",
                        ["entity_count"] = group.GetAllEntityIds().Length,
                        ["selectable"] = group.Selectable,
                        ["is_anonymous"] = group.IsAnonymous
                    });
                }
                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["groups"] = results,
                ["count"] = results.Count
            });
        }
    }

    public class AddToGroupCommand : AcadCommand
    {
        public override string MethodName => "add_to_group";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = EntityHelper.ArgString(parameters, "name", "group_name");
            var idsToken = EntityHelper.Arg(parameters, "ids", "entity_ids");
            if (string.IsNullOrWhiteSpace(name) || idsToken == null)
                return CommandResult.BadParam("Parameters 'name' and 'ids' are required");

            bool remove = parameters["remove"]?.Value<bool>() ?? false;
            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(db.GroupDictionaryId, OpenMode.ForRead);
                if (!dict.Contains(name)) return CommandResult.NotFound($"Group '{name}' not found");

                var group = (Group)tr.GetObject(dict.GetAt(name), OpenMode.ForWrite);

                List<ObjectId> ids;
                string err;
                if (!ModifyHelper.TryResolveAll(db, idsToken, out ids, out err))
                    return CommandResult.NotFound(err);

                var col = new ObjectIdCollection(ids.ToArray());
                if (remove) group.Remove(col);
                else group.Append(col);

                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["name"] = name,
                    ["action"] = remove ? "removed" : "added",
                    ["count"] = ids.Count,
                    ["entity_count"] = group.GetAllEntityIds().Length
                });
            }
        }
    }

    public class UngroupCommand : AcadCommand
    {
        public override string MethodName => "ungroup";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = EntityHelper.ArgString(parameters, "name", "group_name");
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.BadParam("Parameter 'name' is required");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(db.GroupDictionaryId, OpenMode.ForWrite);
                if (!dict.Contains(name)) return CommandResult.NotFound($"Group '{name}' not found");

                ObjectId gid = dict.GetAt(name);
                var group = (Group)tr.GetObject(gid, OpenMode.ForWrite);
                int members = group.GetAllEntityIds().Length;

                // Erasing the group leaves its member entities untouched.
                group.Erase();
                dict.Remove(name);
                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["name"] = name,
                    ["released_entities"] = members,
                    ["message"] = $"Group '{name}' removed; its {members} entities remain in the drawing."
                });
            }
        }
    }

    // ========================================================================
    // Layer states
    // ========================================================================

    internal static class LayerStateHelper
    {
        /// <summary>
        /// Every layer property worth capturing. The API has no "All" member, so
        /// the full mask is spelled out here once.
        /// </summary>
        public const LayerStateMasks FullMask =
            LayerStateMasks.On | LayerStateMasks.Frozen | LayerStateMasks.Locked |
            LayerStateMasks.Plot | LayerStateMasks.NewViewport | LayerStateMasks.Color |
            LayerStateMasks.LineType | LayerStateMasks.LineWeight |
            LayerStateMasks.PlotStyle | LayerStateMasks.Transparency;
    }

    public class SaveLayerStateCommand : AcadCommand
    {
        public override string MethodName => "save_layer_state";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = EntityHelper.ArgString(parameters, "name", "state_name");
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.BadParam("Parameter 'name' is required");

            bool overwrite = parameters["overwrite"]?.Value<bool>() ?? false;

            using (EntityHelper.LockDoc())
            {
                var lsm = doc.Database.LayerStateManager;

                if (lsm.HasLayerState(name))
                {
                    if (!overwrite)
                        return CommandResult.BadParam(
                            $"Layer state '{name}' already exists. Pass overwrite=true to replace it.");
                    lsm.DeleteLayerState(name);
                }

                try
                {
                    lsm.SaveLayerState(name, LayerStateHelper.FullMask, ObjectId.Null);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    return CommandResult.Fail(ErrorCode.Internal, $"Could not save layer state: {ex.Message}");
                }

                string description = parameters["description"]?.ToString();
                if (!string.IsNullOrWhiteSpace(description))
                {
                    try { lsm.SetLayerStateDescription(name, description); } catch { }
                }

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["name"] = name,
                    ["overwritten"] = overwrite
                });
            }
        }
    }

    public class RestoreLayerStateCommand : AcadCommand
    {
        public override string MethodName => "restore_layer_state";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = EntityHelper.ArgString(parameters, "name", "state_name");
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.BadParam("Parameter 'name' is required");

            using (EntityHelper.LockDoc())
            {
                var lsm = doc.Database.LayerStateManager;
                if (!lsm.HasLayerState(name))
                    return CommandResult.NotFound($"Layer state '{name}' not found");

                try
                {
                    lsm.RestoreLayerState(name, ObjectId.Null, 0, LayerStateHelper.FullMask);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    return CommandResult.Fail(ErrorCode.Internal,
                        $"Could not restore layer state: {ex.Message}");
                }

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["name"] = name,
                    ["message"] = $"Layer state '{name}' restored"
                });
            }
        }
    }

    public class ListLayerStatesCommand : AcadCommand
    {
        public override string MethodName => "list_layer_states";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var results = new JArray();
            var lsm = doc.Database.LayerStateManager;

            try
            {
                ArrayList names = lsm.GetLayerStateNames(false, false);
                foreach (var n in names)
                {
                    string s = n?.ToString();
                    if (string.IsNullOrEmpty(s)) continue;
                    var o = new JObject { ["name"] = s };
                    try { o["description"] = lsm.GetLayerStateDescription(s) ?? ""; } catch { }
                    try { o["has_viewport_data"] = lsm.LayerStateHasViewportData(s); } catch { }
                    results.Add(o);
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                return CommandResult.Fail(ErrorCode.Internal, $"Could not list layer states: {ex.Message}");
            }

            return CommandResult.Ok(new JObject
            {
                ["layer_states"] = results,
                ["count"] = results.Count
            });
        }
    }

    public class DeleteLayerStateCommand : AcadCommand
    {
        public override string MethodName => "delete_layer_state";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = EntityHelper.ArgString(parameters, "name", "state_name");
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.BadParam("Parameter 'name' is required");

            using (EntityHelper.LockDoc())
            {
                var lsm = doc.Database.LayerStateManager;
                if (!lsm.HasLayerState(name))
                    return CommandResult.NotFound($"Layer state '{name}' not found");

                lsm.DeleteLayerState(name);

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["deleted"] = name
                });
            }
        }
    }

    // ========================================================================
    // Named views
    // ========================================================================

    public class CreateNamedViewCommand : AcadCommand
    {
        public override string MethodName => "create_named_view";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = EntityHelper.ArgString(parameters, "name", "view_name");
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.BadParam("Parameter 'name' is required");

            var minToken = EntityHelper.Arg(parameters, "min", "corner1");
            var maxToken = EntityHelper.Arg(parameters, "max", "corner2");
            if (minToken == null || maxToken == null)
                return CommandResult.BadParam(
                    "Parameters 'min' and 'max' (the view window corners) are required");

            Point3d min = EntityHelper.ParsePoint(minToken, "min");
            Point3d max = EntityHelper.ParsePoint(maxToken, "max");

            double width = Math.Abs(max.X - min.X);
            double height = Math.Abs(max.Y - min.Y);
            if (width <= 0 || height <= 0)
                return CommandResult.BadParam("'min' and 'max' must define a non-empty rectangle");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var vt = (ViewTable)tr.GetObject(db.ViewTableId, OpenMode.ForWrite);
                if (vt.Has(name))
                    return CommandResult.BadParam($"Named view '{name}' already exists");

                var vtr = new ViewTableRecord
                {
                    Name = name,
                    CenterPoint = new Point2d((min.X + max.X) / 2.0, (min.Y + max.Y) / 2.0),
                    Height = height,
                    Width = width
                };

                vt.Add(vtr);
                tr.AddNewlyCreatedDBObject(vtr, true);
                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["name"] = name,
                    ["center"] = new JArray(vtr.CenterPoint.X, vtr.CenterPoint.Y),
                    ["width"] = width,
                    ["height"] = height
                });
            }
        }
    }

    public class ListNamedViewsCommand : AcadCommand
    {
        public override string MethodName => "list_named_views";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var results = new JArray();

            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var vt = (ViewTable)tr.GetObject(doc.Database.ViewTableId, OpenMode.ForRead);
                foreach (ObjectId id in vt)
                {
                    var vtr = (ViewTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    results.Add(new JObject
                    {
                        ["name"] = vtr.Name,
                        ["center"] = new JArray(vtr.CenterPoint.X, vtr.CenterPoint.Y),
                        ["width"] = vtr.Width,
                        ["height"] = vtr.Height,
                        ["is_paper_space"] = vtr.IsPaperspaceView
                    });
                }
                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["views"] = results,
                ["count"] = results.Count
            });
        }
    }

    public class RestoreViewCommand : AcadCommand
    {
        public override string MethodName => "restore_view";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = EntityHelper.ArgString(parameters, "name", "view_name");
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.BadParam("Parameter 'name' is required");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var vt = (ViewTable)tr.GetObject(db.ViewTableId, OpenMode.ForRead);
                if (!vt.Has(name)) return CommandResult.NotFound($"Named view '{name}' not found");

                var vtr = (ViewTableRecord)tr.GetObject(vt[name], OpenMode.ForRead);
                doc.Editor.SetCurrentView(vtr);
                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["name"] = name
                });
            }
        }
    }

    // ========================================================================
    // UCS
    // ========================================================================

    public class ListUcsCommand : AcadCommand
    {
        public override string MethodName => "list_ucs";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var results = new JArray();

            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var ut = (UcsTable)tr.GetObject(doc.Database.UcsTableId, OpenMode.ForRead);
                foreach (ObjectId id in ut)
                {
                    var utr = (UcsTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    results.Add(new JObject
                    {
                        ["name"] = utr.Name,
                        ["origin"] = new JArray(utr.Origin.X, utr.Origin.Y, utr.Origin.Z),
                        ["x_axis"] = new JArray(utr.XAxis.X, utr.XAxis.Y, utr.XAxis.Z),
                        ["y_axis"] = new JArray(utr.YAxis.X, utr.YAxis.Y, utr.YAxis.Z)
                    });
                }
                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["ucs"] = results,
                ["count"] = results.Count
            });
        }
    }

    public class SetUcsCommand : AcadCommand
    {
        public override string MethodName => "set_ucs";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = EntityHelper.ArgString(parameters, "name", "ucs_name");
            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Named UCS: activate an existing one.
                if (!string.IsNullOrWhiteSpace(name))
                {
                    if (string.Equals(name, "World", StringComparison.OrdinalIgnoreCase))
                    {
                        doc.Editor.CurrentUserCoordinateSystem = Matrix3d.Identity;
                        tr.Commit();
                        return CommandResult.Ok(new JObject
                        {
                            ["success"] = true,
                            ["ucs"] = "World"
                        });
                    }

                    var ut = (UcsTable)tr.GetObject(db.UcsTableId, OpenMode.ForRead);
                    if (!ut.Has(name)) return CommandResult.NotFound($"UCS '{name}' not found");

                    var utr = (UcsTableRecord)tr.GetObject(ut[name], OpenMode.ForRead);
                    doc.Editor.CurrentUserCoordinateSystem = Matrix3d.AlignCoordinateSystem(
                        Point3d.Origin, Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis,
                        utr.Origin, utr.XAxis, utr.YAxis, utr.XAxis.CrossProduct(utr.YAxis));

                    tr.Commit();
                    return CommandResult.Ok(new JObject
                    {
                        ["success"] = true,
                        ["ucs"] = name,
                        ["origin"] = new JArray(utr.Origin.X, utr.Origin.Y, utr.Origin.Z)
                    });
                }

                // Explicit axes: optionally persist it as a named UCS.
                var originToken = EntityHelper.Arg(parameters, "origin");
                if (originToken == null)
                    return CommandResult.BadParam(
                        "Provide either 'name' (an existing UCS) or 'origin' (+ optional 'x_axis'/'y_axis')");

                Point3d origin = EntityHelper.ParsePoint(originToken, "origin");

                Vector3d xAxis = Vector3d.XAxis;
                Vector3d yAxis = Vector3d.YAxis;

                var xToken = EntityHelper.Arg(parameters, "x_axis");
                if (xToken != null)
                {
                    Point3d p = EntityHelper.ParsePoint(xToken, "x_axis");
                    xAxis = new Vector3d(p.X, p.Y, p.Z);
                }
                var yToken = EntityHelper.Arg(parameters, "y_axis");
                if (yToken != null)
                {
                    Point3d p = EntityHelper.ParsePoint(yToken, "y_axis");
                    yAxis = new Vector3d(p.X, p.Y, p.Z);
                }

                if (xAxis.Length < 1e-12 || yAxis.Length < 1e-12)
                    return CommandResult.BadParam("Axis vectors cannot be zero-length");

                xAxis = xAxis.GetNormal();
                yAxis = yAxis.GetNormal();

                if (xAxis.IsParallelTo(yAxis))
                    return CommandResult.BadParam("'x_axis' and 'y_axis' must not be parallel");

                Vector3d zAxis = xAxis.CrossProduct(yAxis).GetNormal();

                doc.Editor.CurrentUserCoordinateSystem = Matrix3d.AlignCoordinateSystem(
                    Point3d.Origin, Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis,
                    origin, xAxis, yAxis, zAxis);

                string saveAs = parameters["save_as"]?.ToString();
                if (!string.IsNullOrWhiteSpace(saveAs))
                {
                    var ut = (UcsTable)tr.GetObject(db.UcsTableId, OpenMode.ForWrite);
                    if (!ut.Has(saveAs))
                    {
                        var utr = new UcsTableRecord
                        {
                            Name = saveAs,
                            Origin = origin,
                            XAxis = xAxis,
                            YAxis = yAxis
                        };
                        ut.Add(utr);
                        tr.AddNewlyCreatedDBObject(utr, true);
                    }
                }

                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["origin"] = new JArray(origin.X, origin.Y, origin.Z),
                    ["x_axis"] = new JArray(xAxis.X, xAxis.Y, xAxis.Z),
                    ["y_axis"] = new JArray(yAxis.X, yAxis.Y, yAxis.Z),
                    ["saved_as"] = saveAs ?? ""
                });
            }
        }
    }

    // ========================================================================
    // Extended entity data (XData)
    // ========================================================================

    public class GetXDataCommand : AcadCommand
    {
        public override string MethodName => "get_xdata";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string handle = EntityHelper.EntityIdArg(parameters);
            if (string.IsNullOrWhiteSpace(handle))
                return CommandResult.BadParam("Parameter 'id' is required");

            string appName = EntityHelper.ArgString(parameters, "app_name", "app", "reg_app");
            Database db = doc.Database;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId id = EntityHelper.ResolveHandle(db, handle);
                if (id.IsNull) return CommandResult.NotFound($"Entity '{handle}' not found");

                var ent = tr.GetObject(id, OpenMode.ForRead) as DBObject;
                if (ent == null) return CommandResult.NotFound($"Entity '{handle}' not found");

                ResultBuffer rb = string.IsNullOrWhiteSpace(appName)
                    ? ent.XData
                    : ent.GetXDataForApplication(appName);

                var items = new JArray();
                if (rb != null)
                {
                    foreach (TypedValue tv in rb)
                    {
                        items.Add(new JObject
                        {
                            ["type_code"] = tv.TypeCode,
                            ["type"] = ((DxfCode)tv.TypeCode).ToString(),
                            ["value"] = tv.Value?.ToString() ?? ""
                        });
                    }
                    rb.Dispose();
                }

                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["id"] = handle,
                    ["app_name"] = appName ?? "",
                    ["xdata"] = items,
                    ["count"] = items.Count
                });
            }
        }
    }

    public class SetXDataCommand : AcadCommand
    {
        public override string MethodName => "set_xdata";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string handle = EntityHelper.EntityIdArg(parameters);
            string appName = EntityHelper.ArgString(parameters, "app_name", "app", "reg_app");

            if (string.IsNullOrWhiteSpace(handle))
                return CommandResult.BadParam("Parameter 'id' is required");
            if (string.IsNullOrWhiteSpace(appName))
                return CommandResult.BadParam("Parameter 'app_name' is required");

            var valuesToken = EntityHelper.Arg(parameters, "values", "data") as JArray;
            if (valuesToken == null)
                return CommandResult.BadParam(
                    "Parameter 'values' must be an array of strings/numbers to store");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId id = EntityHelper.ResolveHandle(db, handle);
                if (id.IsNull) return CommandResult.NotFound($"Entity '{handle}' not found");

                // The application name must be registered before XData can use it.
                var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
                if (!rat.Has(appName))
                {
                    rat.UpgradeOpen();
                    var ratr = new RegAppTableRecord { Name = appName };
                    rat.Add(ratr);
                    tr.AddNewlyCreatedDBObject(ratr, true);
                }

                var ent = tr.GetObject(id, OpenMode.ForWrite);

                var tvs = new List<TypedValue>
                {
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, appName)
                };

                foreach (var v in valuesToken)
                {
                    switch (v.Type)
                    {
                        case JTokenType.Integer:
                            tvs.Add(new TypedValue((int)DxfCode.ExtendedDataInteger32, v.Value<int>()));
                            break;
                        case JTokenType.Float:
                            tvs.Add(new TypedValue((int)DxfCode.ExtendedDataReal, v.Value<double>()));
                            break;
                        default:
                            tvs.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, v.ToString()));
                            break;
                    }
                }

                using (var rb = new ResultBuffer(tvs.ToArray()))
                {
                    ent.XData = rb;
                }

                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["id"] = handle,
                    ["app_name"] = appName,
                    ["value_count"] = valuesToken.Count
                });
            }
        }
    }

    // ========================================================================
    // Drawing properties and health
    // ========================================================================

    public class GetDrawingPropertiesCommand : AcadCommand
    {
        public override string MethodName => "get_drawing_properties";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            var si = doc.Database.SummaryInfo;

            var custom = new JObject();
            try
            {
                // CustomProperties hands back a fresh dictionary enumerator.
                IDictionaryEnumerator it = si.CustomProperties;
                while (it.MoveNext())
                {
                    string key = it.Key?.ToString();
                    if (!string.IsNullOrEmpty(key)) custom[key] = it.Value?.ToString() ?? "";
                }
            }
            catch { }

            return CommandResult.Ok(new JObject
            {
                ["title"] = si.Title ?? "",
                ["subject"] = si.Subject ?? "",
                ["author"] = si.Author ?? "",
                ["keywords"] = si.Keywords ?? "",
                ["comments"] = si.Comments ?? "",
                ["last_saved_by"] = si.LastSavedBy ?? "",
                ["revision_number"] = si.RevisionNumber ?? "",
                ["hyperlink_base"] = si.HyperlinkBase ?? "",
                ["custom"] = custom
            });
        }
    }

    public class SetDrawingPropertiesCommand : AcadCommand
    {
        public override string MethodName => "set_drawing_properties";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            {
                // SummaryInfo is immutable; edit through a builder and reassign.
                var builder = new DatabaseSummaryInfoBuilder(db.SummaryInfo);

                var applied = new JArray();

                Action<string, Action<string>> apply = (key, setter) =>
                {
                    var tok = parameters[key];
                    if (tok != null && tok.Type != JTokenType.Null)
                    {
                        setter(tok.ToString());
                        applied.Add(key);
                    }
                };

                apply("title", v => builder.Title = v);
                apply("subject", v => builder.Subject = v);
                apply("author", v => builder.Author = v);
                apply("keywords", v => builder.Keywords = v);
                apply("comments", v => builder.Comments = v);
                apply("revision_number", v => builder.RevisionNumber = v);
                apply("hyperlink_base", v => builder.HyperlinkBase = v);

                var custom = EntityHelper.Arg(parameters, "custom") as JObject;
                if (custom != null)
                {
                    foreach (var prop in custom.Properties())
                    {
                        // Replace any existing entry with the same key.
                        if (builder.CustomPropertyTable.Contains(prop.Name))
                            builder.CustomPropertyTable.Remove(prop.Name);
                        builder.CustomPropertyTable.Add(prop.Name, prop.Value.ToString());
                        applied.Add("custom:" + prop.Name);
                    }
                }

                if (applied.Count == 0)
                {
                    return CommandResult.BadParam(
                        "Provide at least one of: title, subject, author, keywords, comments, " +
                        "revision_number, hyperlink_base, custom");
                }

                db.SummaryInfo = builder.ToDatabaseSummaryInfo();

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["applied"] = applied
                });
            }
        }
    }

    public class EntityCountReportCommand : AcadCommand
    {
        public override string MethodName => "entity_count_report";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            bool byLayer = parameters["by_layer"]?.Value<bool>() ?? true;
            string space = (parameters["space"]?.ToString() ?? "model").Trim().ToLowerInvariant();

            Database db = doc.Database;
            var byType = new Dictionary<string, int>();
            var byLayerCounts = new Dictionary<string, int>();
            int total = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                ObjectId spaceId;
                if (space == "paper" || space == "paperspace")
                    spaceId = bt[BlockTableRecord.PaperSpace];
                else if (space == "current")
                    spaceId = db.CurrentSpaceId;
                else
                    spaceId = bt[BlockTableRecord.ModelSpace];

                var btr = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);

                foreach (ObjectId id in btr)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    total++;
                    string type = id.ObjectClass.DxfName;
                    byType[type] = byType.ContainsKey(type) ? byType[type] + 1 : 1;

                    if (byLayer)
                    {
                        string layer = ent.Layer;
                        byLayerCounts[layer] = byLayerCounts.ContainsKey(layer) ? byLayerCounts[layer] + 1 : 1;
                    }
                }
                tr.Commit();
            }

            var typeObj = new JObject();
            foreach (var kv in byType.OrderByDescending(k => k.Value)) typeObj[kv.Key] = kv.Value;

            var result = new JObject
            {
                ["space"] = space,
                ["total"] = total,
                ["by_type"] = typeObj
            };

            if (byLayer)
            {
                var layerObj = new JObject();
                foreach (var kv in byLayerCounts.OrderByDescending(k => k.Value)) layerObj[kv.Key] = kv.Value;
                result["by_layer"] = layerObj;
            }

            return CommandResult.Ok(result);
        }
    }

    public class AuditDrawingCommand : AcadCommand
    {
        public override string MethodName => "audit_drawing";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            Database db = doc.Database;
            var findings = new JArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Empty layers — candidates for cleanup.
                var usedLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                int zeroLengthCurves = 0;
                int modelEntities = 0;

                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    modelEntities++;
                    usedLayers.Add(ent.Layer);

                    var curve = ent as Curve;
                    if (curve != null)
                    {
                        try
                        {
                            double len = curve.GetDistanceAtParameter(curve.EndParam) -
                                         curve.GetDistanceAtParameter(curve.StartParam);
                            if (Math.Abs(len) < 1e-9) zeroLengthCurves++;
                        }
                        catch { }
                    }
                }

                var emptyLayers = new JArray();
                var frozenLayers = new JArray();
                var lockedLayers = new JArray();
                int layerCount = 0;

                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in lt)
                {
                    var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    layerCount++;
                    if (!usedLayers.Contains(ltr.Name) && ltr.Name != "0") emptyLayers.Add(ltr.Name);
                    if (ltr.IsFrozen) frozenLayers.Add(ltr.Name);
                    if (ltr.IsLocked) lockedLayers.Add(ltr.Name);
                }

                // Unreferenced block definitions.
                var unusedBlocks = new JArray();
                int blockCount = 0;
                foreach (ObjectId id in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (btr.IsLayout || btr.IsAnonymous || btr.IsFromExternalReference) continue;
                    blockCount++;
                    if (btr.GetBlockReferenceIds(true, true).Count == 0) unusedBlocks.Add(btr.Name);
                }

                // Broken (unresolved) xrefs.
                var brokenXrefs = new JArray();
                foreach (ObjectId id in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!btr.IsFromExternalReference) continue;
                    if (btr.XrefStatus != XrefStatus.Resolved)
                    {
                        brokenXrefs.Add(new JObject
                        {
                            ["name"] = btr.Name,
                            ["path"] = btr.PathName ?? "",
                            ["status"] = btr.XrefStatus.ToString()
                        });
                    }
                }

                if (emptyLayers.Count > 0)
                    findings.Add(Finding("empty_layers", "info",
                        $"{emptyLayers.Count} layer(s) hold no model-space geometry", emptyLayers));
                if (unusedBlocks.Count > 0)
                    findings.Add(Finding("unused_blocks", "info",
                        $"{unusedBlocks.Count} block definition(s) are never inserted", unusedBlocks));
                if (brokenXrefs.Count > 0)
                    findings.Add(Finding("broken_xrefs", "warning",
                        $"{brokenXrefs.Count} xref(s) are not resolved", brokenXrefs));
                if (zeroLengthCurves > 0)
                    findings.Add(Finding("zero_length_curves", "warning",
                        $"{zeroLengthCurves} zero-length curve(s) found in model space", null));

                tr.Commit();

                return CommandResult.Ok(new JObject
                {
                    ["drawing"] = doc.Name,
                    ["model_entities"] = modelEntities,
                    ["layers"] = layerCount,
                    ["blocks"] = blockCount,
                    ["frozen_layers"] = frozenLayers,
                    ["locked_layers"] = lockedLayers,
                    ["findings"] = findings,
                    ["finding_count"] = findings.Count
                });
            }
        }

        private static JObject Finding(string code, string severity, string message, JArray items)
        {
            var o = new JObject
            {
                ["code"] = code,
                ["severity"] = severity,
                ["message"] = message
            };
            if (items != null) o["items"] = items;
            return o;
        }
    }
}
