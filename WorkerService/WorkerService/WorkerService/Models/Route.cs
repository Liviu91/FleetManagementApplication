using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WorkerService.Models
{
    public class Route
    {
        [Key]
        public int Id { get; set; }
        [ForeignKey("User")]
        public string UserId { get; set; }
        [ForeignKey("Car")]
        public int CarId { get; set; }
        public virtual ICollection<CarData> CarDatas { get; set; }
    }
}
