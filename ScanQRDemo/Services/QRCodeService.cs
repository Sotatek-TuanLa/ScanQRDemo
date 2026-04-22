using OpenCvSharp;
using ScanQRDemo.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ZXing.Windows.Compatibility;

namespace ScanQRDemo.Services
{
    /// <summary>
    /// QR / barcode detection pipeline — supports multiple codes per frame/image.
    ///
    /// PASS 1 — Parallel flat scan (wall time = max of all tasks):
    ///   A  OpenCV DetectAndDecodeMulti   — native C++, finds ALL QR codes in one call
    ///   B  ZXing DecodeMultiple (full CLAHE image)
    ///   C–F  ZXing DecodeMultiple on each quadrant tile (catches codes missed by full scan)
    ///
    ///   All results are MERGED — no early exit on partial hits.
    ///
    /// PASS 2 — Adaptive threshold + ZXing full DecodeMultiple
    ///   Supplements Pass 1 results; adds any codes not yet found.
    ///
    /// PASS 3 — 3×3 overlapping tile grid + ZXing QR reader  [LIVE + STILL]
    ///   Runs for EVERY frame (live camera and uploaded images).
    ///   Each tile overlaps 20 % with its neighbours so a code straddling
    ///   a tile boundary is fully contained in at least one tile.
    ///   Uses QrOnlyFormats — no false-positive linear barcode reads.
    ///
    /// PASS 3b — 4×4 finer grid [STILL IMAGE ONLY]
    ///   Extra pass for dense / high-resolution uploaded images.
    ///
    /// PASS 4 — Strong CLAHE + hard AllFormats reader on full image [STILL ONLY]
    ///
    /// PASS 5 — Full-resolution scan (upload only, when downscaled)
    /// </summary>
    public class QRCodeService : IQRCodeService
    {
        // ── Format lists ──────────────────────────────────────────────────────
        // QR / 2D only — used by still-image TryHarder passes where we want
        // maximum sensitivity for 2-D codes without linear-barcode false positives.
        private static readonly List<ZXing.BarcodeFormat> QrOnlyFormats = new()
        {
            ZXing.BarcodeFormat.QR_CODE,
            ZXing.BarcodeFormat.DATA_MATRIX,
            ZXing.BarcodeFormat.AZTEC,
            ZXing.BarcodeFormat.PDF_417,
        };

        // Full set — used for live camera AND uploads to catch every barcode type
        // (QR codes, CODE-128, EAN-13, etc.).
        private static readonly List<ZXing.BarcodeFormat> AllFormats = new()
        {
            ZXing.BarcodeFormat.QR_CODE,
            ZXing.BarcodeFormat.DATA_MATRIX,
            ZXing.BarcodeFormat.PDF_417,
            ZXing.BarcodeFormat.AZTEC,
            ZXing.BarcodeFormat.CODE_128,
            ZXing.BarcodeFormat.CODE_39,
            ZXing.BarcodeFormat.CODE_93,
            ZXing.BarcodeFormat.EAN_13,
            ZXing.BarcodeFormat.EAN_8,
            ZXing.BarcodeFormat.UPC_A,
            ZXing.BarcodeFormat.UPC_E,
            ZXing.BarcodeFormat.ITF,
        };

        // Hard reader — TryHarder=true, AllFormats. Expensive; used for still-image
        // deep passes only. Kept as a field so the costly internal init runs once.
        private readonly BarcodeReader _hardReader = new BarcodeReader
        {
            AutoRotate = false,
            Options    = new ZXing.Common.DecodingOptions
            {
                TryHarder       = true,
                TryInverted     = true,
                PossibleFormats = AllFormats
            }
        };

        // OpenCV native C++ multi-QR detector.
        private readonly QRCodeDetector _cvDetector = new QRCodeDetector();

        private const double DarkThreshold = 80.0;

        // ──────────────────────────────────────────────────────────────────────
        public List<QRCodeResult> Detect(Mat frame, int maxWidth = 800, bool isStillImage = false)
        {
            if (frame == null || frame.Empty())
                return new List<QRCodeResult>();

            // ── 1. Downscale to working resolution ────────────────────────────
            double scale = 1.0;
            Mat working;
            if (frame.Width > maxWidth)
            {
                scale   = (double)maxWidth / frame.Width;
                working = new Mat();
                Cv2.Resize(frame, working,
                    new Size(maxWidth, (int)(frame.Height * scale)),
                    interpolation: InterpolationFlags.Area);
            }
            else
            {
                working = frame.Clone();
            }

            double invScale = scale > 0 ? 1.0 / scale : 1.0;

            using (working)
            {
                // ── 2. Grayscale ──────────────────────────────────────────────
                using var gray = new Mat();
                if (working.Channels() == 1) working.CopyTo(gray);
                else Cv2.CvtColor(working, gray, ColorConversionCodes.BGR2GRAY);

                bool isDark = Cv2.Mean(gray).Val0 < DarkThreshold;

                // ── PASS 1: parallel flat scan on CLAHE image ─────────────────
                using var clahe = ApplyClahe(gray, isDark ? 3.5 : 2.0);

                // 4 quadrant tiles — each pre-cloned (contiguous) for thread safety.
                int halfW = clahe.Width  / 2;
                int halfH = clahe.Height / 2;

                var tileRects = new Rect[]
                {
                    new Rect(0,     0,     halfW, halfH),   // top-left
                    new Rect(halfW, 0,     halfW, halfH),   // top-right
                    new Rect(0,     halfH, halfW, halfH),   // bottom-left
                    new Rect(halfW, halfH, halfW, halfH),   // bottom-right
                };
                var tileOffX = new[] { 0, halfW, 0,     halfW };
                var tileOffY = new[] { 0, 0,     halfH, halfH };

                var tiles = tileRects.Select(r => clahe[r].Clone()).ToArray();

                // pass1[0] = OpenCV multi, pass1[1] = ZXing full, pass1[2-5] = ZXing tiles
                var pass1 = new List<QRCodeResult>[6];

                try
                {
                    Parallel.Invoke(
                        // A — OpenCV C++ DetectAndDecodeMulti — finds ALL QR codes natively
                        () => pass1[0] = ScanOpenCvMulti(_cvDetector, clahe, invScale, frame.Width, frame.Height),

                        // B — ZXing live reader on full CLAHE — AllFormats: detects QR, CODE-128, EAN, etc.
                        () => pass1[1] = ScanFull(clahe, MakeLiveReader(), invScale, frame.Width, frame.Height),

                        // C-F — ZXing live reader on each quadrant tile (AllFormats, TryHarder=false for speed)
                        () => pass1[2] = ScanTile(tiles[0], MakeLiveReader(), tileOffX[0], tileOffY[0], invScale, frame.Width, frame.Height),
                        () => pass1[3] = ScanTile(tiles[1], MakeLiveReader(), tileOffX[1], tileOffY[1], invScale, frame.Width, frame.Height),
                        () => pass1[4] = ScanTile(tiles[2], MakeLiveReader(), tileOffX[2], tileOffY[2], invScale, frame.Width, frame.Height),
                        () => pass1[5] = ScanTile(tiles[3], MakeLiveReader(), tileOffX[3], tileOffY[3], invScale, frame.Width, frame.Height)
                    );
                }
                finally
                {
                    foreach (var t in tiles) t?.Dispose();
                }

                // Merge ALL pass-1 results — no early exit on partial hits.
                var merged = Merge(pass1);

                // ── PASS 2: Adaptive threshold + ZXing live reader ───────────────
                using var adaptive = ApplyAdaptiveThreshold(gray);
                var r2 = ScanFull(adaptive, MakeLiveReader(), invScale, frame.Width, frame.Height);
                merged = MergeTwo(merged, r2);

                // ── PASS 3: 3×3 overlapping tile grid + ZXing live reader ──────────
                // Runs for BOTH live camera and still images.
                // The 4-quadrant split in Pass 1 cuts any code sitting on the centre
                // cross into 4 pieces; this overlapping grid ensures every code is
                // fully contained in at least one tile regardless of its position.
                // Uses AllFormats so linear barcodes (CODE-128, EAN, etc.) are caught.
                using var claheForGrid = ApplyClahe(gray, isDark ? 3.5 : 2.0);
                var r3 = ScanOverlappingGridQr(claheForGrid, 3, 3, 0.20, invScale, frame.Width, frame.Height);
                merged = MergeTwo(merged, r3);

                // ── Live camera: stop here — fast + accurate ─────────────────────
                // Passes 3b-5 use AllFormats / full-resolution which are too slow
                // for per-frame scanning and can produce linear-barcode false positives.
                if (!isStillImage)
                    return Numbered(merged);

                // ── PASS 3b: 4×4 finer grid [STILL ONLY] ────────────────────────
                var r3b = ScanOverlappingGrid(claheForGrid, 4, 4, 0.20, invScale, frame.Width, frame.Height);
                merged = MergeTwo(merged, r3b);

                // ── PASS 4: Strong CLAHE + hard AllFormats reader [STILL ONLY] ───
                using var claheHard = ApplyClahe(gray, isDark ? 5.0 : 3.5);
                var r4 = ScanFull(claheHard, _hardReader, invScale, frame.Width, frame.Height);
                merged = MergeTwo(merged, r4);

                // ── PASS 5: Full-resolution scan (always when downscaled) ─────────
                // Run unconditionally — even if Pass 1-4 found some codes, there may
                // still be codes that were lost in the downscale and need full-res.
                if (scale < 1.0)
                {
                    var r5 = DetectFullRes(frame);
                    merged = MergeTwo(merged, r5);
                }

                return Numbered(merged);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Last-resort: run the hard reader on the original full-resolution frame.
        // ──────────────────────────────────────────────────────────────────────
        private List<QRCodeResult> DetectFullRes(Mat original)
        {
            var results = new List<QRCodeResult>();
            try
            {
                using var gray = new Mat();
                if (original.Channels() == 1) original.CopyTo(gray);
                else Cv2.CvtColor(original, gray, ColorConversionCodes.BGR2GRAY);

                bool isDark = Cv2.Mean(gray).Val0 < DarkThreshold;

                // (a) CLAHE on full-res
                using var clahe = ApplyClahe(gray, isDark ? 4.0 : 2.5);
                var ra = ScanFull(clahe, _hardReader, 1.0, original.Width, original.Height);
                results = MergeTwo(results, ra);

                // (b) Overlapping grid on full-res
                var rb = ScanOverlappingGrid(clahe, 3, 3, 0.20, 1.0, original.Width, original.Height);
                results = MergeTwo(results, rb);

                // (c) Adaptive threshold on full-res
                using var adaptive = ApplyAdaptiveThreshold(gray);
                var rc = ScanFull(adaptive, _hardReader, 1.0, original.Width, original.Height);
                results = MergeTwo(results, rc);
            }
            catch { }
            return results;
        }

        // ── A: OpenCV native multi-QR detector ───────────────────────────────
        // OpenCvSharp 4.13 uses a two-step API:
        //   DetectMulti  → finds all QR corner sets (flat Point2f[], 4 pts per code)
        //   DecodeMulti  → decodes the detected corners into text strings
        private static List<QRCodeResult> ScanOpenCvMulti(
            QRCodeDetector det, Mat img,
            double invScale, int origW, int origH)
        {
            var results = new List<QRCodeResult>();
            try
            {
                // Step 1: detect all QR corner sets
                bool found = det.DetectMulti(img, out Point2f[] points);
                if (!found || points == null || points.Length < 4) return results;

                // Step 2: decode all detected QR codes
                // The API returns string?[] — individual entries may be null if a
                // code was detected but could not be decoded.
                bool decoded = det.DecodeMulti(img, points, out string?[] decodedInfo);
                if (!decoded || decodedInfo == null) return results;

                // points is flat: code i occupies [i*4 .. i*4+3]
                for (int i = 0; i < decodedInfo.Length; i++)
                {
                    if (string.IsNullOrEmpty(decodedInfo[i])) continue;

                    Point[]? polygon = null;
                    int baseIdx = i * 4;
                    if (baseIdx + 3 < points.Length)
                    {
                        polygon = new Point[4];
                        for (int c = 0; c < 4; c++)
                            polygon[c] = new Point(
                                Clamp((int)Math.Round(points[baseIdx + c].X * invScale), 0, origW),
                                Clamp((int)Math.Round(points[baseIdx + c].Y * invScale), 0, origH));
                    }

                    results.Add(new QRCodeResult
                    {
                        Text     = decodedInfo[i]!,   // guarded by IsNullOrEmpty check above
                        Format   = "QR_CODE",
                        Points   = polygon,
                        LastSeen = DateTime.Now
                    });
                }
            }
            catch { /* native failure – ZXing paths cover */ }
            return results;
        }

        // ── B / Pass 2 / Pass 4: ZXing DecodeMultiple on full image ──────────
        private static List<QRCodeResult> ScanFull(
            Mat img, BarcodeReader reader,
            double invScale, int origW, int origH)
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
                        Text     = r.Text,
                        Format   = r.BarcodeFormat.ToString(),
                        Points   = BuildPolygon(r.ResultPoints, 0, 0, invScale, origW, origH),
                        LastSeen = DateTime.Now
                    });
                }
            }
            catch { }
            return results;
        }

        // ── C-F: ZXing DecodeMultiple on a pre-cloned tile ───────────────────
        // Now uses DecodeMultiple (was Decode) so a quadrant containing 2 codes
        // returns both results instead of only the first one found.
        private static List<QRCodeResult> ScanTile(
            Mat tile, BarcodeReader reader,
            int offX, int offY,
            double invScale, int origW, int origH)
        {
            var results = new List<QRCodeResult>();
            try
            {
                using var bmp = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(tile);
                var raw = reader.DecodeMultiple(bmp);
                if (raw == null) return results;

                foreach (var r in raw)
                {
                    if (r == null || string.IsNullOrEmpty(r.Text)) continue;
                    results.Add(new QRCodeResult
                    {
                        Text     = r.Text,
                        Format   = r.BarcodeFormat.ToString(),
                        Points   = BuildPolygon(r.ResultPoints, offX, offY, invScale, origW, origH),
                        LastSeen = DateTime.Now
                    });
                }
            }
            catch { }
            return results;
        }

        // ── Pass 3 (live + still): overlapping grid with live reader (AllFormats) ─
        /// <summary>
        /// Overlapping grid scan using <see cref="MakeLiveReader"/> (AllFormats, fast).
        /// Detects QR codes AND linear barcodes (CODE-128, EAN, etc.) on live camera.
        /// </summary>
        private List<QRCodeResult> ScanOverlappingGridQr(
            Mat img, int cols, int rows, double overlap,
            double invScale, int origW, int origH)
            => ScanOverlappingGridCore(img, cols, rows, overlap, invScale, origW, origH,
                                       useHardReader: false);

        // ── Pass 3b+ (still only): overlapping grid with hard/AllFormats reader ─
        /// <summary>
        /// Overlapping grid scan using <see cref="MakeHardReader"/> (AllFormats).
        /// Used only for still images where false positives are acceptable.
        /// </summary>
        private List<QRCodeResult> ScanOverlappingGrid(
            Mat img, int cols, int rows, double overlap,
            double invScale, int origW, int origH)
            => ScanOverlappingGridCore(img, cols, rows, overlap, invScale, origW, origH,
                                       useHardReader: true);

        private List<QRCodeResult> ScanOverlappingGridCore(
            Mat img, int cols, int rows, double overlap,
            double invScale, int origW, int origH, bool useHardReader)
        {
            var all = new List<QRCodeResult>();

            int tileW = img.Width  / cols;
            int tileH = img.Height / rows;
            int padW  = (int)(tileW * overlap);
            int padH  = (int)(tileH * overlap);

            var tasks = new List<(Mat tile, int ox, int oy)>();

            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    int x  = col * tileW;
                    int y  = row * tileH;
                    int x0 = Math.Max(0, x - padW);
                    int y0 = Math.Max(0, y - padH);
                    int x1 = Math.Min(img.Width,  x + tileW + padW);
                    int y1 = Math.Min(img.Height, y + tileH + padH);

                    var rect = new Rect(x0, y0, x1 - x0, y1 - y0);
                    if (rect.Width <= 0 || rect.Height <= 0) continue;

                    tasks.Add((img[rect].Clone(), x0, y0));
                }
            }

            try
            {
                var taskResults = new List<QRCodeResult>[tasks.Count];
                Parallel.For(0, tasks.Count, i =>
                {
                    var (tile, ox, oy) = tasks[i];
                    var reader = useHardReader ? MakeHardReader() : MakeLiveReader();
                    taskResults[i] = ScanTile(tile, reader, ox, oy, invScale, origW, origH);
                });
                all = Merge(taskResults);
            }
            finally
            {
                foreach (var (tile, _, _) in tasks) tile?.Dispose();
            }

            return all;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static List<QRCodeResult> Merge(IEnumerable<List<QRCodeResult>?> lists)
        {
            var merged = new List<QRCodeResult>();
            foreach (var r in lists.Where(l => l != null).SelectMany(l => l!))
            {
                if (!merged.Any(x => x.Text == r.Text))
                    merged.Add(r);
            }
            return merged;
        }

        private static List<QRCodeResult> MergeTwo(List<QRCodeResult> existing, List<QRCodeResult> incoming)
        {
            foreach (var r in incoming)
            {
                if (!existing.Any(x => x.Text == r.Text))
                    existing.Add(r);
            }
            return existing;
        }

        private static Mat ApplyClahe(Mat gray, double clip)
        {
            var c   = Cv2.CreateCLAHE(clip, new Size(8, 8));
            var dst = new Mat();
            c.Apply(gray, dst);
            return dst;
        }

        private static Mat ApplyAdaptiveThreshold(Mat gray)
        {
            var dst = new Mat();
            Cv2.AdaptiveThreshold(gray, dst, 255,
                AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, 31, 10);
            return dst;
        }

        // Live reader — AllFormats, TryHarder=false for speed on per-frame scanning.
        // Detects QR codes, DATA_MATRIX, CODE-128, EAN-13, UPC, ITF, etc.
        private static BarcodeReader MakeLiveReader() => new BarcodeReader
        {
            AutoRotate = false,
            Options    = new ZXing.Common.DecodingOptions
            {
                TryHarder       = false,
                TryInverted     = false,
                PossibleFormats = AllFormats
            }
        };

        // QR reader — TryHarder=true, 2D formats only.
        // Used by still-image TryHarder passes for maximum 2D-code sensitivity.
        private static BarcodeReader MakeQrReader() => new BarcodeReader
        {
            AutoRotate = false,
            Options    = new ZXing.Common.DecodingOptions
            {
                TryHarder       = true,
                TryInverted     = true,
                PossibleFormats = QrOnlyFormats
            }
        };

        // Hard reader — still-image deep passes (Pass 3b/4). AllFormats + TryHarder.
        private static BarcodeReader MakeHardReader() => new BarcodeReader
        {
            AutoRotate = false,
            Options    = new ZXing.Common.DecodingOptions
            {
                TryHarder       = true,
                TryInverted     = true,
                PossibleFormats = AllFormats
            }
        };

        private static List<QRCodeResult> Numbered(List<QRCodeResult> list)
        {
            for (int i = 0; i < list.Count; i++) list[i].Id = i + 1;
            return list;
        }

        /// <summary>
        /// Convert ZXing ResultPoints to an OpenCV polygon in original-frame coordinates.
        /// <paramref name="offX"/>/<paramref name="offY"/> translate tile-local points
        /// into full-downscaled-image space before back-scaling.
        /// </summary>
        private static Point[] BuildPolygon(
            ZXing.ResultPoint[]? rp,
            int offX, int offY,
            double inv, int maxW, int maxH)
        {
            if (rp == null || rp.Length == 0) return Array.Empty<Point>();

            if (rp.Length >= 3)
            {
                var bl = ToPoint(rp[0], offX, offY, inv, maxW, maxH);
                var tl = ToPoint(rp[1], offX, offY, inv, maxW, maxH);
                var tr = ToPoint(rp[2], offX, offY, inv, maxW, maxH);
                var br = new Point(
                    Clamp(tr.X + bl.X - tl.X, 0, maxW),
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