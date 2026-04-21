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
        List<QRCodeResult> Detect(Mat frame);
    }
}
