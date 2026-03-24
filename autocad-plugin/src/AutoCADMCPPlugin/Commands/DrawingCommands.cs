using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using static AutoCADMCPPlugin.Commands.EntityHelper;

namespace AutoCADMCPPlugin.Commands
{
    public class DrawingNewCommand : ICommand
    {
        public string MethodName => "drawing_new";

        public CommandResult Execute(JObject parameters)
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

    public class DrawingOpenCommand : ICommand
    {
        public string MethodName => "drawing_open";

        public CommandResult Execute(JObject parameters)
        {
            string path = parameters["path"]?.ToString();
            if (string.IsNullOrEmpty(path))
                return CommandResult.Fail("Parameter 'path' is required");

            if (!File.Exists(path))
                return CommandResult.Fail($"File not found: {path}");

            Document doc = Application.DocumentManager.Open(path, false);
            return CommandResult.Ok(new JObject
            {
                ["document"] = doc.Name,
                ["path"] = path,
                ["message"] = "Drawing opened"
            });
        }
    }

    public class DrawingSaveCommand : ICommand
    {
        public string MethodName => "drawing_save";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return CommandResult.Fail("No active document");

            string savePath = parameters["path"]?.ToString();

            Database db = doc.Database;
            using (LockDoc())
            {
                if (!string.IsNullOrEmpty(savePath))
                {
                    string dir = Path.GetDirectoryName(savePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    db.SaveAs(savePath, DwgVersion.Current);
                    return CommandResult.Ok(new JObject
                    {
                        ["path"] = savePath,
                        ["message"] = $"Drawing saved to {savePath}"
                    });
                }
                else
                {
                    // db.Save() fails from Idle event context (eFileInternalErr).
                    // Use SaveAs with current filename instead.
                    string currentPath = db.Filename;
                    if (string.IsNullOrEmpty(currentPath) || !File.Exists(currentPath))
                    {
                        return CommandResult.Fail(
                            "Cannot save: this drawing has never been saved to disk. " +
                            "Provide a 'path' parameter to Save As (e.g. \"C:\\\\drawings\\\\myfile.dwg\").");
                    }
                    db.SaveAs(currentPath, DwgVersion.Current);
                    return CommandResult.Ok(new JObject
                    {
                        ["path"] = currentPath,
                        ["message"] = $"Drawing saved to {currentPath}"
                    });
                }
            }
        }
    }

    public class DrawingInfoCommand : ICommand
    {
        public string MethodName => "drawing_info";

        public CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return CommandResult.Fail("No active document");

            Database db = doc.Database;
            var data = new JObject
            {
                ["name"] = doc.Name,
                ["path"] = db.Filename
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
