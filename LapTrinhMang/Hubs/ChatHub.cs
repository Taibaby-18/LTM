using LapTrinhMang.Data;
using LapTrinhMang.Models;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace LapTrinhMang.Hubs;

public class ChatHub : Hub
{
    private readonly AppDbContext _db;

    public ChatHub(AppDbContext db)
    {
        _db = db;
    }

    // Khi kết nối, phân loại User vào nhóm
    public override async Task OnConnectedAsync()
    {
        var role = Context.User.FindFirst(ClaimTypes.Role)?.Value;
        var userId = Context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (role == "Manager")
        {
            // Manager tham gia vào nhóm chung để nhận tin từ tất cả User
            await Groups.AddToGroupAsync(Context.ConnectionId, "Managers");
        }
        else if (!string.IsNullOrEmpty(userId))
        {
            // User tham gia vào nhóm riêng của họ (để Manager trả lời đúng người)
            await Groups.AddToGroupAsync(Context.ConnectionId, $"User_{userId}");
        }

        await base.OnConnectedAsync();
    }

    // USER gửi tin nhắn cho MANAGER
    public async Task SendMessageToManager(string message)
    {
        var userIdStr = Context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var userName = Context.User.FindFirst("name")?.Value ?? "Khách";

        if (int.TryParse(userIdStr, out int userId))
        {
            // 1. Lưu DB
            var msg = new ChatMessage
            {
                SenderId = userId,
                Message = message,
                IsFromManager = false,
                CreatedAt = DateTime.UtcNow
            };
            _db.ChatMessages.Add(msg);
            await _db.SaveChangesAsync();

            // 2. Gửi cho nhóm Managers
            await Clients.Group("Managers").SendAsync("ReceiveMessageFromUser", userId, userName, message, msg.CreatedAt);
        }
    }

    // MANAGER trả lời cho USER cụ thể
    public async Task SendMessageToUser(int userId, string message)
    {
        var managerIdStr = Context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (int.TryParse(managerIdStr, out int managerId))
        {
            // 1. Lưu DB
            var msg = new ChatMessage
            {
                SenderId = managerId,
                ReceiverId = userId,
                Message = message,
                IsFromManager = true,
                CreatedAt = DateTime.UtcNow
            };
            _db.ChatMessages.Add(msg);
            await _db.SaveChangesAsync();

            // 2. Gửi về đúng User đó
            await Clients.Group($"User_{userId}").SendAsync("ReceiveMessageFromManager", message, msg.CreatedAt);
        }
    }
}