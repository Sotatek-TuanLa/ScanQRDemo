using MVSDK_Net;
using OpenCvSharp;
using ScanQRDemo.Models;
using System.Runtime.InteropServices;

namespace ScanQRDemo.Services
{
    public class CameraService : ICameraService
    {
        private MyCamera? _capture;
        private CancellationTokenSource? _cts;
        private Mat? _lastFrame;
        private readonly object _frameLock = new();
        private List<string> _deviceKeys = new();

        public event Action<Mat>? OnFrame;
        public bool IsRunning { get; private set; }

        public List<CameraDevice> GetLocalCameras()
        {
            var cameras = new List<CameraDevice>();
            _deviceKeys.Clear();

            IMVDefine.IMV_DeviceList deviceList = new IMVDefine.IMV_DeviceList();
            // 0 = interfaceTypeAll (GigE + USB3 + CL + PCIe)
            int ret = MyCamera.IMV_EnumDevices(ref deviceList, (uint)IMVDefine.IMV_EInterfaceType.interfaceTypeAll);

            if (ret != IMVDefine.IMV_OK)
                throw new InvalidOperationException($"IMV_EnumDevices failed. SDK error code: {ret}");

            for (int i = 0; i < deviceList.nDevNum; i++)
            {
                IntPtr ptr = new IntPtr(deviceList.pDevInfo.ToInt64() + i * Marshal.SizeOf(typeof(IMVDefine.IMV_DeviceInfo)));
                var deviceInfo = Marshal.PtrToStructure<IMVDefine.IMV_DeviceInfo>(ptr);

                string name = deviceInfo.cameraName;
                if (string.IsNullOrEmpty(name)) name = deviceInfo.modelName;
                if (string.IsNullOrEmpty(name)) name = $"Camera {i}";

                _deviceKeys.Add(deviceInfo.cameraKey);
                cameras.Add(new CameraDevice { Index = i, Name = name });
            }
            return cameras;
        }

        public void Start(int cameraIndex = 0)
        {
            if (IsRunning) return;

            if (cameraIndex < 0 || cameraIndex >= _deviceKeys.Count)
                throw new InvalidOperationException($"Invalid camera index {cameraIndex}.");

            _capture = new MyCamera();
            int ret = _capture.IMV_CreateHandle(IMVDefine.IMV_ECreateHandleMode.modeByIndex, cameraIndex);
            if (ret != IMVDefine.IMV_OK)
                throw new InvalidOperationException($"Cannot create handle. Code: {ret}");

            ret = _capture.IMV_Open();
            if (ret != IMVDefine.IMV_OK)
            {
                _capture.IMV_DestroyHandle();
                throw new InvalidOperationException($"Cannot open camera. Code: {ret}");
            }

            ret = _capture.IMV_StartGrabbing();
            if (ret != IMVDefine.IMV_OK)
            {
                _capture.IMV_Close();
                _capture.IMV_DestroyHandle();
                throw new InvalidOperationException($"Cannot start grabbing. Code: {ret}");
            }

            IsRunning = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    IMVDefine.IMV_Frame frame = new IMVDefine.IMV_Frame();
                    ret = _capture.IMV_GetFrame(ref frame, 500);
                    
                    if (ret == IMVDefine.IMV_OK && frame.pData != IntPtr.Zero)
                    {
                        try
                        {
                            var info = frame.frameInfo;
                            int channels = (int)(info.size / (info.width * info.height));
                            MatType matType = channels == 3 ? MatType.CV_8UC3 : MatType.CV_8UC1;

                            var mat = Mat.FromPixelData((int)info.height, (int)info.width, matType, frame.pData);
                            
                            // If it's a monochrome image but the system expects BGR, OpenCV can handle it or we convert:
                            Mat finalMat = mat.Clone(); // Clone immediately unbinds from unmanaged memory
                            
                            lock (_frameLock)
                            {
                                _lastFrame?.Dispose();
                                _lastFrame = finalMat;
                            }
                            OnFrame?.Invoke(finalMat);
                            mat.Dispose();
                        }
                        catch { }
                        finally
                        {
                            _capture.IMV_ReleaseFrame(ref frame);
                        }
                    }
                    else
                    {
                        Thread.Sleep(5);
                    }
                }
            }, token);
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            _cts?.Cancel();
            
            if (_capture != null)
            {
                _capture.IMV_StopGrabbing();
                _capture.IMV_Close();
                _capture.IMV_DestroyHandle();
                _capture = null;
            }
        }

        public Mat? CaptureFrame()
        {
            lock (_frameLock)
            {
                return _lastFrame?.Clone();
            }
        }

        public void Dispose()
        {
            Stop();
            lock (_frameLock)
            {
                _lastFrame?.Dispose();
                _lastFrame = null;
            }
        }
    }
}
