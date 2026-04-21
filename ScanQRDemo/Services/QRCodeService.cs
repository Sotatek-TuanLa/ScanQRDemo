using OpenCvSharp;
using ScanQRDemo.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using ZXing.Windows.Compatibility;

namespace ScanQRDemo.Services
{
    public class QRCodeService : IQRCodeService
    {
        // ── OpenCV QR detector (reused across calls) ──────────────────────────
        private readonly QRCodeDetector _detector = new QRCodeDetector();

        // ── ZXing reader: AutoRotate=false, TryHarder=false ──────────────────
        // AutoRotate  tries 4 orientations  →  ~4× decode time.  Disable it
        // because industrial cameras are typically axis-aligned or very close.
        // TryHarder   does exhaustive search →  150–300 ms extra.  Disable it
        // so we use it only as a fast fallback when OpenCV finds a region but
        // cannot decode the payload.
        private readonly BarcodeReader _reader = new BarcodeReader
        {
            AutoRotate = false,
            Options = new ZXing.Common.DecodingOptions
            {
                TryHarder    = false,   // fast path; enable if very damaged QRs needed
                TryInverted  = true,    // cheap – just inverts pixels
                PossibleFormats = new List<ZXing.BarcodeFormat>
                {
                    ZXing.BarcodeFormat.QR_CODE
                }
            }
        };

        /// <summary>
        /// Detect and decode all QR codes present in <paramref name="frame"/>.
        /// Optimised for low latency on a background thread.
        /// </summary>
        public List<QRCodeResult> Detect(Mat frame)
        {
            var results = new List<QRCodeResult>();

            if (frame == null || frame.Empty())
                return results;

            // Clone once so we can mask found QRs without touching the caller's Mat
            using var working = frame.Clone();
            int id      = 1;
            const int maxLoop = 5;   // real-world scenes rarely have > 5 QRs

            for (int i = 0; i < maxLoop; i++)
            {
                Point2f[] points;
                string text = _detector.DetectAndDecode(working, out points);

                // No region found at all – we are done
                if (points == null || points.Length == 0)
                    break;

                var cvPoints = points
                    .Select(p => new OpenCvSharp.Point((int)p.X, (int)p.Y))
                    .ToArray();

                string finalText;

                if (!string.IsNullOrEmpty(text))
                {
                    // OpenCV decoded successfully – skip the expensive ZXing call
                    finalText = text;
                }
                else
                {
                    // OpenCV found the region but couldn't decode →
                    // fall back to ZXing on the cropped region only (fast – small bitmap)
                    finalText = DecodeZXing(frame, cvPoints) ?? string.Empty;
                }

                if (!string.IsNullOrEmpty(finalText))
                {
                    results.Add(new QRCodeResult
                    {
                        Id       = id++,
                        Text     = finalText,
                        Points   = cvPoints,
                        LastSeen = DateTime.Now
                    });
                }

                // Mask this QR so the next iteration can find the next one
                Cv2.FillConvexPoly(working, cvPoints, Scalar.Black);
            }

            return results;
        }

        // ── ZXing region-crop decode ──────────────────────────────────────────
        private string? DecodeZXing(Mat frame, OpenCvSharp.Point[] region)
        {
            try
            {
                var rect = Cv2.BoundingRect(region);

                const int pad = 8;
                rect = new Rect(
                    Math.Max(0, rect.X - pad),
                    Math.Max(0, rect.Y - pad),
                    Math.Min(frame.Width  - rect.X + pad, rect.Width  + pad * 2),
                    Math.Min(frame.Height - rect.Y + pad, rect.Height + pad * 2));

                using var cropped = new Mat(frame, rect);
                using var bitmap  = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(cropped);

                var result = _reader.Decode(bitmap);
                return result?.Text;
            }
            catch
            {
                return null;
            }
        }
    }
}