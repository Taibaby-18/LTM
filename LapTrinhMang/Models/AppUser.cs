namespace LapTrinhMang.Models;

public class AppUser
{
    public int Id { get; set; }
    public string Phone { get; set; } = "";
    public string FullName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "User"; // User / Manager
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
