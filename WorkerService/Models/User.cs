using System.ComponentModel.DataAnnotations;

namespace WorkerService.Models
{
    public class User
    {
        [Key]
        public int Id { get; set; }
        public required string FirstName { get; set; }
        public required string LastName { get; set; }
        public virtual ICollection<Route> UserCarMappings { get; set; }
    }
}
