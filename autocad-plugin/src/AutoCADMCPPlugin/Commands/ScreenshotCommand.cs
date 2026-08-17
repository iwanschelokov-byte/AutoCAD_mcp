using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using AutoCADMCPPlugin.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCADMCPPlugin.Commands
{
    public class CaptureScreenshotCommand : AcadCommand
    {
        public override string MethodName => "capture_screenshot";

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        public override CommandResult Execute(JObject parameters)
        {
            try
            {
                string outputPath = parameters?["output_path"]?.ToString();

                if (string.IsNullOrEmpty(outputPath))
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "autocad_mcp_screenshots");
                    Directory.CreateDirectory(tempDir);
                    outputPath = Path.Combine(tempDir, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                }
                else
                {
                    // Ensure the directory exists
                    string dir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                }

                IntPtr hwnd = Application.MainWindow.Handle;
                if (hwnd == IntPtr.Zero)
                    return CommandResult.Fail("Could not get AutoCAD window handle");

                RECT rect;
                if (!GetWindowRect(hwnd, out rect))
                    return CommandResult.Fail("Could not get AutoCAD window dimensions");

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;

                if (width <= 0 || height <= 0)
                    return CommandResult.Fail("Invalid window dimensions");

                using (Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        IntPtr hdc = g.GetHdc();
                        // PW_RENDERFULLCONTENT (0x2) captures even if window is partially offscreen
                        bool success = PrintWindow(hwnd, hdc, 0x2);
                        g.ReleaseHdc(hdc);

                        if (!success)
                        {
                            // Fallback: use screen copy
                            g.CopyFromScreen(rect.Left, rect.Top, 0, 0,
                                new Size(width, height), CopyPixelOperation.SourceCopy);
                        }
                    }

                    bitmap.Save(outputPath, ImageFormat.Png);
                }

                // Verify file was created
                FileInfo fi = new FileInfo(outputPath);
                if (!fi.Exists || fi.Length == 0)
                    return CommandResult.Fail("Screenshot file was not created");

                var data = new JObject
                {
                    ["success"] = true,
                    ["file_path"] = outputPath,
                    ["width"] = width,
                    ["height"] = height,
                    ["file_size_kb"] = (int)(fi.Length / 1024),
                    ["message"] = $"Screenshot saved ({width}x{height}, {fi.Length / 1024}KB)"
                };
                return CommandResult.Ok(data);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to capture screenshot: {ex.Message}");
            }
        }
    }
}
