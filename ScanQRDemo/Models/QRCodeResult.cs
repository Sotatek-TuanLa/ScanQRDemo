using System;

namespace ScanQRDemo.Models
{
    public class QRCodeResult
    {
        public string Text   { get; set; } = string.Empty;
        /// <summary>Barcode format, e.g. "QR_CODE", "CODE_128", "EAN_13".</summary>
        public string Format { get; set; } = string.Empty;
        public OpenCvSharp.Point[]? Points { get; set; }
        public DateTime LastSeen { get; set; }
        public int Id { get; set; }

        // Computed helpers for UI binding
        public string SkuId  => $"SKU-{Id:D3}";
        public string Status => "Success";
    }
}
