using System.ComponentModel.DataAnnotations;

namespace WebApplication1.Models
{
    public class Car
    {
        [Key]
        public int Id { get; set; }
        public required string SerialNumber { get; set; }
        public virtual ICollection<Route> Routes { get; set; }
    }
}
