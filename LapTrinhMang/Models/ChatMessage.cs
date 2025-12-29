using System.ComponentModel.DataAnnotations;

namespace LapTrinhMang.Models;

public class ChatMessage
{
    [Key]
    public int Id { get; set; }
    public int? SenderId { get; set; }      // ID người gửi
    public int? ReceiverId { get; set; }    // ID người nhận (Null nếu gửi chung cho Manager)
    public string Message { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsFromManager { get; set; } // True: Manager gửi, False: User gửi
}