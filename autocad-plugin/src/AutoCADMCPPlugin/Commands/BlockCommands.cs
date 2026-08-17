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
using static AutoCADMCPPlugin.Commands.EntityHelper;

namespace AutoCADMCPPlugin.Commands
{
    public class ListBlocksCommand : AcadCommand
    {
        public override string MethodName => "list_blocks";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            Database db = doc.Database;
            JArray blocks = new JArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                foreach (ObjectId blockId in bt)
                {
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);

                    // Skip model/paper space and anonymous blocks
                    if (btr.IsLayout || btr.IsAnonymous) continue;

                    var blockInfo = new JObject
                    {
                        ["name"] = btr.Name,
                        ["is_dynamic"] = btr.IsDynamicBlock,
                        ["has_attributes"] = btr.HasAttributeDefinitions
                    };

                    // Count entities in the block
                    int entityCount = 0;
                    foreach (ObjectId id in btr)
                        entityCount++;
                    blockInfo["entity_count"] = entityCount;

                    // List attribute definitions
                    if (btr.HasAttributeDefinitions)
                    {
                        JArray attrs = new JArray();
                        foreach (ObjectId id in btr)
                        {
                            DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                            if (obj is AttributeDefinition attDef)
                            {
                                attrs.Add(new JObject
                                {
                                    ["tag"] = attDef.Tag,
                                    ["prompt"] = attDef.Prompt,
                                    ["default_value"] = attDef.TextString
                                });
                            }
                        }
                        blockInfo["attributes"] = attrs;
                    }

                    blocks.Add(blockInfo);
                }

                tr.Commit();
            }

            return CommandResult.Ok(new JObject
            {
                ["blocks"] = blocks,
                ["count"] = blocks.Count
            });
        }
    }

    public class InsertBlockCommand : AcadCommand
    {
        public override string MethodName => "insert_block";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string blockName = parameters["name"]?.ToString();
            if (string.IsNullOrEmpty(blockName))
                return CommandResult.Fail("Parameter 'name' is required");

            Point3d position = EntityHelper.ParsePoint(parameters["position"], "position");
            double rotation = (parameters["rotation"]?.Value<double>() ?? 0) * Math.PI / 180.0;
            double scaleX = parameters["scale_x"]?.Value<double>() ?? 1.0;
            double scaleY = parameters["scale_y"]?.Value<double>() ?? 1.0;
            double scaleZ = parameters["scale_z"]?.Value<double>() ?? 1.0;

            Database db = doc.Database;

            using (LockDoc())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                if (!bt.Has(blockName))
                    return CommandResult.Fail($"Block '{blockName}' not found in drawing");

                ObjectId blockDefId = bt[blockName];
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                using (BlockReference blkRef = new BlockReference(position, blockDefId))
                {
                    blkRef.Rotation = rotation;
                    blkRef.ScaleFactors = new Scale3d(scaleX, scaleY, scaleZ);

                    // Set layer if specified
                    string layer = parameters["layer"]?.ToString();
                    if (!string.IsNullOrEmpty(layer))
                    {
                        LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        if (lt.Has(layer))
                            blkRef.Layer = layer;
                    }

                    ObjectId refId = modelSpace.AppendEntity(blkRef);
                    tr.AddNewlyCreatedDBObject(blkRef, true);

                    // Set attribute values if provided
                    JObject attrValues = parameters["attributes"] as JObject;
                    if (attrValues != null)
                    {
                        BlockTableRecord blockDef = (BlockTableRecord)tr.GetObject(blockDefId, OpenMode.ForRead);
                        foreach (ObjectId attId in blockDef)
                        {
                            DBObject obj = tr.GetObject(attId, OpenMode.ForRead);
                            if (obj is AttributeDefinition attDef && !attDef.Constant)
                            {
                                AttributeReference attRef = new AttributeReference();
                                attRef.SetAttributeFromBlock(attDef, blkRef.BlockTransform);

                                string val = attrValues[attDef.Tag]?.ToString();
                                if (val != null)
                                    attRef.TextString = val;

                                blkRef.AttributeCollection.AppendAttribute(attRef);
                                tr.AddNewlyCreatedDBObject(attRef, true);
                            }
                        }
                    }

                    tr.Commit();

                    var result = EntityHelper.EntityToJson(refId);
                    result["type"] = "BlockReference";
                    result["block_name"] = blockName;
                    return CommandResult.Ok(result);
                }
            }
        }
    }

    /// <summary>
    /// Import block definitions from an external DWG file into the current drawing.
    /// Uses Database.Insert() / WblockCloneObjects() to properly transfer blocks
    /// without the issues of -INSERT via SendStringToExecute.
    /// </summary>
    public class ImportBlockCommand : AcadCommand
    {
        public override string MethodName => "import_block";

        public override CommandResult Execute(JObject parameters)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return CommandResult.Fail("No active document");

            string sourcePath = parameters["source_path"]?.ToString();
            if (string.IsNullOrWhiteSpace(sourcePath))
                return CommandResult.Fail("Parameter 'source_path' is required (path to source .dwg file)");

            if (!File.Exists(sourcePath))
                return CommandResult.Fail($"Source file not found: {sourcePath}");

            // Optional: specific block names to import. If empty, import all user blocks.
            JArray blockNamesArr = parameters["block_names"] as JArray;
            HashSet<string> requestedBlocks = null;
            if (blockNamesArr != null && blockNamesArr.Count > 0)
                requestedBlocks = new HashSet<string>(
                    blockNamesArr.Select(b => b.ToString()),
                    StringComparer.OrdinalIgnoreCase);

            Database destDb = doc.Database;
            JArray imported = new JArray();
            JArray skipped = new JArray();

            using (LockDoc())
            {
                // Open the source drawing as a separate database
                using (Database srcDb = new Database(false, true))
                {
                    srcDb.ReadDwgFile(sourcePath, FileOpenMode.OpenForReadAndAllShare, true, null);

                    using (Transaction srcTr = srcDb.TransactionManager.StartTransaction())
                    {
                        BlockTable srcBt = (BlockTable)srcTr.GetObject(srcDb.BlockTableId, OpenMode.ForRead);
                        ObjectIdCollection blockIds = new ObjectIdCollection();

                        foreach (ObjectId blockId in srcBt)
                        {
                            BlockTableRecord btr = (BlockTableRecord)srcTr.GetObject(blockId, OpenMode.ForRead);

                            // Skip model/paper space and anonymous blocks
                            if (btr.IsLayout || btr.IsAnonymous) continue;

                            // If specific blocks requested, filter
                            if (requestedBlocks != null && !requestedBlocks.Contains(btr.Name))
                                continue;

                            blockIds.Add(blockId);
                        }

                        if (blockIds.Count == 0)
                        {
                            srcTr.Commit();
                            return CommandResult.Fail(
                                requestedBlocks != null
                                    ? $"None of the requested blocks found in {Path.GetFileName(sourcePath)}"
                                    : $"No user blocks found in {Path.GetFileName(sourcePath)}");
                        }

                        // Clone blocks into the destination database
                        IdMapping idMap = new IdMapping();
                        using (Transaction destTr = destDb.TransactionManager.StartTransaction())
                        {
                            BlockTable destBt = (BlockTable)destTr.GetObject(destDb.BlockTableId, OpenMode.ForRead);

                            destDb.WblockCloneObjects(
                                blockIds,
                                destDb.BlockTableId,
                                idMap,
                                DuplicateRecordCloning.Ignore,
                                false);

                            // Report what was imported
                            foreach (ObjectId blockId in blockIds)
                            {
                                BlockTableRecord btr = (BlockTableRecord)srcTr.GetObject(blockId, OpenMode.ForRead);
                                if (destBt.Has(btr.Name))
                                {
                                    imported.Add(btr.Name);
                                }
                                else
                                {
                                    skipped.Add(btr.Name);
                                }
                            }

                            destTr.Commit();
                        }

                        srcTr.Commit();
                    }
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["source"] = Path.GetFileName(sourcePath),
                ["imported"] = imported,
                ["imported_count"] = imported.Count,
                ["skipped_existing"] = skipped,
                ["message"] = $"Imported {imported.Count} block(s) from {Path.GetFileName(sourcePath)}"
            });
        }
    }
}
