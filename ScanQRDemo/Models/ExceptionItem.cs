using System;

namespace ScanQRDemo.Models
{
    /// <summary>Represents a scan failure shown in the Exception Queue card.</summary>
    public class ExceptionItem
    {
        public string   Message  { get; set; } = string.Empty;
        public DateTime Time     { get; set; } = DateTime.Now;
        public string   TimeText => Time.ToString("HH:mm:ss");
    }
}
