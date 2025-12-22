using LapTrinhMang.Data;
using LapTrinhMang.Dtos;
using LapTrinhMang.Hubs;
using LapTrinhMang.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace LapTrinhMang.Controllers;

[Authorize(Roles = "User")]
public class UserHomeController : Controller
{
    private readonly AppDbContext _db;
    private readonly IHubContext<BookingHub> _hub;

    public UserHomeController(AppDbContext db, IHubContext<BookingHub> hub)
    {
        _db = db;
        _hub = hub;
    }

    // ===== VIEW =====
    public IActionResult Index()
    {
        // nếu bạn không muốn hiện info thì có thể bỏ ViewBag luôn
        ViewBag.Name = User.FindFirst("name")?.Value ?? "";
        ViewBag.Phone = User.FindFirst("phone")?.Value ?? "";
        ViewBag.Role = User.FindFirst(ClaimTypes.Role)?.Value ?? "";
        return View(); // Views/UserHome/Index.cshtml
    }

    // ===== API: GET TABLES (để vẽ 20 bàn dạng card) =====
    [HttpGet]
    [Route("api/user/tables")]
    public async Task<IActionResult> GetTables()
    {
        var now = DateTime.UtcNow;

        var tables = await _db.Tables
            .OrderBy(t => t.Number)
            .Select(t => new { t.Id, t.Number, t.Capacity })
            .ToListAsync();

        // Lấy các đơn chưa canceled để tính trạng thái
        var active = await _db.Reservations
            .Where(r => r.Status != "Canceled" && r.EndTime > now)
            .Select(r => new
            {
                r.Id,
                r.TableId,
                r.StartTime,
                r.EndTime,
                r.Status,          // Pending / Reserved
                r.CustomerName,
                r.Phone
            })
            .ToListAsync();

        var result = tables.Select(t =>
        {
            var cur = active
                .Where(r => r.TableId == t.Id)
                .OrderBy(r => r.StartTime)
                .FirstOrDefault();

            // Nếu không có đơn => Available
            var status = cur == null ? "Available" : cur.Status;

            return new
            {
                tableId = t.Id,
                tableNumber = t.Number,
                capacity = t.Capacity,
                status,
                currentReservationId = cur?.Id,
                currentCustomer = cur?.CustomerName,
                currentPhone = cur?.Phone,
                currentStartTime = cur?.StartTime,
                currentEndTime = cur?.EndTime
            };
        });

        return Ok(result);
    }

    // ===== API: USER CREATE RESERVATION =====
    [HttpPost]
    [Route("api/user/reservations")]
    public async Task<IActionResult> CreateReservation([FromBody] CreateReservationDto dto)
    {
        var table = await _db.Tables.FirstOrDefaultAsync(t => t.Number == dto.TableNumber);
        if (table is null) return BadRequest(new { message = "Table not found" });

        if (dto.Hours <= 0 || dto.Hours > 12) return BadRequest(new { message = "Hours invalid" });

        var start = DateTime.SpecifyKind(dto.StartTime, DateTimeKind.Utc);
        var end = start.AddHours(dto.Hours);

        // CHỐT status: Pending / Reserved / Canceled
        var overlapped = await _db.Reservations.AnyAsync(r =>
            r.TableId == table.Id &&
            r.Status != "Canceled" &&
            start < r.EndTime && end > r.StartTime);

        if (overlapped) return Conflict(new { message = "Time slot is already booked" });

        int? userId = null;
        var sub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value // nếu bạn set NameIdentifier
                  ?? User.FindFirst("sub")?.Value;                 // hoặc claim sub
        if (int.TryParse(sub, out var id)) userId = id;

        var entity = new ReservationEntity
        {
            TableId = table.Id,
            UserId = userId,
            StartTime = start,
            EndTime = end,
            CustomerName = (dto.CustomerName ?? "").Trim(),
            Phone = (dto.Phone ?? "").Trim(),
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };

        _db.Reservations.Add(entity);
        await _db.SaveChangesAsync();

        // Realtime
        await _hub.Clients.All.SendAsync("ReservationCreated", new
        {
            id = entity.Id,
            tableNumber = table.Number,
            startTime = entity.StartTime,
            endTime = entity.EndTime,
            customerName = entity.CustomerName,
            phone = entity.Phone,
            status = entity.Status
        });

        await _hub.Clients.All.SendAsync("TableUpdated", new
        {
            tableNumber = table.Number,
            status = "Pending",
            startTime = entity.StartTime,
            endTime = entity.EndTime
        });

        return Ok(new { id = entity.Id, status = entity.Status });
    }

    // ===== API: USER CANCEL (optional) =====
    [HttpPut]
    [Route("api/user/reservations/{id:int}/cancel")]
    public async Task<IActionResult> CancelReservation(int id)
    {
        var r = await _db.Reservations.Include(x => x.Table).FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return NotFound();

        if (r.Status == "Canceled") return Ok(new { ok = true });

        r.Status = "Canceled";
        await _db.SaveChangesAsync();

        await _hub.Clients.All.SendAsync("ReservationCanceled", new
        {
            id = r.Id,
            tableNumber = r.Table.Number,
            status = r.Status
        });

        await _hub.Clients.All.SendAsync("TableUpdated", new
        {
            tableNumber = r.Table.Number,
            status = "Available"
        });

        return Ok(new { ok = true });
    }
}
