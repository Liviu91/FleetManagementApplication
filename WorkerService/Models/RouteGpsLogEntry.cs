using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WorkerService.Models
{
    public class RouteGpsLogEntry
    {
        public int RouteId { get; set; }
        public DateTime Timestamp { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string? RPM { get; set; }
        public string? Speed { get; set; }
        public string? EngineCoolantTemperature { get; set; }
    }
}
