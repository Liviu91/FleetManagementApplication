using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using WebApplication1.Enums;

namespace WebApplication1.Models
{
    public class Route
    {
        [Key]
        public int Id { get; set; }
        //[ForeignKey("User")]
        //public int UserId { get; set; }
        [ForeignKey("User")]
        public string UserId { get; set; } = string.Empty; // Changed from int to string
        public virtual AppUser User { get; set; }          // Navigation to AppUser
        [ForeignKey("Car")]
        public int CarId { get; set; }
        public virtual Car Car { get; set; }
        public virtual ICollection<CarData> CarDatas { get; set; } = new List<CarData>();
        public Status Status { get; set; }
        public required string Name { get; set; }
        public required string Start { get; set; }
        public required string End { get; set; }
        public DateTime StartDate { get; set; } 
        public DateTime? EndDate { get; set; } 
        public DateTime Timestamp { get; set; }
    }
}
