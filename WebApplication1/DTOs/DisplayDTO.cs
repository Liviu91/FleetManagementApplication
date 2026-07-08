namespace WebApplication1.DTOs
{
    public class DisplayDTO
    {
        public int RouteId { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string SerialNumber { get; set; }
        public string RPM { get; set; }
        public string EngineCoolantTemperature { get; set; }
        public DateTime? Timestamp { get; set; }
        public string Longitude { get; set; }
        public string Latitude { get; set; }
    }
}
