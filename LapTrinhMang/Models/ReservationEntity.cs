namespace LapTrinhMang.Models;

public class ReservationEntity
{
    public int Id { get; set; }

    public int TableId { get; set; }
    public TableEntity? Table { get; set; }

    public int? UserId { get; set; } // user đặt (nếu có)
    public AppUser? User { get; set; }

    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }

    public string CustomerName { get; set; } = "";
    public string Phone { get; set; } = "";

    // Pending / Approved / Canceled
    public string Status { get; set; } = "Pending";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
