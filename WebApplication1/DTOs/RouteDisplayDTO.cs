using WebApplication1.Enums;

namespace WebApplication1.DTOs
{
    public class RouteDisplayDTO
    {
        public int Id { get; set; }
        public int CarId { get; set; }
        public string UserFullName { get; set; }
        public string CarSerialNumber { get; set; }
        public string Name { get; set; }
        public string Start { get; set; }
        public string End { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public Status Status { get; set; }
    }
}
