using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AutoCADMCPPlugin.Core;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using static AutoCADMCPPlugin.Commands.EntityHelper;

namespace AutoCADMCPPlugin.Commands
{
    public class DrawingNewCommand : AcadCommand
    {
        public override string MethodName => "drawing_new";

        public override CommandResult Execute(JObject parameters)
        {
            string templatePath = parameters["template"]?.ToString();

            DocumentCollection docMgr = Application.DocumentManager;
            Document newDoc;

            if (!string.IsNullOrEmpty(templatePath) && File.Exists(templatePath))
            {
                newDoc = docMgr.Add(templatePath);
            }
            else
            {
                newDoc = docMgr.Add("");
            }

            return CommandResult.Ok(new JObject
            {
                ["document"] = newDoc.Name,
                ["message"] = "New drawing created"
            });
        }
    }

    public class DrawingOpenCommand : AcadCommand
    {
        public override string MethodName => "drawing_open";

        public override CommandResult Execute(JObject parameters)
        {
            string path = parameters["path"]?.ToString();
            if (string.IsNullOrEmpty(path))
                return CommandResult.Fail("Parameter 'path' is required");

            if (!File.Exists(path))
                return CommandResult.Fail($"File not found: {path}");

            bool readOnly = parameters["read_only"]?.Value<bool>() ?? false;

            // If the drawing is already open, activate it instead of calling
            // Open() again. AutoCAD refuses to open a file it already holds a
            // lock on, so the old behaviour turned a harmless repeat call into
            // a hard error (eFileSharingViolation).
            Document existing = DocumentHelper.FindOpen(path);
            if (existing != null)
            {
                try { Application.DocumentManager.MdiActiveDocument = existing; }
                catch { /* activation is best effort */ }

                return CommandResult.Ok(new JObject
                {
                    ["document"] = existing.Name,
                    ["path"] = SafeFilename(existing) ?? path,
                    ["already_open"] = true,
                    ["message"] = "Drawing was already open; it is now the active drawing"
                });
            }

            Document doc = Application.DocumentManager.Open(path, readOnly);
            try { Application.DocumentManager.MdiActiveDocument = doc; }
            catch { }

            return CommandResult.Ok(new JObject
            {
                ["document"] = doc.Name,
                ["path"] = SafeFilename(doc) ?? path,
                ["already_open"] = false,
                ["read_only"] = readOnly,
                ["message"] = "Drawing opened"
            });
        }

        private static string SafeFilename(Document d)
        {
            try { return d.Database?.Filename; }
            catch { return null; }
        }
    }

    public class DrawingSaveCommand : AcadCommand
    {
        public override string MethodName => "drawing_save";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return CommandResult.Fail("No active document");

            string savePath = parameters["path"]?.ToString();
            string mode = (parameters["mode"]?.ToString() ?? "copy").Trim().ToLowerInvariant();

            Database db = doc.Database;
            using (LockDoc())
            {
                if (!string.IsNullOrEmpty(savePath))
                {
                    if (mode != "copy" && mode != "saveas")
                        return CommandResult.Fail($"Unknown mode '{mode}'. Use \"copy\" (write a copy, keep editing the current file) or \"saveas\" (switch the editing session to the new file).");

                    string dir = Path.GetDirectoryName(savePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    if (mode == "saveas")
                    {
                        // db.SaveAs() writes the file but leaves the editing
                        // session pointing at the old one. A real Save As has to
                        // go through the SAVEAS command, with FILEDIA suppressed
                        // so no dialog appears.
                        string quoted = savePath.IndexOf(' ') >= 0 ? "\"" + savePath + "\"" : savePath;
                        object oldFiledia = Application.GetSystemVariable("FILEDIA");
                        string overwrite = File.Exists(savePath) ? "_Y\n" : "";

                        string script = "_.FILEDIA 0\n_.SAVEAS\n\n" + quoted + "\n" + overwrite
                                      + "_.FILEDIA " + Convert.ToInt32(oldFiledia) + "\n";

                        long since = CommandTracker.Record("SAVEAS", "queued", doc.Name, savePath);
                        doc.SendStringToExecute(script, true, false, false);

                        return CommandResult.Ok(new JObject
                        {
                            ["path"] = savePath,
                            ["previous_path"] = db.Filename,
                            ["mode"] = "saveas",
                            ["queued"] = true,
                            ["since"] = since,
                            ["message"] = $"Save As queued: the drawing will become {savePath}. " +
                                          "Confirm with drawing_info or read_command_line."
                        });
                    }

                    db.SaveAs(savePath, DwgVersion.Current);
                    return CommandResult.Ok(new JObject
                    {
                        ["path"] = savePath,
                        ["mode"] = "copy",
                        ["active_path"] = db.Filename,
                        ["message"] = $"A copy of the drawing was written to {savePath}. " +
                                      "The editing session still points at " +
                                      (string.IsNullOrEmpty(db.Filename) ? "an unsaved drawing" : db.Filename) +
                                      "; pass mode=\"saveas\" to switch it."
                    });
                }
                else
                {
                    // db.Save() and db.SaveAs(currentPath) both fail when AutoCAD
                    // holds a file lock on the open drawing (eFilerError / eFileInternalErr).
                    // Use QSAVE which goes through AutoCAD's internal save mechanism.
                    string currentPath = db.Filename;
                    if (string.IsNullOrEmpty(currentPath) || !File.Exists(currentPath))
                    {
                        return CommandResult.Fail(
                            "Cannot save: this drawing has never been saved to disk. " +
                            "Provide a 'path' parameter to Save As (e.g. \"C:\\\\drawings\\\\myfile.dwg\").");
                    }
                    doc.SendStringToExecute("_.QSAVE\n", true, false, false);
                    return CommandResult.Ok(new JObject
                    {
                        ["path"] = currentPath,
                        ["message"] = $"Drawing saved to {currentPath}"
                    });
                }
            }
        }
    }

    public class DrawingInfoCommand : AcadCommand
    {
        public override string MethodName => "drawing_info";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return CommandResult.Fail("No active document");

            Database db = doc.Database;
            var data = new JObject
            {
                ["name"] = doc.Name,
                // An unsaved drawing has no path. Report null rather than "" so a
                // caller can test for it without guessing what empty means.
                ["path"] = string.IsNullOrEmpty(db.Filename)
                    ? (JToken)JValue.CreateNull()
                    : db.Filename,
                ["is_saved"] = !string.IsNullOrEmpty(db.Filename)
            };

            int entityCount = 0;
            var layerNames = new JArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Count entities in model space
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in modelSpace)
                    entityCount++;

                // List layers
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId layerId in lt)
                {
                    LayerTableRecord layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);
                    layerNames.Add(layer.Name);
                }

                tr.Commit();
            }

            data["entity_count"] = entityCount;
            data["layers"] = layerNames;
            data["layer_count"] = layerNames.Count;

            return CommandResult.Ok(data);
        }
    }
}
