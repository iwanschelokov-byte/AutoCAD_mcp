using System;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Colors;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using static AutoCADMCPPlugin.Commands.EntityHelper;

namespace AutoCADMCPPlugin.Commands
{
    public class ListLayersCommand : AcadCommand
    {
        public override string MethodName => "list_layers";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            Database db = doc.Database;
            JArray layers = new JArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

                foreach (ObjectId layerId in lt)
                {
                    LayerTableRecord layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);
                    layers.Add(new JObject
                    {
                        ["name"] = layer.Name,
                        ["color"] = layer.Color.ColorIndex,
                        ["is_off"] = layer.IsOff,
                        ["is_frozen"] = layer.IsFrozen,
                        ["is_locked"] = layer.IsLocked,
                        ["linetype"] = layer.LinetypeObjectId.IsValid
                            ? ((LinetypeTableRecord)tr.GetObject(layer.LinetypeObjectId, OpenMode.ForRead)).Name
                            : "Continuous",
                        ["is_current"] = (layerId == db.Clayer)
                    });
                }

                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["layers"] = layers,
                ["count"] = layers.Count
            });
        }
    }

    public class CreateLayerCommand : AcadCommand
    {
        public override string MethodName => "create_layer";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string name = parameters["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                return CommandResult.Fail("Parameter 'name' is required");

            int colorIndex = parameters["color"]?.Value<int>() ?? 7; // White default
            bool makeActive = parameters["set_current"]?.Value<bool>() ?? false;

            Database db = doc.Database;

            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);

                if (lt.Has(name))
                {
                    if (makeActive)
                    {
                        db.Clayer = lt[name];
                        tr.Commit();
                        return CommandResult.Ok($"Layer '{name}' already exists, set as current");
                    }
                    return CommandResult.Ok($"Layer '{name}' already exists");
                }

                LayerTableRecord newLayer = new LayerTableRecord
                {
                    Name = name,
                    Color = Color.FromColorIndex(ColorMethod.ByAci, (short)colorIndex)
                };

                // Set linetype if specified
                string linetype = parameters["linetype"]?.ToString();
                if (!string.IsNullOrEmpty(linetype))
                {
                    LinetypeTable ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                    if (ltt.Has(linetype))
                        newLayer.LinetypeObjectId = ltt[linetype];
                }

                ObjectId layerId = lt.Add(newLayer);
                tr.AddNewlyCreatedDBObject(newLayer, true);

                if (makeActive)
                    db.Clayer = layerId;

                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["name"] = name,
                ["color"] = colorIndex,
                ["message"] = $"Layer '{name}' created"
            });
        }
    }

    public class SetCurrentLayerCommand : AcadCommand
    {
        public override string MethodName => "set_current_layer";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string name = parameters["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                return CommandResult.Fail("Parameter 'name' is required");

            Database db = doc.Database;

            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

                if (!lt.Has(name))
                    return CommandResult.Fail($"Layer '{name}' not found");

                db.Clayer = lt[name];
                tr.Commit();
            }

            return CommandResult.Ok($"Current layer set to '{name}'");
        }
    }

    public class SetLayerPropertiesCommand : AcadCommand
    {
        public override string MethodName => "set_layer_properties";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string name = parameters["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                return CommandResult.Fail("Parameter 'name' is required");

            Database db = doc.Database;

            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

                if (!lt.Has(name))
                    return CommandResult.Fail($"Layer '{name}' not found");

                LayerTableRecord layer = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);

                // Apply each property if specified
                if (parameters["color"] != null)
                    layer.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)parameters["color"].Value<int>());

                if (parameters["is_off"] != null)
                    layer.IsOff = parameters["is_off"].Value<bool>();

                if (parameters["is_frozen"] != null)
                    layer.IsFrozen = parameters["is_frozen"].Value<bool>();

                if (parameters["is_locked"] != null)
                    layer.IsLocked = parameters["is_locked"].Value<bool>();

                if (parameters["linetype"] != null)
                {
                    string lt_name = parameters["linetype"].ToString();
                    LinetypeTable ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                    if (ltt.Has(lt_name))
                        layer.LinetypeObjectId = ltt[lt_name];
                }

                tr.Commit();
            }

            return CommandResult.Ok($"Layer '{name}' properties updated");
        }
    }
}
