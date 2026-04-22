using Microsoft.Win32;
using OpenCvSharp;
using ScanQRDemo.Helpers;
using ScanQRDemo.Models;
using ScanQRDemo.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
        private bool         _isRunning;
        private string       _statusText  = "Disconnected";
        private SolidColorBrush _statusBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        private int    _detectedCount;
        private int    _failedCount;
        private int    _cameraIndex;
        private string _lastUpdatedText = "--:--:--";
        private CameraDevice? _selectedCamera;

        // ── frame throttle ────────────────────────────────────────────────────
        private int _isProcessingFrame = 0;
        private int _isScanningQr      = 0;
        private DateTime _lastDisplay  = DateTime.MinValue;
        private DateTime _lastQrScan   = DateTime.MinValue;
        private static readonly TimeSpan DisplayInterval = TimeSpan.FromMilliseconds(33);   // ~30 fps
        private static readonly TimeSpan QrScanInterval  = TimeSpan.FromMilliseconds(100);  // ~10x/sec
        private const int QrCloneMaxWidth = 800;

        // ── annotation state ──────────────────────────────────────────────────
        private readonly object _resultsLock = new object();
        private List<QRCodeResult> _lastKnownResults = new List<QRCodeResult>();

        // ── public collections ────────────────────────────────────────────────
        public ObservableCollection<CameraDevice>  AvailableCameras { get; } = new();
        public ObservableCollection<QRCodeResult>  QRResults        { get; } = new();
        public ObservableCollection<ExceptionItem> ExceptionItems   { get; } = new();

        // ── public properties ─────────────────────────────────────────────────
        public CameraDevice? SelectedCamera
        {
            get => _selectedCamera;
            set
            {
                _selectedCamera = value;
                OnPropertyChanged();
                if (value != null) CameraIndex = value.Index;
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
            private set
            {
                _detectedCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TotalCount));
                OnPropertyChanged(nameof(AccuracyText));
            }
        }

        public int FailedCount
        {
            get => _failedCount;
            private set
            {
                _failedCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TotalCount));
                OnPropertyChanged(nameof(AccuracyText));
            }
        }

        public int    TotalCount    => DetectedCount + FailedCount;
        public string AccuracyText
        {
            get
            {
                int total = TotalCount;
                return total == 0 ? "—" : $"{(int)Math.Round((double)DetectedCount / total * 100)}%";
            }
        }

        public string LastUpdatedText
        {
            get => _lastUpdatedText;
            private set { _lastUpdatedText = value; OnPropertyChanged(); }
        }

        public int ExceptionCount => ExceptionItems.Count;

        public int CameraIndex
        {
            get => _cameraIndex;
            set { _cameraIndex = value; OnPropertyChanged(); }
        }

        // ── commands ──────────────────────────────────────────────────────────
        public ICommand ConnectCommand       { get; }
        public ICommand DisconnectCommand    { get; }
        public ICommand CaptureCommand       { get; }
        public ICommand ClearResultsCommand  { get; }
        public ICommand UploadImageCommand   { get; }

        // ── constructor ───────────────────────────────────────────────────────
        public MainViewModel(ICameraService cameraService, IQRCodeService qrService)
        {
            _camera = cameraService;
            _qr     = qrService;

            ConnectCommand      = new RelayCommand(Connect,      () => !IsRunning);
            DisconnectCommand   = new RelayCommand(Disconnect,   () =>  IsRunning);
            CaptureCommand      = new RelayCommand(Capture,      () =>  IsRunning);
            ClearResultsCommand = new RelayCommand(ClearResults);
            UploadImageCommand  = new RelayCommand(UploadImage);

            // Keep ExceptionCount in sync with the collection
            ExceptionItems.CollectionChanged += (_, __) => OnPropertyChanged(nameof(ExceptionCount));

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
                if (AvailableCameras.Count > 0) SelectedCamera = AvailableCameras[0];
                else SetStatus("No cameras found", 0xF9, 0x73, 0x16);
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
            lock (_resultsLock) { _lastKnownResults.Clear(); }
        }

        private void Capture()
        {
            using var frame = _camera.CaptureFrame();
            if (frame == null || frame.Empty()) return;

            var results = _qr.Detect(frame);
            ProcessQrResults(results);

            using var annotated = frame.Clone();
            DrawAnnotations(annotated, results);
            CameraImage = BitmapConverter.ToBitmapSource(annotated);
        }

        private void ClearResults()
        {
            QRResults.Clear();
            ExceptionItems.Clear();
            DetectedCount    = 0;
            FailedCount      = 0;
            LastUpdatedText  = "--:--:--";
            lock (_resultsLock) { _lastKnownResults.Clear(); }
        }

        private void UploadImage()
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Select an image to scan",
                Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.tiff;*.tif;*.webp|All files|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            string fileName = System.IO.Path.GetFileName(dlg.FileName);

            try
            {
                using var mat = Cv2.ImRead(dlg.FileName, ImreadModes.Color);
                if (mat == null || mat.Empty())
                {
                    ExceptionItems.Insert(0, new Models.ExceptionItem
                    {
                        Message = $"[Upload] Could not read image: {fileName}"
                    });
                    FailedCount++;
                    return;
                }

                // Use 1920-px working resolution for still images (vs 640 for live video).
                // Phone photos can be 3000-4000 px wide; at 640 px a code occupying
                // 10% of the image would be only ~64 px — too small to decode reliably.
                var results = _qr.Detect(mat, maxWidth: 1920, isStillImage: true);

                // Update the result list
                ProcessQrResults(results);

                // Draw annotations on a copy and show in the camera panel
                using var annotated = mat.Clone();
                DrawAnnotations(annotated, results);
                var bmp = BitmapConverter.ToBitmapSource(annotated);
                if (bmp != null && bmp.CanFreeze) bmp.Freeze();
                CameraImage = bmp;

                // Persist in overlay so live-view continues showing the last annotations
                lock (_resultsLock) { _lastKnownResults = new List<QRCodeResult>(results); }

                if (results.Count == 0)
                {
                    // No popup — add a silent entry to the Exception Queue instead
                    ExceptionItems.Insert(0, new Models.ExceptionItem
                    {
                        Message = $"[Upload] No QR/barcode detected: {fileName}"
                    });
                    FailedCount++;
                }
            }
            catch (Exception ex)
            {
                ExceptionItems.Insert(0, new Models.ExceptionItem
                {
                    Message = $"[Upload] Error processing {fileName}: {ex.Message}"
                });
                FailedCount++;
            }
        }

        // ── frame pipeline ────────────────────────────────────────────────────
        private void OnFrameReceived(Mat frame)
        {
            var now = DateTime.UtcNow;

            // 1. Display path: ~30 fps
            if (now - _lastDisplay >= DisplayInterval &&
                Interlocked.Exchange(ref _isProcessingFrame, 1) == 0)
            {
                _lastDisplay = now;
                DisplayFrame(frame);
            }

            // 2. QR scan path: ~10x per second
            if (now - _lastQrScan >= QrScanInterval &&
                Interlocked.Exchange(ref _isScanningQr, 1) == 0)
            {
                _lastQrScan = now;

                // Always clone the full original frame — do NOT pre-resize here.
                // Detect() handles internal downscaling and returns coordinates
                // in the original frame's coordinate space. If we pre-resize to
                // 800 px, coordinates come back in the 800-px space but
                // DisplayFrame() draws on the original-size frame → wrong positions.
                var frameForQr = frame.Clone();

                Task.Run(() =>
                {
                    try    { ScanQrAsync(frameForQr); }
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
            using var displayMat = frame.Clone();

            List<QRCodeResult> annotations;
            lock (_resultsLock)
            {
                annotations = new List<QRCodeResult>(_lastKnownResults);
            }

            DrawAnnotations(displayMat, annotations);

            var bmp = BitmapConverter.ToBitmapSource(displayMat);
            if (bmp != null)
            {
                if (bmp.CanFreeze) bmp.Freeze();
                Volatile.Write(ref _isProcessingFrame, 0);
                _dispatcher.BeginInvoke(DispatcherPriority.Render,
                    () => { CameraImage = bmp; });
            }
            else
            {
                Volatile.Write(ref _isProcessingFrame, 0);
            }
        }

        private void ScanQrAsync(Mat frame)
        {
            var results = _qr.Detect(frame);
            var now     = DateTime.Now;

            // Merge new results with the 1-second overlay window
            var valid = new List<QRCodeResult>(results);
            lock (_resultsLock)
            {
                foreach (var old in _lastKnownResults)
                {
                    if (now - old.LastSeen < TimeSpan.FromSeconds(1) &&
                        !valid.Exists(x => x.Text == old.Text))
                        valid.Add(old);
                }
                _lastKnownResults = valid;
            }

            if (results.Count > 0)
                _dispatcher.BeginInvoke(() => ProcessQrResults(results));
        }

        private void ProcessQrResults(List<QRCodeResult> results)
        {
            LastUpdatedText = DateTime.Now.ToString("HH:mm:ss");

            // Collect only genuinely new results (not already in the list)
            var newItems = results
                .Where(r => !QRResults.Any(e => e.Text == r.Text))
                .ToList();

            if (newItems.Count == 0) return;

            // Assign sequential Ids starting from the next available number.
            // We iterate in natural order (oldest-first in the batch) so that
            // the first code found gets the lowest Id (e.g. SKU-001, SKU-002 …).
            int nextId = DetectedCount + 1;
            foreach (var r in newItems)
                r.Id = nextId++;

            // Insert newest (highest Id) at position 0 so the list reads top-down
            // newest-first, and the Id numbers still ascend bottom-to-top correctly.
            for (int i = newItems.Count - 1; i >= 0; i--)
                QRResults.Insert(0, newItems[i]);

            // Update DetectedCount once — avoids triggering TotalCount / AccuracyText
            // notifications on every individual insertion.
            DetectedCount = QRResults.Count;
        }

        // ── annotation helpers ────────────────────────────────────────────────
        private static void DrawAnnotations(Mat img, List<QRCodeResult> results)
        {
            // Neon-green colour for QR_CODE / unknown, orange for linear barcodes
            static Scalar PickColor(string fmt) =>
                string.IsNullOrEmpty(fmt) || fmt == "QR_CODE" || fmt == "DATA_MATRIX" || fmt == "AZTEC" || fmt == "PDF_417"
                    ? new Scalar(0, 230, 80)     // green  (BGR)
                    : new Scalar(0, 165, 255);   // orange (BGR)

            foreach (var qr in results)
            {
                if (qr.Points == null || qr.Points.Length < 2) continue;

                var color = PickColor(qr.Format);
                int n     = qr.Points.Length;

                // ── Draw polygon edges (thick) ────────────────────────────────
                for (int i = 0; i < n; i++)
                    Cv2.Line(img, qr.Points[i], qr.Points[(i + 1) % n], color, 3, LineTypes.AntiAlias);

                // ── Corner markers (small filled squares) ─────────────────────
                foreach (var pt in qr.Points)
                {
                    Cv2.Rectangle(img,
                        new OpenCvSharp.Rect(pt.X - 5, pt.Y - 5, 10, 10),
                        color, -1, LineTypes.AntiAlias);
                }

                // ── Label with filled background ──────────────────────────────
                string fmt   = string.IsNullOrEmpty(qr.Format) ? "" : $"[{qr.Format}] ";
                string label = $"#{qr.Id} {fmt}{TruncateText(qr.Text, 24)}";

                // Measure label size
                int    baseline  = 0;
                double fontScale = 0.52;
                int    thick     = 1;
                var    textSize  = Cv2.GetTextSize(label, HersheyFonts.HersheySimplex, fontScale, thick, out baseline);

                // Anchor: above the first point (top-left of polygon)
                int tx = qr.Points[0].X;
                int ty = Math.Max(textSize.Height + 6, qr.Points[0].Y - 6);

                // Filled background rect
                var bgRect = new OpenCvSharp.Rect(tx - 2, ty - textSize.Height - 6,
                                                  textSize.Width + 6, textSize.Height + baseline + 8);
                // Clamp to image bounds
                bgRect = new OpenCvSharp.Rect(
                    Math.Max(0, bgRect.X),
                    Math.Max(0, bgRect.Y),
                    Math.Min(bgRect.Width,  img.Width  - Math.Max(0, bgRect.X)),
                    Math.Min(bgRect.Height, img.Height - Math.Max(0, bgRect.Y)));

                if (bgRect.Width > 0 && bgRect.Height > 0)
                    Cv2.Rectangle(img, bgRect, color, -1, LineTypes.AntiAlias);

                Cv2.PutText(img, label,
                    new OpenCvSharp.Point(tx, ty - 2),
                    HersheyFonts.HersheySimplex, fontScale,
                    new Scalar(15, 15, 15),   // near-black text on coloured background
                    thick, LineTypes.AntiAlias);
            }
        }

        private static string TruncateText(string s, int max)
            => s.Length <= max ? s : s[..max] + "…";

        private void SetStatus(string text, byte r, byte g, byte b)
        {
            StatusText   = text;
            StatusBrush  = new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        public void Cleanup()
        {
            _camera.OnFrame -= OnFrameReceived;
            _camera.Dispose();
        }
    }
}
