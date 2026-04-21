using OpenCvSharp;
using ScanQRDemo.Models;
using System;
using System.Collections.Generic;

namespace ScanQRDemo.Services
{
    public interface ICameraService : IDisposable
    {
        /// <summary>Fired on background thread each time a new frame is captured.</summary>
        event Action<Mat> OnFrame;

        /// <summary>True while the capture loop is active.</summary>
        bool IsRunning { get; }

        /// <summary>Lists available video input devices.</summary>
        List<CameraDevice> GetLocalCameras();

        /// <summary>Starts the capture loop on the given camera index (0 = first USB cam).</summary>
        void Start(int cameraIndex = 0);

        /// <summary>Stops the capture loop and releases the device.</summary>
        void Stop();

        /// <summary>Returns a snapshot of the most-recent frame, or null if not running.</summary>
        Mat? CaptureFrame();
    }
}
