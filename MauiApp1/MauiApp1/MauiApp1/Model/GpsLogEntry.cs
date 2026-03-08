namespace MauiApp1.Model
{
    public class GpsLogEntry
    {
        public DateTime Timestamp { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        // Optionally add RouteId, RPM, etc. later
    }
}
