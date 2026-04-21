using ScanQRDemo.Services;
using ScanQRDemo.ViewModels;
using ScanQRDemo.Views;
using System.Windows;

namespace ScanQRDemo
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Manual DI composition root
            ICameraService cameraService = new CameraService();
            IQRCodeService qrService     = new QRCodeService();
            var vm                       = new MainViewModel(cameraService, qrService);
            var window                   = new MainWindow(vm);

            window.Show();
        }
    }
}
