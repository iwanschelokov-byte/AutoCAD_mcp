using Newtonsoft.Json.Linq;
using AutoCADMCPPlugin.Core;

namespace AutoCADMCPPlugin.Models
{
    /// <summary>
    /// Interface for all MCP commands that can be executed against AutoCAD.
    /// Each command receives JSON parameters and returns a CommandResult.
    ///
    /// Most commands run on AutoCAD's main thread (marshaled via IdleActionRunner).
    /// Commands with RunDirect = true execute immediately on the socket thread and
    /// must never touch the AutoCAD API.
    /// </summary>
    public interface ICommand
    {
        /// <summary>
        /// Unique method name for JSON-RPC routing (e.g., "create_line", "list_layers").
        /// </summary>
        string MethodName { get; }

        /// <summary>
        /// Execute the command with the given parameters.
        /// </summary>
        CommandResult Execute(JObject parameters);

        /// <summary>
        /// When true the command runs on the calling (socket) thread instead of being
        /// marshaled to AutoCAD's main thread. Only safe for commands that do not
        /// touch the AutoCAD API — introspection, settings, registry queries.
        /// Keeps tool discovery responsive while AutoCAD is busy or modal.
        /// </summary>
        bool RunDirect { get; }

        /// <summary>True when the command may modify the drawing database.</summary>
        bool IsWrite { get; }

        /// <summary>True when the command destroys data and requires confirmation.</summary>
        bool IsDestructive { get; }
    }

    /// <summary>
    /// Base class for commands that touch the AutoCAD API.
    /// Execution is marshaled to AutoCAD's main thread via IdleActionRunner.
    /// Write/destructive flags are inferred from the method name by
    /// CommandClassifier; override either property for a per-command exception.
    /// </summary>
    public abstract class AcadCommand : ICommand
    {
        public abstract string MethodName { get; }

        public abstract CommandResult Execute(JObject parameters);

        public virtual bool RunDirect => false;

        public virtual bool IsWrite => CommandClassifier.IsWrite(MethodName);

        public virtual bool IsDestructive => CommandClassifier.IsDestructive(MethodName);
    }

    /// <summary>
    /// Base class for commands that never touch the AutoCAD API and can therefore
    /// answer immediately on the socket thread — introspection, capabilities,
    /// settings. These stay responsive even when AutoCAD is showing a modal dialog.
    /// </summary>
    public abstract class DirectCommand : ICommand
    {
        public abstract string MethodName { get; }

        public abstract CommandResult Execute(JObject parameters);

        public bool RunDirect => true;

        public virtual bool IsWrite => false;

        public virtual bool IsDestructive => false;
    }
}
