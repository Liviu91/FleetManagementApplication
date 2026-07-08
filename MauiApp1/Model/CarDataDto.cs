namespace MauiApp1.Model
{
    public class CarDataDto
    {
        public int RouteId { get; set; }
        public string? RPM { get; set; }
        public string? Speed { get; set; }
        public string? VIN { get; set; }
        public string? FuelType { get; set; }
        public string? EngineCoolantTemperature { get; set; }
        public string? FuelLevel { get; set; }
        public string? BatteryVoltage { get; set; }
        public string? ThrottlePosition { get; set; }
        public string? EngineLoad { get; set; }
        public string? IntakeAirTemperature { get; set; }
        public string? MAF { get; set; }
        public string? MAP { get; set; }
        public string? FuelRailPressure { get; set; }
        public string? O2SensorVoltage { get; set; }
        public string? LambdaValue { get; set; }
        public string? CatalystTemperature { get; set; }
        public DateTime Timestamp { get; set; }
        public string? Latitude { get; set; }
        public string? Longitude { get; set; }
    }
}
