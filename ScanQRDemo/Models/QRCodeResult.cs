using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ScanQRDemo.Models
{
    public class QRCodeResult
    {
        public string Text { get; set; } = string.Empty;
        public OpenCvSharp.Point[]? Points { get; set; }
        public DateTime LastSeen { get; set; }

        // tracking id
        public int Id { get; set; }
    }
}
