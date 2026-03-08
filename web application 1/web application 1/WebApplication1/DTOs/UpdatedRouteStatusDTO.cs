namespace WebApplication1.DTOs
{
    public class UpdateRouteStatusDTO
    {
        public int RouteId { get; set; }
        public string Status { get; set; } // "Started" or "Finished"
    }
}
