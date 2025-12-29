using LapTrinhMang.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace LapTrinhMang.Controllers;

[Authorize]
public class ChatController : Controller
{
    private readonly AppDbContext _db;

    public ChatController(AppDbContext db)
    {
        _db = db;
    }

    // API: Lấy lịch sử chat của User đang đăng nhập (Cho User)
    [HttpGet("api/chat/history")]
    public async Task<IActionResult> GetMyHistory()
    {
        var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(userIdStr, out int userId)) return Unauthorized();

        var msgs = await _db.ChatMessages
            .Where(m => m.SenderId == userId || m.ReceiverId == userId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        return Ok(msgs);
    }

    // API: Lấy lịch sử chat với 1 User cụ thể (Cho Manager)
    [Authorize(Roles = "Manager")]
    [HttpGet("api/manager/chat/history/{userId}")]
    public async Task<IActionResult> GetUserHistory(int userId)
    {
        var msgs = await _db.ChatMessages
            .Where(m => m.SenderId == userId || m.ReceiverId == userId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        return Ok(msgs);
    }

    // API: Lấy danh sách User đã từng nhắn tin (Cho Manager)
    [Authorize(Roles = "Manager")]
    [HttpGet("api/manager/chat/users")]
    public async Task<IActionResult> GetChatUsers()
    {
        // Lấy danh sách ID user đã nhắn tin
        var userIds = await _db.ChatMessages
            .Where(m => !m.IsFromManager && m.SenderId != null)
            .Select(m => m.SenderId)
            .Distinct()
            .ToListAsync();

        var users = await _db.Users
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName, u.Phone })
            .ToListAsync();

        return Ok(users);
    }
}