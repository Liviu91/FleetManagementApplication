namespace WebApplication1.DTOs
{
    public class UserDTO
    {
        //public int Id { get; set; }
        public string Id { get; set; }
        public required string FirstName { get; set; }
        public required string LastName { get; set; }
    }

    public class CreateUserDTO
    {
        public required string DisplayName { get; set; }
        public required string Email { get; set; }
        public string? Password { get; set; }
        public string Role { get; set; } = "Driver";
    }
}
