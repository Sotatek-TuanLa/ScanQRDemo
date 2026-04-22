using OpenCvSharp;
using ScanQRDemo.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ScanQRDemo.Services
{
    public interface IQRCodeService
    {
        /// <summary>
        /// Detect all QR codes and barcodes in <paramref name="frame"/>.
        /// <paramref name="maxWidth"/> caps the internal working resolution;
        /// use a larger value (e.g. 1920) for high-res still images.
        /// Set <paramref name="isStillImage"/> to true for uploaded photos to enable
        /// the aggressive overlapping-grid multi-pass scan (slower but more thorough).
        /// </summary>
        List<QRCodeResult> Detect(Mat frame, int maxWidth = 640, bool isStillImage = false);
    }
}
