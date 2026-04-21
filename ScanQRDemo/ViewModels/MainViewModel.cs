using OpenCvSharp;
using ScanQRDemo.Helpers;
using ScanQRDemo.Models;
using ScanQRDemo.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScanQRDemo.ViewModels
{
    public class MainViewModel : BaseViewModel
    {
        // ── services ─────────────────────────────────────────────────────────
        private readonly ICameraService _camera;
        private readonly IQRCodeService _qr;
        private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;

        // ── backing fields ────────────────────────────────────────────────────
        private ImageSource? _cameraImage;
        private bool _isRunning;
        private string _statusText = "Disconnected";
        private SolidColorBrush _statusBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        private int _detectedCount;
        private int _cameraIndex;
        private CameraDevice? _selectedCamera;

        // ── frame throttle ────────────────────────────────────────────────────
        private int _isProcessingFrame = 0;
        private int _isScanningQr = 0;
        private DateTime _lastDisplay = DateTime.MinValue;
        private DateTime _lastQrScan  = DateTime.MinValue;
        private static readonly TimeSpan DisplayInterval = TimeSpan.FromMilliseconds(33);  // ~30 fps
        private static readonly TimeSpan QrScanInterval  = TimeSpan.FromMilliseconds(250); // 4x/sec

        // State for drawing latest known QRs on display frames
        private readonly object _resultsLock = new object();
        private List<QRCodeResult> _lastKnownResults = new List<QRCodeResult>();

        // ── public properties ─────────────────────────────────────────────────
        public ObservableCollection<CameraDevice> AvailableCameras { get; } = new();

        public CameraDevice? SelectedCamera
        {
            get => _selectedCamera;
            set
            {
                _selectedCamera = value;
                OnPropertyChanged();
                if (value != null)
                {
                    CameraIndex = value.Index;
                }
            }
        }

        public ImageSource? CameraImage
        {
            get => _cameraImage;
            private set { _cameraImage = value; OnPropertyChanged(); }
        }

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                _isRunning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNotRunning));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsNotRunning => !_isRunning;

        public string StatusText
        {
            get => _statusText;
            private set { _statusText = value; OnPropertyChanged(); }
        }

        public SolidColorBrush StatusBrush
        {
            get => _statusBrush;
            private set { _statusBrush = value; OnPropertyChanged(); }
        }

        public int DetectedCount
        {
            get => _detectedCount;
            private set { _detectedCount = value; OnPropertyChanged(); }
        }

        public int CameraIndex
        {
            get => _cameraIndex;
            set { _cameraIndex = value; OnPropertyChanged(); }
        }

        /// <summary>Decoded QR results shown in the sidebar.</summary>
        public ObservableCollection<QRCodeResult> QRResults { get; } = new();

        // ── commands ──────────────────────────────────────────────────────────
        public ICommand ConnectCommand { get; }
        public ICommand DisconnectCommand { get; }
        public ICommand CaptureCommand { get; }
        public ICommand ClearResultsCommand { get; }

        // ── constructor ───────────────────────────────────────────────────────
        public MainViewModel(ICameraService cameraService, IQRCodeService qrService)
        {
            _camera = cameraService;
            _qr = qrService;

            ConnectCommand = new RelayCommand(Connect, () => !IsRunning);
            DisconnectCommand = new RelayCommand(Disconnect, () => IsRunning);
            CaptureCommand = new RelayCommand(Capture, () => IsRunning);
            ClearResultsCommand = new RelayCommand(ClearResults);

            _camera.OnFrame += OnFrameReceived;
            LoadCameras();
        }

        public void LoadCameras()
        {
            AvailableCameras.Clear();
            try
            {
                var cameras = _camera.GetLocalCameras();
                foreach (var c in cameras) AvailableCameras.Add(c);

                if (AvailableCameras.Count > 0)
                    SelectedCamera = AvailableCameras[0];
                else
                    SetStatus("No cameras found", 0xF9, 0x73, 0x16);
            }
            catch (Exception ex)
            {
                SetStatus("Enum failed", 0xEF, 0x44, 0x44);
                MessageBox.Show($"Camera enumeration failed:\n{ex.Message}",
                    "SDK Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── command handlers ──────────────────────────────────────────────────
        private void Connect()
        {
            try
            {
                _camera.Start(CameraIndex);
                IsRunning = true;
                SetStatus("Live", 0x22, 0xC5, 0x5E);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open camera {CameraIndex}:\n{ex.Message}",
                    "Camera Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Error", 0xEF, 0x44, 0x44);
            }
        }

        private void Disconnect()
        {
            _camera.Stop();
            IsRunning = false;
            CameraImage = null;
            SetStatus("Disconnected", 0xEF, 0x44, 0x44);

            lock (_resultsLock)
            {
                _lastKnownResults.Clear();
            }
        }

        private void Capture()
        {
            using var frame = _camera.CaptureFrame();
            if (frame == null || frame.Empty()) return;

            // Manual capture bypasses the throttle
            var results = _qr.Detect(frame);
            ProcessQrResults(results);

            using var annotated = frame.Clone();
            DrawAnnotations(annotated, results);
            CameraImage = BitmapConverter.ToBitmapSource(annotated);
        }

        private void ClearResults()
        {
            QRResults.Clear();
            DetectedCount = 0;
            lock (_resultsLock)
            {
                _lastKnownResults.Clear();
            }
        }

        // ── frame pipeline ────────────────────────────────────────────────────
        private void OnFrameReceived(Mat frame)
        {
            var now = DateTime.UtcNow;

            // 1. DISPLAY PATH: fast, updates UI at ~30 fps
            //    Only enter if NOT already processing AND enough time has elapsed.
            //    We update _lastDisplay INSIDE the guard so only one caller wins.
            if (now - _lastDisplay >= DisplayInterval &&
                Interlocked.Exchange(ref _isProcessingFrame, 1) == 0)
            {
                _lastDisplay = now;          // stamp here – inside the guard
                DisplayFrame(frame);         // _isProcessingFrame reset inside BeginInvoke
            }

            // 2. QR SCAN PATH: slow, runs async roughly 4 times per second
            //    Same pattern: stamp inside the interlocked guard.
            if (now - _lastQrScan >= QrScanInterval &&
                Interlocked.Exchange(ref _isScanningQr, 1) == 0)
            {
                _lastQrScan = now;           // stamp here – inside the guard
                Mat frameForQr = frame.Clone();

                Task.Run(() =>
                {
                    try
                    {
                        ScanQrAsync(frameForQr);
                    }
                    finally
                    {
                        frameForQr.Dispose();
                        Volatile.Write(ref _isScanningQr, 0);
                    }
                });
            }
        }

        private void DisplayFrame(Mat frame)
        {
            // We annotate directly onto a copy for display
            using var displayMat = frame.Clone();

            List<QRCodeResult> currentAnnotations;
            lock (_resultsLock)
            {
                currentAnnotations = new List<QRCodeResult>(_lastKnownResults);
            }

            DrawAnnotations(displayMat, currentAnnotations);

            var bmp = BitmapConverter.ToBitmapSource(displayMat);

            if (bmp != null)
            {
                // Reset the processing flag INSIDE BeginInvoke so that the next
                // display slot is only opened AFTER the bitmap is committed to
                // CameraImage. This prevents queuing an identical frame before
                // the UI thread has consumed the current one.
                _dispatcher.BeginInvoke(() =>
                {
                    CameraImage = bmp;
                    Volatile.Write(ref _isProcessingFrame, 0);  // ← key fix
                });
            }
            else
            {
                // bmp was null (conversion failed) – release the slot anyway
                Volatile.Write(ref _isProcessingFrame, 0);
            }
        }

        private void ScanQrAsync(Mat frame)
        {
            // Expensive CV/ZXing run on ThreadPool
            var results = _qr.Detect(frame);
            
            // Remove overly old known results
            var now = DateTime.Now;
            var validResults = new List<QRCodeResult>();
            foreach (var res in results) validResults.Add(res);

            lock (_resultsLock)
            {
                foreach (var old in _lastKnownResults)
                {
                    if (now - old.LastSeen < TimeSpan.FromSeconds(1) && !validResults.Exists(x => x.Text == old.Text))
                    {
                        validResults.Add(old);
                    }
                }
                _lastKnownResults = validResults;
            }

            if (results.Count > 0)
            {
                _dispatcher.BeginInvoke(() => ProcessQrResults(results));
            }
        }

        private void ProcessQrResults(List<QRCodeResult> results)
        {
            foreach (var r in results)
            {
                // Only add if not already in the list (deduplicate by text)
                bool exists = false;
                foreach (var existing in QRResults)
                {
                    if (existing.Text == r.Text) { exists = true; break; }
                }
                if (!exists)
                {
                    QRResults.Insert(0, r);
                    DetectedCount = QRResults.Count;
                }
            }
        }

        // ── annotation helpers ────────────────────────────────────────────────
        private static void DrawAnnotations(Mat img, List<QRCodeResult> results)
        {
            foreach (var qr in results)
            {
                if (qr.Points == null || qr.Points.Length < 4) continue;

                // Draw bounding polygon
                var pts = Array.ConvertAll(qr.Points, p => p);
                for (int i = 0; i < pts.Length; i++)
                {
                    Cv2.Line(img, pts[i], pts[(i + 1) % pts.Length],
                        new Scalar(0, 220, 90), 2);
                }

                // Label
                var origin = new OpenCvSharp.Point(
                    pts[0].X, Math.Max(0, pts[0].Y - 8));
                Cv2.PutText(img, $"#{qr.Id}: {TruncateText(qr.Text, 24)}",
                    origin, HersheyFonts.HersheySimplex, 0.55, new Scalar(0, 220, 90), 2);
            }
        }

        private static string TruncateText(string s, int max)
            => s.Length <= max ? s : s[..max] + "…";

        // ── status helpers ────────────────────────────────────────────────────
        private void SetStatus(string text, byte r, byte g, byte b)
        {
            StatusText = text;
            StatusBrush = new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        // ── cleanup ───────────────────────────────────────────────────────────
        public void Cleanup()
        {
            _camera.OnFrame -= OnFrameReceived;
            _camera.Dispose();
        }
    }
}
