using System;

namespace AutoCADMCPPlugin.Core
{
    /// <summary>
    /// Single place where AutoCAD / .NET version drift is isolated.
    ///
    /// The plugin ships three binaries from one source tree:
    ///   net48           → AutoCAD 2021-2024 (R24.0-R24.3, .NET Framework 4.8)
    ///   net8.0-windows  → AutoCAD 2025-2026 (R25.0-R25.1, .NET 8)
    ///   net10.0-windows → AutoCAD 2027      (R26.0,       .NET 10)
    ///
    /// Every #if in the codebase belongs here so the other ~100 files stay
    /// source-identical across all targets (the pattern the Revit MCP uses in
    /// IdCompat.cs).
    /// </summary>
    public static class AcadCompat
    {
        /// <summary>The .NET target this binary was compiled for.</summary>
        public static string TargetFramework
        {
            get
            {
// The SDK defines NET8_0 / NET10_0 for net8.0-windows / net10.0-windows.
                // There is no combined NET8_0_WINDOWS symbol - assuming there was
                // made both branches dead code, and every non-net48 build silently
                // reported "unknown" here. Check the newest first: NET8_0 is not
                // defined for a net10 build, but keeping the order explicit means a
                // future net12 leg cannot accidentally match an older branch.
#if NET48
                return "net48";
#elif NET10_0
                return "net10.0-windows";
#elif NET8_0
                return "net8.0-windows";
#else
#error Unrecognised target framework - add it to AcadCompat rather than letting it report "unknown".
#endif
            }
        }

        /// <summary>Human-readable range of AutoCAD releases this binary serves.</summary>
        public static string SupportedAutoCadRange
        {
            get
            {
#if NET48
                return "AutoCAD 2021-2024 (R24.0-R24.3)";
#elif NET10_0
                return "AutoCAD 2027 (R26.0)";
#elif NET8_0
                return "AutoCAD 2025-2026 (R25.0-R25.1)";
#else
#error Unrecognised target framework - add it to AcadCompat rather than letting it report "unknown".
#endif
            }
        }
    }
}
