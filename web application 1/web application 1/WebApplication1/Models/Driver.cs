using System.ComponentModel.DataAnnotations;

namespace WebApplication1.Models
{
    public class Driver
    {
        [Key]
        public int Id { get; set; }
        public required string FirstName { get; set; }
        public required string LastName { get; set; }
        public virtual ICollection<Route> Routes { get; set; }
    }
}
