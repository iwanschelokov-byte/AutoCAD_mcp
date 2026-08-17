namespace AutoCADMCPPlugin.Models
{
    /// <summary>
    /// Typed error categories surfaced to MCP clients in the JSON-RPC
    /// error payload as error.data.errorCode.
    ///
    /// These let an AI client branch programmatically on the failure kind
    /// instead of pattern-matching free-text error strings.
    /// </summary>
    public enum ErrorCode
    {
        /// <summary>No error (successful result).</summary>
        None = 0,

        /// <summary>A referenced entity, layer, block, style or layout does not exist.</summary>
        NotFound,

        /// <summary>A parameter is missing, malformed, or out of range.</summary>
        InvalidParam,

        /// <summary>Server is in read-only mode and the command would modify the drawing.</summary>
        ReadOnly,

        /// <summary>No drawing is currently open in AutoCAD.</summary>
        NoDocument,

        /// <summary>Destructive command requires "__confirm": true to proceed.</summary>
        NeedsConfirm,

        /// <summary>Operation is not supported by this AutoCAD version or entity type.</summary>
        Unsupported,

        /// <summary>The AutoCAD transaction failed or was aborted.</summary>
        TxnFailed,

        /// <summary>AutoCAD did not process the command in time (usually a modal dialog).</summary>
        Timeout,

        /// <summary>Unexpected internal failure.</summary>
        Internal
    }
}
