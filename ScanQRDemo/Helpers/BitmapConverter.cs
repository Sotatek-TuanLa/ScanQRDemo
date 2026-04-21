using OpenCvSharp;
using System;
using System.Windows.Media.Imaging;

namespace ScanQRDemo.Helpers
{
    /// <summary>
    /// Converts an OpenCV Mat to a frozen WPF BitmapSource using JPEG encoding
    /// (≈ 5–10× faster than PNG; quality 85 is visually lossless for live preview).
    /// </summary>
    public static class BitmapConverter
    {
        // JPEG encode parameters reused every call – avoids repeated array allocations
        private static readonly int[] JpegParams =
            new int[] { (int)ImwriteFlags.JpegQuality, 85 };

        public static BitmapSource? ToBitmapSource(Mat frame)
        {
            if (frame == null || frame.Empty())
                return null;

            try
            {
                Cv2.ImEncode(".jpg", frame, out byte[] buf, JpegParams);

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption    = BitmapCacheOption.OnLoad;
                bmp.StreamSource   = new System.IO.MemoryStream(buf);
                bmp.EndInit();
                bmp.Freeze();   // cross-thread safe
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        // ── back-compat shim so callers that still use ToBitmapImage compile ──
        [Obsolete("Use ToBitmapSource – JPEG path is much faster.")]
        public static BitmapImage? ToBitmapImage(Mat frame)
            => ToBitmapSource(frame) as BitmapImage;
    }
}
