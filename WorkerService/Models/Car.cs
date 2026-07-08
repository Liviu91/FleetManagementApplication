using System.ComponentModel.DataAnnotations;

namespace WorkerService.Models
{
    public class Car
    {
        [Key]
        public int Id { get; set; }
        public required string SerialNumber { get; set; }
        public virtual ICollection<Route> UserCarMappings { get; set; }
    }
}
