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
    /// <summary>
    /// Capture the AutoCAD window as a PNG or JPEG.
    ///
    /// Two things this command has to get right that the naive version did not:
    ///  * <b>area</b> - "window" grabs the whole application frame (ribbon,
    ///    palettes, command line); "drawing" crops to the MDI child window, which
    ///    is what a caller asking "what does the drawing look like" actually
    ///    wants, and is typically a third of the pixels.
    ///  * <b>size</b> - a 4K frame is several megabytes of PNG. The image is
    ///    downscaled to <c>max_width</c> (default 1600 px, 0 disables) and can be
    ///    written as JPEG, which for a CAD screen is roughly a tenth the bytes.
    /// Every reduction is reported back, so the caller always knows what it got.
    /// </summary>
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

        /// <summary>
        /// Handle of the MDI child window that holds the drawing area, or
        /// IntPtr.Zero when it cannot be reached.
        ///
        /// Document.Window is reached by reflection on purpose: the property
        /// exists in every AutoCAD version this plugin targets but lives in
        /// different assemblies, and a hard reference would tie the build to one
        /// of them. Failing to find it is not an error - the caller simply gets
        /// the full window and is told so via "area_used".
        /// </summary>
        private static IntPtr DrawingWindowHandle()
        {
            try
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return IntPtr.Zero;

                object window = doc.GetType()
                    .GetProperty("Window")?.GetValue(doc, null);
                if (window == null) return IntPtr.Zero;

                object handle = window.GetType()
                    .GetProperty("Handle")?.GetValue(window, null);
                if (handle is IntPtr ptr) return ptr;
            }
            catch { }
            return IntPtr.Zero;
        }

        private static ImageCodecInfo JpegCodec()
        {
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                if (c.MimeType == "image/jpeg") return c;
            return null;
        }

        public override CommandResult Execute(JObject parameters)
        {
            try
            {
                string format = (parameters?["format"]?.ToString() ?? "png").Trim().ToLowerInvariant();
                if (format == "jpg") format = "jpeg";
                if (format != "png" && format != "jpeg")
                    return CommandResult.Fail("Parameter 'format' must be \"png\" or \"jpeg\".");

                // What was asked for; Save() returns what was actually written,
                // which differs when the runtime has no JPEG encoder.
                string formatWritten = format;

                string areaWanted = (parameters?["area"]?.ToString() ?? "window").Trim().ToLowerInvariant();
                if (areaWanted == "model" || areaWanted == "canvas" || areaWanted == "viewport")
                    areaWanted = "drawing";
                if (areaWanted != "window" && areaWanted != "drawing")
                    return CommandResult.Fail("Parameter 'area' must be \"window\" or \"drawing\".");

                int maxWidth = parameters?["max_width"]?.Value<int>() ?? 1600;
                if (maxWidth < 0) maxWidth = 0;
                int quality = parameters?["quality"]?.Value<int>() ?? 85;
                if (quality < 20) quality = 20;
                if (quality > 100) quality = 100;

                string outputPath = parameters?["output_path"]?.ToString();
                string ext = format == "jpeg" ? ".jpg" : ".png";

                if (string.IsNullOrEmpty(outputPath))
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "autocad_mcp_screenshots");
                    Directory.CreateDirectory(tempDir);
                    outputPath = Path.Combine(tempDir, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
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

                // Crop rectangle inside the captured frame, in frame coordinates.
                string areaUsed = "window";
                string areaNote = null;
                int cropX = 0, cropY = 0, cropW = width, cropH = height;

                if (areaWanted == "drawing")
                {
                    IntPtr child = DrawingWindowHandle();
                    RECT cr;
                    if (child != IntPtr.Zero && GetWindowRect(child, out cr))
                    {
                        int x = cr.Left - rect.Left;
                        int y = cr.Top - rect.Top;
                        int w = cr.Right - cr.Left;
                        int h = cr.Bottom - cr.Top;

                        // Clamp to the frame; a maximised MDI child can report a
                        // rectangle a few pixels outside it.
                        if (x < 0) { w += x; x = 0; }
                        if (y < 0) { h += y; y = 0; }
                        if (x + w > width) w = width - x;
                        if (y + h > height) h = height - y;

                        if (w > 50 && h > 50)
                        {
                            cropX = x; cropY = y; cropW = w; cropH = h;
                            areaUsed = "drawing";
                        }
                        else
                        {
                            areaNote = "The drawing window reported an unusable rectangle " +
                                       $"({w}x{h}); captured the whole application window instead.";
                        }
                    }
                    else
                    {
                        areaNote = "Could not locate the drawing window (no open document, or the " +
                                   "MDI child is not available in this AutoCAD version); captured " +
                                   "the whole application window instead.";
                    }
                }

                int outW = cropW, outH = cropH;
                bool scaled = false;
                if (maxWidth > 0 && cropW > maxWidth)
                {
                    outW = maxWidth;
                    outH = Math.Max(1, (int)Math.Round(cropH * (maxWidth / (double)cropW)));
                    scaled = true;
                }

                using (Bitmap frame = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(frame))
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

                    if (areaUsed == "window" && !scaled)
                    {
                        formatWritten = Save(frame, outputPath, format, quality);
                    }
                    else
                    {
                        using (Bitmap outImage = new Bitmap(outW, outH, PixelFormat.Format32bppArgb))
                        {
                            using (Graphics g2 = Graphics.FromImage(outImage))
                            {
                                g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                g2.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                                g2.DrawImage(frame,
                                    new Rectangle(0, 0, outW, outH),
                                    new Rectangle(cropX, cropY, cropW, cropH),
                                    GraphicsUnit.Pixel);
                            }
                            formatWritten = Save(outImage, outputPath, format, quality);
                        }
                    }
                }

                // Verify file was created
                FileInfo fi = new FileInfo(outputPath);
                if (!fi.Exists || fi.Length == 0)
                    return CommandResult.Fail("Screenshot file was not created");

                var data = new JObject
                {
                    ["success"] = true,
                    ["file_path"] = outputPath,
                    ["width"] = outW,
                    ["height"] = outH,
                    ["format"] = formatWritten,
                    ["area_requested"] = areaWanted,
                    ["area_used"] = areaUsed,
                    ["captured_width"] = cropW,
                    ["captured_height"] = cropH,
                    ["window_width"] = width,
                    ["window_height"] = height,
                    ["scaled"] = scaled,
                    ["file_size_kb"] = (int)(fi.Length / 1024),
                    ["message"] = $"Screenshot saved ({outW}x{outH} {formatWritten}, {fi.Length / 1024}KB, area={areaUsed}" +
                                  (scaled ? $", downscaled from {cropW}x{cropH}" : "") + ")."
                };
                if (areaNote != null) data["area_note"] = areaNote;
                return CommandResult.Ok(data);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to capture screenshot: {ex.Message}");
            }
        }

        /// <summary>
        /// Write the bitmap and return the format actually used. JPEG needs an
        /// encoder that a stripped-down runtime may not have; falling back to PNG
        /// silently would leave the caller with PNG bytes in a .jpg file and a
        /// reply claiming JPEG, so the fallback is reported instead of hidden.
        /// </summary>
        private static string Save(Bitmap bmp, string path, string format, int quality)
        {
            if (format == "jpeg")
            {
                ImageCodecInfo codec = JpegCodec();
                if (codec != null)
                {
                    using (var ep = new EncoderParameters(1))
                    {
                        ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
                        // JPEG has no alpha; draw onto white first so the frame
                        // does not come out with black edges.
                        using (var flat = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb))
                        {
                            using (Graphics g = Graphics.FromImage(flat))
                            {
                                g.Clear(Color.White);
                                g.DrawImageUnscaled(bmp, 0, 0);
                            }
                            flat.Save(path, codec, ep);
                        }
                    }
                    return "jpeg";
                }
            }
            bmp.Save(path, ImageFormat.Png);
            return "png";
        }
    }
}
