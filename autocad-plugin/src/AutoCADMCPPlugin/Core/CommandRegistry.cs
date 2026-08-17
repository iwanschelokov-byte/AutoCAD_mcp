using System;
using System.Collections.Generic;
using AutoCADMCPPlugin.Models;
using AutoCADMCPPlugin.Commands;

namespace AutoCADMCPPlugin.Core
{
    /// <summary>
    /// Registry that maps JSON-RPC method names to command implementations.
    /// All commands are registered at startup. New commands can be added
    /// by implementing ICommand and registering here.
    /// </summary>
    public static class CommandRegistry
    {
        private static readonly Dictionary<string, ICommand> _commands = new Dictionary<string, ICommand>(StringComparer.OrdinalIgnoreCase);
        private static volatile bool _initialized;
        private static readonly object _initLock = new object();

        /// <summary>
        /// Get a command by its JSON-RPC method name.
        /// Returns null if the method is not registered.
        /// </summary>
        public static ICommand GetCommand(string method)
        {
            EnsureInitialized();
            _commands.TryGetValue(method, out ICommand command);
            return command;
        }

        /// <summary>
        /// Get all registered method names.
        /// </summary>
        public static IEnumerable<string> GetAllMethods()
        {
            EnsureInitialized();
            return _commands.Keys;
        }

        private static void Register(ICommand command)
        {
            _commands[command.MethodName] = command;
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;
                RegisterAllCommands();
                _initialized = true;
            }
        }

        private static void RegisterAllCommands()
        {
            // === System ===
            Register(new SystemStatusCommand());
            Register(new ListMethodsCommand());
            Register(new GetCapabilitiesCommand());
            Register(new GetServerOptionsCommand());
            Register(new SetServerOptionsCommand());
            Register(new SetSystemVariableCommand());
            Register(new GetSystemVariableCommand());
            Register(new ExecuteCommandCommand());
            Register(new ReadCommandLineCommand());

            // === Drawing / Document ===
            Register(new DrawingNewCommand());
            Register(new DrawingOpenCommand());
            Register(new DrawingSaveCommand());
            Register(new DrawingInfoCommand());
            Register(new DrawingListCommand());
            Register(new DrawingCloseCommand());
            Register(new CloseAllCommand());

            // === Entity Creation ===
            Register(new CreateLineCommand());
            Register(new CreateCircleCommand());
            Register(new CreateArcCommand());
            Register(new CreatePolylineCommand());
            Register(new CreateRectangleCommand());
            Register(new CreateEllipseCommand());
            Register(new CreateTextCommand());
            Register(new CreateMTextCommand());
            Register(new CreateHatchCommand());

            // === Entity Query & Modification ===
            Register(new ListEntitiesCommand());
            Register(new GetEntityCommand());
            Register(new GetEntitiesCommand());
            Register(new EraseEntityCommand());
            Register(new MoveEntityCommand());
            Register(new CopyEntityCommand());
            Register(new RotateEntityCommand());
            Register(new ScaleEntityCommand());
            Register(new MirrorEntityCommand());

            // === Advanced Entity Modification (Sprint 1-2) ===
            Register(new SetEntityPropertiesCommand());
            Register(new OffsetEntityCommand());
            Register(new ExplodeEntityCommand());
            Register(new ArrayRectangularCommand());
            Register(new ArrayPolarCommand());
            Register(new JoinEntitiesCommand());
            Register(new BulkEraseCommand());
            Register(new UndoCommand());

            // === Layers ===
            Register(new ListLayersCommand());
            Register(new CreateLayerCommand());
            Register(new SetCurrentLayerCommand());
            Register(new SetLayerPropertiesCommand());
            Register(new DeleteLayerCommand());
            Register(new RenameLayerCommand());

            // === Blocks ===
            Register(new ListBlocksCommand());
            Register(new InsertBlockCommand());
            Register(new CreateBlockCommand());
            Register(new ImportBlockCommand());

            // === Styles (Sprint 1) ===
            Register(new CreateDimensionStyleCommand());
            Register(new CreateTextStyleCommand());
            Register(new ListDimensionStylesCommand());
            Register(new ListTextStylesCommand());

            // === Query & Measurement (Sprint 1) ===
            Register(new MeasureDistanceCommand());
            Register(new MeasureAreaCommand());
            Register(new GetBoundingBoxCommand());
            Register(new SelectByWindowCommand());
            Register(new SelectByPropertiesCommand());
            Register(new FindIntersectionsCommand());
            Register(new SearchTextCommand());
            Register(new FindNearestCommand());
            Register(new MeasureBetweenCommand());

            // === Annotations ===
            Register(new CreateLinearDimensionCommand());
            Register(new CreateAlignedDimensionCommand());
            Register(new CreateAngularDimensionCommand());
            Register(new CreateRadialDimensionCommand());
            Register(new CreateDiameterDimensionCommand());
            Register(new CreateLeaderCommand());

            // === Advanced Entity Creation (Sprint 3) ===
            Register(new CreateSplineCommand());
            Register(new CreateTableCommand());
            Register(new BulkCreateCommand());

            // === Drawing Utilities (Sprint 4) ===
            Register(new PurgeDrawingCommand());
            Register(new SetUnitsCommand());

            // === Plotting ===
            Register(new PlotToPdfCommand());
            Register(new PlotDevicesCommand());

            // === View ===
            Register(new ZoomExtentsCommand());
            Register(new ZoomWindowCommand());

            // === Text Measurement ===
            Register(new MeasureTextCommand());
            Register(new MeasureTextsCommand());

            // === Screenshot ===
            Register(new CaptureScreenshotCommand());

            // ================================================================
            // v2.0 — Layouts, paper space, page setup, plotting
            // ================================================================
            Register(new ListLayoutsCommand());
            Register(new CreateLayoutCommand());
            Register(new DeleteLayoutCommand());
            Register(new RenameLayoutCommand());
            Register(new SetCurrentLayoutCommand());
            Register(new CopyLayoutCommand());
            Register(new GetPageSetupCommand());
            Register(new SetPageSetupCommand());
            Register(new ListViewportsCommand());
            Register(new CreateViewportCommand());
            Register(new SetViewportScaleCommand());
            Register(new LockViewportCommand());

            // ================================================================
            // v2.0 — Xrefs and external drawing data
            // ================================================================
            Register(new AttachXrefCommand());
            Register(new ListXrefsCommand());
            Register(new ReloadXrefCommand());
            Register(new UnloadXrefCommand());
            Register(new DetachXrefCommand());
            Register(new BindXrefCommand());
            Register(new SetXrefPathCommand());
            Register(new ReadExternalDwgCommand());
            Register(new BatchQueryDwgsCommand());

            // ================================================================
            // v2.0 — Block attributes and dynamic blocks
            // ================================================================
            Register(new ListBlockAttributesCommand());
            Register(new GetAttributeValuesCommand());
            Register(new SetAttributeValuesCommand());
            Register(new SyncAttributesCommand());
            Register(new GetDynamicBlockPropertiesCommand());
            Register(new SetDynamicBlockPropertyCommand());
            Register(new RenameBlockCommand());
            Register(new DeleteBlockDefinitionCommand());
            Register(new CountBlockReferencesCommand());
            Register(new ExportBlockToFileCommand());

            // ================================================================
            // v2.0 — Modify operations
            // ================================================================
            Register(new BreakEntityCommand());
            Register(new ReversePolylineCommand());
            Register(new PolylineEditCommand());
            Register(new SetDrawOrderCommand());
            Register(new FlattenEntitiesCommand());
            Register(new DivideEntityCommand());
            Register(new MeasureEntityCommand());
            Register(new CreateRegionCommand());
            Register(new CreateBoundaryCommand());
            Register(new FilletEntitiesCommand());
            Register(new OverkillCommand());

            // ================================================================
            // v2.0 — Additional 2D entities and 3D solids
            // ================================================================
            Register(new CreatePointCommand());
            Register(new CreateXlineCommand());
            Register(new CreateRayCommand());
            Register(new CreatePolygonCommand());
            Register(new CreateDonutCommand());
            Register(new Create3dPolylineCommand());
            Register(new CreateBoxCommand());
            Register(new CreateSphereCommand());
            Register(new CreateCylinderCommand());
            Register(new CreateConeCommand());
            Register(new CreateWedgeCommand());
            Register(new CreateTorusCommand());
            Register(new ExtrudeProfileCommand());
            Register(new RevolveProfileCommand());
            Register(new BooleanSolidsCommand());
            Register(new GetSolidPropertiesCommand());

            // ================================================================
            // v2.0 — Groups, layer states, views, UCS, data, audit
            // ================================================================
            Register(new CreateGroupCommand());
            Register(new ListGroupsCommand());
            Register(new AddToGroupCommand());
            Register(new UngroupCommand());
            Register(new SaveLayerStateCommand());
            Register(new RestoreLayerStateCommand());
            Register(new ListLayerStatesCommand());
            Register(new DeleteLayerStateCommand());
            Register(new CreateNamedViewCommand());
            Register(new ListNamedViewsCommand());
            Register(new RestoreViewCommand());
            Register(new ListUcsCommand());
            Register(new SetUcsCommand());
            Register(new GetXDataCommand());
            Register(new SetXDataCommand());
            Register(new GetDrawingPropertiesCommand());
            Register(new SetDrawingPropertiesCommand());
            Register(new EntityCountReportCommand());
            Register(new AuditDrawingCommand());

            // ================================================================
            // v2.0 — Annotation completion
            // ================================================================
            Register(new CreateMultileaderCommand());
            Register(new ListMleaderStylesCommand());
            Register(new CreateMleaderStyleCommand());
            Register(new CreateOrdinateDimensionCommand());
            Register(new CreateArcLengthDimensionCommand());
            Register(new CreateToleranceCommand());
            Register(new EditDimensionTextCommand());
            Register(new UpdateDimensionsCommand());
            Register(new ListAnnotationScalesCommand());
            Register(new SetAnnotationScaleCommand());
            Register(new AddAnnotationScaleToEntityCommand());
            Register(new GetTableDataCommand());
            Register(new SetTableCellCommand());
            Register(new MergeTableCellsCommand());
            Register(new ListTableStylesCommand());
            Register(new EditMtextCommand());
            Register(new CreateWipeoutCommand());
            Register(new CreateRevisionCloudCommand());

            // ================================================================
            // v2.0 — Sheet sets (COM; read-only, degrades gracefully)
            // ================================================================
            Register(new SheetSetStatusCommand());
            Register(new OpenSheetSetCommand());
            Register(new ListSheetsCommand());
            Register(new CloseSheetSetCommand());
        }
    }
}
