using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCADMCPPlugin.Commands
{
    internal static class XrefHelper
    {
        /// <summary>Find an xref's BlockTableRecord id by block name.</summary>
        public static ObjectId FindXrefBlock(Transaction tr, Database db, string name)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!btr.IsFromExternalReference) continue;
                if (string.Equals(btr.Name, name, StringComparison.OrdinalIgnoreCase))
                    return id;
            }
            return ObjectId.Null;
        }
    }

    // ========================================================================
    // Xref attach / manage
    // ========================================================================

    public class AttachXrefCommand : AcadCommand
    {
        public override string MethodName => "attach_xref";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string path = EntityHelper.ArgString(parameters, "path", "file_path", "dwg_path");
            if (string.IsNullOrWhiteSpace(path))
                return CommandResult.BadParam("Parameter 'path' is required");
            if (!File.Exists(path))
                return CommandResult.NotFound($"File not found: {path}");

            string blockName = parameters["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(blockName))
                blockName = Path.GetFileNameWithoutExtension(path);

            Point3d insert = parameters["position"] != null || parameters["insertion_point"] != null
                ? EntityHelper.ParsePoint(EntityHelper.Arg(parameters, "position", "insertion_point"), "position")
                : Point3d.Origin;

            double scale = parameters["scale"]?.Value<double>() ?? 1.0;
            double rotation = parameters["rotation"]?.Value<double>() ?? 0.0;
            bool overlay = parameters["overlay"]?.Value<bool>() ?? false;

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            {
                ObjectId xrefBtrId;
                try
                {
                    xrefBtrId = overlay
                        ? db.OverlayXref(path, blockName)
                        : db.AttachXref(path, blockName);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    return CommandResult.Fail(ErrorCode.Internal, $"Xref attach failed: {ex.Message}");
                }

                if (xrefBtrId.IsNull)
                    return CommandResult.Fail(ErrorCode.Internal, "AutoCAD returned no xref block definition");

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    var br = new BlockReference(insert, xrefBtrId)
                    {
                        ScaleFactors = new Scale3d(scale),
                        Rotation = rotation * Math.PI / 180.0
                    };

                    string layer = parameters["layer"]?.ToString();
                    if (!string.IsNullOrEmpty(layer))
                    {
                        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        if (lt.Has(layer)) br.Layer = layer;
                    }

                    ObjectId refId = ms.AppendEntity(br);
                    tr.AddNewlyCreatedDBObject(br, true);
                    tr.Commit();

                    return CommandResult.Ok(new JObject
                    {
                        ["id"] = refId.Handle.Value.ToString(),
                        ["type"] = overlay ? "XrefOverlay" : "XrefAttach",
                        ["name"] = blockName,
                        ["path"] = path,
                        ["position"] = new JArray(insert.X, insert.Y, insert.Z),
                        ["scale"] = scale,
                        ["rotation"] = rotation
                    });
                }
            }
        }
    }

    public class ListXrefsCommand : AcadCommand
    {
        public override string MethodName => "list_xrefs";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            Database db = doc.Database;
            var results = new JArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!btr.IsFromExternalReference) continue;

                    var o = new JObject
                    {
                        ["name"] = btr.Name,
                        ["path"] = btr.PathName ?? "",
                        ["is_overlay"] = btr.IsFromOverlayReference,
                        ["is_unloaded"] = btr.IsUnloaded,
                        ["is_resolved"] = btr.XrefStatus == XrefStatus.Resolved,
                        ["status"] = btr.XrefStatus.ToString(),
                        ["handle"] = id.Handle.Value.ToString()
                    };

                    // Count how many times this xref is placed in the drawing.
                    try
                    {
                        var refIds = btr.GetBlockReferenceIds(true, true);
                        o["reference_count"] = refIds.Count;
                    }
                    catch { }

                    results.Add(o);
                }
                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["xrefs"] = results,
                ["count"] = results.Count
            });
        }
    }

    /// <summary>Base for the reload/unload/detach/bind family, which share resolution logic.</summary>
    public abstract class XrefActionCommand : AcadCommand
    {
        protected abstract string Action { get; }

        protected abstract void Apply(Database db, ObjectId btrId, JObject parameters);

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = EntityHelper.ArgString(parameters, "name", "xref_name");
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.BadParam("Parameter 'name' (xref block name) is required");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            {
                ObjectId btrId;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    btrId = XrefHelper.FindXrefBlock(tr, db, name);
                    tr.Commit();
                }

                if (btrId.IsNull)
                    return CommandResult.NotFound($"Xref '{name}' not found in this drawing");

                try
                {
                    Apply(db, btrId, parameters);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    return CommandResult.Fail(ErrorCode.Internal, $"Xref {Action} failed: {ex.Message}");
                }

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["name"] = name,
                    ["action"] = Action
                });
            }
        }
    }

    public class ReloadXrefCommand : XrefActionCommand
    {
        public override string MethodName => "reload_xref";
        protected override string Action => "reload";

        protected override void Apply(Database db, ObjectId btrId, JObject parameters)
        {
            db.ReloadXrefs(new ObjectIdCollection { btrId });
        }
    }

    public class UnloadXrefCommand : XrefActionCommand
    {
        public override string MethodName => "unload_xref";
        protected override string Action => "unload";

        protected override void Apply(Database db, ObjectId btrId, JObject parameters)
        {
            db.UnloadXrefs(new ObjectIdCollection { btrId });
        }
    }

    public class DetachXrefCommand : XrefActionCommand
    {
        public override string MethodName => "detach_xref";
        protected override string Action => "detach";

        protected override void Apply(Database db, ObjectId btrId, JObject parameters)
        {
            db.DetachXref(btrId);
        }
    }

    public class BindXrefCommand : XrefActionCommand
    {
        public override string MethodName => "bind_xref";
        protected override string Action => "bind";

        protected override void Apply(Database db, ObjectId btrId, JObject parameters)
        {
            // insert_bind=true merges names into the host (like INSERT);
            // false keeps them prefixed with the xref name.
            bool insertBind = parameters["insert_bind"]?.Value<bool>() ?? false;
            db.BindXrefs(new ObjectIdCollection { btrId }, insertBind);
        }
    }

    public class SetXrefPathCommand : AcadCommand
    {
        public override string MethodName => "set_xref_path";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.NoDoc();

            string name = EntityHelper.ArgString(parameters, "name", "xref_name");
            string path = EntityHelper.ArgString(parameters, "path", "new_path", "file_path");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
                return CommandResult.BadParam("Parameters 'name' and 'path' are required");

            Database db = doc.Database;

            using (EntityHelper.LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId btrId = XrefHelper.FindXrefBlock(tr, db, name);
                if (btrId.IsNull) return CommandResult.NotFound($"Xref '{name}' not found");

                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForWrite);
                string oldPath = btr.PathName;
                btr.PathName = path;
                tr.Commit();

                bool reload = parameters["reload"]?.Value<bool>() ?? true;
                if (reload)
                {
                    try { db.ReloadXrefs(new ObjectIdCollection { btrId }); } catch { }
                }

                return CommandResult.Ok(new JObject
                {
                    ["success"] = true,
                    ["name"] = name,
                    ["old_path"] = oldPath ?? "",
                    ["new_path"] = path,
                    ["reloaded"] = reload
                });
            }
        }
    }

    // ========================================================================
    // Side-database queries — inspect DWG files WITHOUT opening them
    // ========================================================================

    internal static class SideDatabaseReader
    {
        /// <summary>
        /// Open a DWG as a side database (no UI, no document window) and project
        /// a summary of it. This is what makes folder-wide audits possible.
        /// </summary>
        public static JObject Summarize(string path, bool includeLayers, bool includeBlocks,
                                        bool includeLayouts, bool includeEntityCounts)
        {
            var summary = new JObject
            {
                ["path"] = path,
                ["file_name"] = Path.GetFileName(path)
            };

            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
                db.CloseInput(true);

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    if (includeLayers)
                    {
                        var layers = new JArray();
                        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        foreach (ObjectId id in lt)
                        {
                            var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                            layers.Add(new JObject
                            {
                                ["name"] = ltr.Name,
                                ["color"] = ltr.Color.ColorIndex,
                                ["is_frozen"] = ltr.IsFrozen,
                                ["is_off"] = ltr.IsOff,
                                ["is_locked"] = ltr.IsLocked
                            });
                        }
                        summary["layers"] = layers;
                        summary["layer_count"] = layers.Count;
                    }

                    if (includeBlocks)
                    {
                        var blocks = new JArray();
                        var xrefs = new JArray();
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        foreach (ObjectId id in bt)
                        {
                            var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                            if (btr.IsLayout) continue;
                            if (btr.IsFromExternalReference)
                            {
                                xrefs.Add(new JObject
                                {
                                    ["name"] = btr.Name,
                                    ["path"] = btr.PathName ?? ""
                                });
                            }
                            else if (!btr.IsAnonymous)
                            {
                                blocks.Add(btr.Name);
                            }
                        }
                        summary["blocks"] = blocks;
                        summary["block_count"] = blocks.Count;
                        summary["xrefs"] = xrefs;
                        summary["xref_count"] = xrefs.Count;
                    }

                    if (includeLayouts)
                    {
                        var layouts = new JArray();
                        var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                        foreach (DBDictionaryEntry e in dict)
                        {
                            var lay = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
                            if (lay.ModelType) continue;
                            layouts.Add(lay.LayoutName);
                        }
                        summary["layouts"] = layouts;
                        summary["layout_count"] = layouts.Count;
                    }

                    if (includeEntityCounts)
                    {
                        var counts = new Dictionary<string, int>();
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        int total = 0;
                        foreach (ObjectId id in ms)
                        {
                            string t = id.ObjectClass.DxfName;
                            counts[t] = counts.ContainsKey(t) ? counts[t] + 1 : 1;
                            total++;
                        }
                        var byType = new JObject();
                        foreach (var kv in counts.OrderByDescending(k => k.Value))
                            byType[kv.Key] = kv.Value;
                        summary["model_space_entity_count"] = total;
                        summary["entities_by_type"] = byType;
                    }

                    tr.Commit();
                }
            }

            return summary;
        }
    }

    public class ReadExternalDwgCommand : AcadCommand
    {
        public override string MethodName => "read_external_dwg";

        public override CommandResult Execute(JObject parameters)
        {
            string path = EntityHelper.ArgString(parameters, "path", "file_path", "dwg_path");
            if (string.IsNullOrWhiteSpace(path))
                return CommandResult.BadParam("Parameter 'path' is required");
            if (!File.Exists(path))
                return CommandResult.NotFound($"File not found: {path}");

            bool layers = parameters["include_layers"]?.Value<bool>() ?? true;
            bool blocks = parameters["include_blocks"]?.Value<bool>() ?? true;
            bool layouts = parameters["include_layouts"]?.Value<bool>() ?? true;
            bool counts = parameters["include_entity_counts"]?.Value<bool>() ?? true;

            try
            {
                var summary = SideDatabaseReader.Summarize(path, layers, blocks, layouts, counts);
                return CommandResult.Ok(summary);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                return CommandResult.Fail(ErrorCode.Internal, $"Could not read '{path}': {ex.Message}");
            }
            catch (System.Exception ex)
            {
                return CommandResult.Fail(ErrorCode.Internal, $"Could not read '{path}': {ex.Message}");
            }
        }
    }

    public class BatchQueryDwgsCommand : AcadCommand
    {
        public override string MethodName => "batch_query_dwgs";

        // Opens each DWG as a read-only side database; the open drawing is never
        // touched, so this stays available in read-only mode.
        public override bool IsWrite => false;

        public override CommandResult Execute(JObject parameters)
        {
            string folder = EntityHelper.ArgString(parameters, "folder", "path", "directory");
            if (string.IsNullOrWhiteSpace(folder))
                return CommandResult.BadParam("Parameter 'folder' is required");
            if (!Directory.Exists(folder))
                return CommandResult.NotFound($"Folder not found: {folder}");

            bool recursive = parameters["recursive"]?.Value<bool>() ?? false;
            int limit = parameters["limit"]?.Value<int>() ?? 200;
            if (limit <= 0) limit = 200;

            bool layers = parameters["include_layers"]?.Value<bool>() ?? false;
            bool blocks = parameters["include_blocks"]?.Value<bool>() ?? false;
            bool layouts = parameters["include_layouts"]?.Value<bool>() ?? true;
            bool counts = parameters["include_entity_counts"]?.Value<bool>() ?? false;

            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "*.dwg", option);
            }
            catch (System.Exception ex)
            {
                return CommandResult.Fail(ErrorCode.Internal, $"Could not list folder: {ex.Message}");
            }

            var results = new JArray();
            var errors = new JArray();
            int scanned = 0;

            foreach (var f in files)
            {
                if (scanned >= limit) break;
                scanned++;
                try
                {
                    results.Add(SideDatabaseReader.Summarize(f, layers, blocks, layouts, counts));
                }
                catch (System.Exception ex)
                {
                    errors.Add(new JObject
                    {
                        ["path"] = f,
                        ["error"] = ex.Message
                    });
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["folder"] = folder,
                ["files_found"] = files.Length,
                ["files_scanned"] = scanned,
                ["truncated"] = files.Length > scanned,
                ["results"] = results,
                ["errors"] = errors
            });
        }
    }
}
