using System.ComponentModel.DataAnnotations.Schema;
using WebApplication1.Enums;
using WebApplication1.Models;

namespace WebApplication1.DTOs
{
    public class RouteDTO
    {
        //public int UserId { get; set; }
        public string UserId { get; set; }
        public int CarId { get; set; }
        public required string Name { get; set; }
        public required string Start { get; set; }
        public required string End { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
    }
}
