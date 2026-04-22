using OpenCvSharp;
using ScanQRDemo.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZXing.Windows.Compatibility;

namespace ScanQRDemo.Services
{
    public class QRCodeService : IQRCodeService
    {
        // Fast formats — 5 common types, much faster than all 12
        private static readonly List<ZXing.BarcodeFormat> FastFormats = new()
        {
            ZXing.BarcodeFormat.QR_CODE,
            ZXing.BarcodeFormat.DATA_MATRIX,
            ZXing.BarcodeFormat.CODE_128,
            ZXing.BarcodeFormat.EAN_13,
            ZXing.BarcodeFormat.UPC_A,
        };

        private static readonly List<ZXing.BarcodeFormat> AllFormats = new()
        {
            ZXing.BarcodeFormat.QR_CODE, ZXing.BarcodeFormat.DATA_MATRIX,
            ZXing.BarcodeFormat.PDF_417, ZXing.BarcodeFormat.AZTEC,
            ZXing.BarcodeFormat.CODE_128, ZXing.BarcodeFormat.CODE_39,
            ZXing.BarcodeFormat.CODE_93, ZXing.BarcodeFormat.EAN_13,
            ZXing.BarcodeFormat.EAN_8, ZXing.BarcodeFormat.UPC_A,
            ZXing.BarcodeFormat.UPC_E, ZXing.BarcodeFormat.ITF,
        };

        // ThreadLocal readers — one instance per thread, created once, never re-allocated
        private static readonly ThreadLocal<BarcodeReader> _fastReaderTL =
            new ThreadLocal<BarcodeReader>(() => new BarcodeReader
            {
                AutoRotate = false,
                Options = new ZXing.Common.DecodingOptions
                {
                    TryHarder       = true,   // catches angled / small codes
                    TryInverted     = false,
                    PossibleFormats = FastFormats
                }
            });

        private static readonly ThreadLocal<BarcodeReader> _hardReaderTL =
            new ThreadLocal<BarcodeReader>(() => new BarcodeReader
            {
                AutoRotate = false,
                Options = new ZXing.Common.DecodingOptions
                {
                    TryHarder       = true,
                    TryInverted     = true,
                    PossibleFormats = AllFormats
                }
            });

        private static BarcodeReader FastReader => _fastReaderTL.Value!;
        private static BarcodeReader HardReader => _hardReaderTL.Value!;

        private readonly QRCodeDetector _cvDetector = new QRCodeDetector();

        private const double DarkThreshold = 80.0;
        private const int    MinWorkWidth  = 640;  // upscale tiny camera frames to this
        private const int    MaxWorkWidth  = 1280; // downscale huge images to this for live

        public List<QRCodeResult> Detect(Mat frame, int maxWidth = MaxWorkWidth, bool isStillImage = false)
        {
            if (frame == null || frame.Empty()) return new List<QRCodeResult>();

            // ── Working resolution: clamp into [MinWorkWidth, maxWidth] ───────
            // Small cameras (e.g. 80×80) are upscaled so decoders have enough pixels.
            // Large images are downscaled so decoders run fast.
            double scale = 1.0;
            Mat working;

            int targetW = Math.Clamp(frame.Width, MinWorkWidth, maxWidth);
            if (frame.Width != targetW)
            {
                scale   = (double)targetW / frame.Width;
                working = new Mat();
                Cv2.Resize(frame, working,
                    new Size(targetW, (int)(frame.Height * scale)),
                    interpolation: scale < 1.0 ? InterpolationFlags.Area : InterpolationFlags.Cubic);
            }
            else
            {
                working = frame.Clone();
            }

            // invScale converts working-space coords back to original frame space
            double invScale = scale > 0 ? 1.0 / scale : 1.0;

            using (working)
            {
                using var gray = new Mat();
                if (working.Channels() == 1) working.CopyTo(gray);
                else Cv2.CvtColor(working, gray, ColorConversionCodes.BGR2GRAY);

                bool isDark = Cv2.Mean(gray).Val0 < DarkThreshold;
                using var clahe = ApplyClahe(gray, isDark ? 3.5 : 2.0);

                // ── PASS 1: OpenCV multi-QR + ZXing full image [parallel] ─────
                var cvRes    = new List<QRCodeResult>();
                var zxingRes = new List<QRCodeResult>();

                Parallel.Invoke(
                    () => cvRes    = ScanOpenCvMulti(_cvDetector, clahe, invScale, frame.Width, frame.Height),
                    () => zxingRes = ScanFull(clahe, FastReader, invScale, frame.Width, frame.Height)
                );

                var merged = MergeTwo(cvRes, zxingRes);

                // Live fast-exit: Pass 1 found everything we need
                if (merged.Count > 0 && !isStillImage)
                    return Numbered(merged);

                // ── PASS 2: 4-quadrant tiles [parallel] ──────────────────────
                int halfW = clahe.Width  / 2;
                int halfH = clahe.Height / 2;
                var offX  = new[] { 0, halfW, 0,     halfW };
                var offY  = new[] { 0, 0,     halfH, halfH };
                var rects = new[]
                {
                    new Rect(0, 0, halfW, halfH), new Rect(halfW, 0, halfW, halfH),
                    new Rect(0, halfH, halfW, halfH), new Rect(halfW, halfH, halfW, halfH),
                };
                var tiles      = rects.Select(r => clahe[r].Clone()).ToArray();
                var tileResult = new List<QRCodeResult>[4];
                try
                {
                    Parallel.For(0, 4, i =>
                        tileResult[i] = ScanTile(tiles[i], FastReader, offX[i], offY[i],
                                                 invScale, frame.Width, frame.Height));
                }
                finally { foreach (var t in tiles) t?.Dispose(); }

                merged = MergeTwo(merged, Merge(tileResult));

                // ── PASS 3: Adaptive threshold fallback ───────────────────────
                if (merged.Count == 0 || isStillImage)
                {
                    using var adaptive = ApplyAdaptiveThreshold(gray);
                    merged = MergeTwo(merged,
                        ScanFull(adaptive, FastReader, invScale, frame.Width, frame.Height));
                }

                if (!isStillImage) return Numbered(merged);

                // ── STILL IMAGE ONLY: deep passes ─────────────────────────────

                // Pass 4: 3×3 overlapping grid
                merged = MergeTwo(merged,
                    ScanOverlappingGrid(clahe, 3, 3, 0.20, invScale, frame.Width, frame.Height));

                // Pass 5: Strong CLAHE + hard reader
                using var claheHard = ApplyClahe(gray, isDark ? 5.0 : 3.5);
                merged = MergeTwo(merged,
                    ScanFull(claheHard, HardReader, invScale, frame.Width, frame.Height));

                // Pass 6: Full-resolution (if we downscaled)
                if (scale < 1.0)
                    merged = MergeTwo(merged, DetectFullRes(frame));

                return Numbered(merged);
            }
        }

        // ── Full-resolution last-resort (still images only) ───────────────────
        private static List<QRCodeResult> DetectFullRes(Mat original)
        {
            var results = new List<QRCodeResult>();
            try
            {
                using var gray = new Mat();
                if (original.Channels() == 1) original.CopyTo(gray);
                else Cv2.CvtColor(original, gray, ColorConversionCodes.BGR2GRAY);

                bool isDark = Cv2.Mean(gray).Val0 < DarkThreshold;
                using var clahe = ApplyClahe(gray, isDark ? 4.0 : 2.5);

                results = MergeTwo(results,
                    ScanFull(clahe, HardReader, 1.0, original.Width, original.Height));
                results = MergeTwo(results,
                    ScanOverlappingGrid(clahe, 3, 3, 0.20, 1.0, original.Width, original.Height));

                using var adaptive = ApplyAdaptiveThreshold(gray);
                results = MergeTwo(results,
                    ScanFull(adaptive, HardReader, 1.0, original.Width, original.Height));
            }
            catch { }
            return results;
        }

        // ── OpenCV two-step multi-QR ──────────────────────────────────────────
        private static List<QRCodeResult> ScanOpenCvMulti(
            QRCodeDetector det, Mat img, double invScale, int origW, int origH)
        {
            var results = new List<QRCodeResult>();
            try
            {
                if (!det.DetectMulti(img, out Point2f[] pts) || pts == null || pts.Length < 4)
                    return results;
                if (!det.DecodeMulti(img, pts, out string?[] decoded) || decoded == null)
                    return results;

                for (int i = 0; i < decoded.Length; i++)
                {
                    if (string.IsNullOrEmpty(decoded[i])) continue;
                    int b = i * 4;
                    Point[]? poly = null;
                    if (b + 3 < pts.Length)
                    {
                        poly = new Point[4];
                        for (int c = 0; c < 4; c++)
                            poly[c] = new Point(
                                Clamp((int)Math.Round(pts[b + c].X * invScale), 0, origW),
                                Clamp((int)Math.Round(pts[b + c].Y * invScale), 0, origH));
                    }
                    results.Add(new QRCodeResult
                    {
                        Text = decoded[i]!, Format = "QR_CODE",
                        Points = poly, LastSeen = DateTime.Now
                    });
                }
            }
            catch { }
            return results;
        }

        // ── ZXing on full image ───────────────────────────────────────────────
        private static List<QRCodeResult> ScanFull(
            Mat img, BarcodeReader reader, double invScale, int origW, int origH)
        {
            var results = new List<QRCodeResult>();
            try
            {
                using var bmp = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(img);
                var raw = reader.DecodeMultiple(bmp);
                if (raw == null) return results;
                foreach (var r in raw)
                {
                    if (r == null || string.IsNullOrEmpty(r.Text)) continue;
                    results.Add(new QRCodeResult
                    {
                        Text = r.Text, Format = r.BarcodeFormat.ToString(),
                        Points = BuildPolygon(r.ResultPoints, 0, 0, invScale, origW, origH),
                        LastSeen = DateTime.Now
                    });
                }
            }
            catch { }
            return results;
        }

        // ── ZXing on a single tile ────────────────────────────────────────────
        private static List<QRCodeResult> ScanTile(
            Mat tile, BarcodeReader reader,
            int offX, int offY, double invScale, int origW, int origH)
        {
            var results = new List<QRCodeResult>();
            try
            {
                using var bmp = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(tile);
                // Decode() (single) is 2-4× faster than DecodeMultiple for per-quadrant tiles
                var r = reader.Decode(bmp);
                if (r == null || string.IsNullOrEmpty(r.Text)) return results;
                results.Add(new QRCodeResult
                {
                    Text = r.Text, Format = r.BarcodeFormat.ToString(),
                    Points = BuildPolygon(r.ResultPoints, offX, offY, invScale, origW, origH),
                    LastSeen = DateTime.Now
                });
            }
            catch { }
            return results;
        }

        // ── Overlapping grid (still images only) ──────────────────────────────
        private static List<QRCodeResult> ScanOverlappingGrid(
            Mat img, int cols, int rows, double overlap,
            double invScale, int origW, int origH)
        {
            int tileW = img.Width  / cols;
            int tileH = img.Height / rows;
            int padW  = (int)(tileW * overlap);
            int padH  = (int)(tileH * overlap);

            var tasks = new List<(Mat tile, int ox, int oy)>();
            for (int row = 0; row < rows; row++)
                for (int col = 0; col < cols; col++)
                {
                    int x0 = Math.Max(0, col * tileW - padW);
                    int y0 = Math.Max(0, row * tileH - padH);
                    int x1 = Math.Min(img.Width,  col * tileW + tileW + padW);
                    int y1 = Math.Min(img.Height, row * tileH + tileH + padH);
                    var rect = new Rect(x0, y0, x1 - x0, y1 - y0);
                    if (rect.Width > 0 && rect.Height > 0)
                        tasks.Add((img[rect].Clone(), x0, y0));
                }

            var tileResults = new List<QRCodeResult>[tasks.Count];
            try
            {
                Parallel.For(0, tasks.Count, i =>
                {
                    var (tile, ox, oy) = tasks[i];
                    tileResults[i] = ScanTile(tile, HardReader, ox, oy, invScale, origW, origH);
                });
            }
            finally { foreach (var (tile, _, _) in tasks) tile?.Dispose(); }

            return Merge(tileResults);
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static List<QRCodeResult> Merge(IEnumerable<List<QRCodeResult>?> lists)
        {
            var merged = new List<QRCodeResult>();
            foreach (var r in lists.Where(l => l != null).SelectMany(l => l!))
                if (!merged.Any(x => x.Text == r.Text)) merged.Add(r);
            return merged;
        }

        private static List<QRCodeResult> MergeTwo(List<QRCodeResult> a, List<QRCodeResult> b)
        {
            foreach (var r in b)
                if (!a.Any(x => x.Text == r.Text)) a.Add(r);
            return a;
        }

        private static Mat ApplyClahe(Mat gray, double clip)
        {
            var dst = new Mat();
            Cv2.CreateCLAHE(clip, new Size(8, 8)).Apply(gray, dst);
            return dst;
        }

        private static Mat ApplyAdaptiveThreshold(Mat gray)
        {
            var dst = new Mat();
            Cv2.AdaptiveThreshold(gray, dst, 255,
                AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, 31, 10);
            return dst;
        }

        private static List<QRCodeResult> Numbered(List<QRCodeResult> list)
        {
            for (int i = 0; i < list.Count; i++) list[i].Id = i + 1;
            return list;
        }

        private static Point[] BuildPolygon(
            ZXing.ResultPoint[]? rp,
            int offX, int offY, double inv, int maxW, int maxH)
        {
            if (rp == null || rp.Length == 0) return Array.Empty<Point>();
            if (rp.Length >= 3)
            {
                var bl = ToPoint(rp[0], offX, offY, inv, maxW, maxH);
                var tl = ToPoint(rp[1], offX, offY, inv, maxW, maxH);
                var tr = ToPoint(rp[2], offX, offY, inv, maxW, maxH);
                var br = new Point(Clamp(tr.X + bl.X - tl.X, 0, maxW),
                                   Clamp(tr.Y + bl.Y - tl.Y, 0, maxH));
                return new[] { tl, tr, br, bl };
            }
            if (rp.Length == 2)
            {
                var p0  = ToPoint(rp[0], offX, offY, inv, maxW, maxH);
                var p1  = ToPoint(rp[1], offX, offY, inv, maxW, maxH);
                int pad = Math.Max(14, (int)(Math.Abs(p1.Y - p0.Y) * 0.5 + 14));
                return new[]
                {
                    new Point(Clamp(p0.X, 0, maxW), Clamp(p0.Y - pad, 0, maxH)),
                    new Point(Clamp(p1.X, 0, maxW), Clamp(p1.Y - pad, 0, maxH)),
                    new Point(Clamp(p1.X, 0, maxW), Clamp(p1.Y + pad, 0, maxH)),
                    new Point(Clamp(p0.X, 0, maxW), Clamp(p0.Y + pad, 0, maxH)),
                };
            }
            return rp.Select(p => ToPoint(p, offX, offY, inv, maxW, maxH)).ToArray();
        }

        private static Point ToPoint(
            ZXing.ResultPoint rp, int offX, int offY, double inv, int maxW, int maxH)
            => new Point(
                Clamp((int)Math.Round((rp.X + offX) * inv), 0, maxW),
                Clamp((int)Math.Round((rp.Y + offY) * inv), 0, maxH));

        private static int Clamp(int v, int lo, int hi)
            => v < lo ? lo : v > hi ? hi : v;
    }
}