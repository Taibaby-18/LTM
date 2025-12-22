using LapTrinhMang.Data;
using LapTrinhMang.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LapTrinhMang.Controllers;

[Authorize(Roles = "Manager")]
public class ManagerHomeController : Controller
{
    private readonly AppDbContext _db;
    private readonly IHubContext<BookingHub> _hub;

    public ManagerHomeController(AppDbContext db, IHubContext<BookingHub> hub)
    {
        _db = db;
        _hub = hub;
    }

    // ===== VIEWS =====
    public IActionResult Index()
    {
        ViewBag.Name = User.FindFirst("name")?.Value ?? "";
        ViewBag.Phone = User.FindFirst("phone")?.Value ?? "";
        ViewBag.Role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "";
        return View(); // Views/ManagerHome/Index.cshtml
    }

    public IActionResult Reservations()
    {
        ViewBag.Name = User.FindFirst("name")?.Value ?? "";
        ViewBag.Phone = User.FindFirst("phone")?.Value ?? "";
        ViewBag.Role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "";
        return View(); // Views/ManagerHome/Reservations.cshtml (tạo thêm nếu cần)
    }

    // ===== API: TABLES (grid bàn) =====
    [HttpGet]
    [Route("api/manager/tables")]
    public async Task<IActionResult> GetTablesStatus()
    {
        var now = DateTime.Now;

        var tables = await _db.Tables
            .AsNoTracking()
            .OrderBy(t => t.Number)
            .Select(t => new { t.Id, t.Number, t.Capacity })
            .ToListAsync();

        var active = await _db.Reservations
            .AsNoTracking()
            .Where(r => (r.Status == "Pending" || r.Status == "Approved")
                        && r.StartTime <= now && now < r.EndTime)
            .Select(r => new { r.TableId, r.Status, r.CustomerName, r.Phone, r.EndTime })
            .ToListAsync();

        var result = tables.Select(t =>
        {
            var a = active.FirstOrDefault(x => x.TableId == t.Id);

            var status = "Available";
            if (a != null) status = (a.Status == "Pending") ? "Pending" : "Reserved";

            return new
            {
                tableNumber = t.Number,
                capacity = t.Capacity,
                status,
                currentCustomer = a?.CustomerName,
                currentPhone = a?.Phone,
                currentEndTime = a?.EndTime
            };
        });

        return Ok(result);
    }

    // ===== API: RESERVATIONS LIST =====
    [HttpGet]
    [Route("api/manager/reservations")]
    public async Task<IActionResult> GetReservations([FromQuery] string? status)
    {
        var q = _db.Reservations
            .AsNoTracking()
            .Include(r => r.Table)
            .OrderByDescending(r => r.Id)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(r => r.Status == status);

        var items = await q.Select(r => new
        {
            r.Id,
            tableNumber = r.Table!.Number,
            r.CustomerName,
            r.Phone,
            r.StartTime,
            r.EndTime,
            r.Status,
            r.CreatedAt
        }).ToListAsync();

        return Ok(items);
    }

    // ===== API: APPROVE =====
    [HttpPut]
    [Route("api/manager/reservations/{id:int}/approve")]
    public async Task<IActionResult> Approve(int id)
    {
        var r = await _db.Reservations.Include(x => x.Table).FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return NotFound();

        if (r.Status == "Canceled") return BadRequest(new { message = "Already canceled" });

        r.Status = "Approved";
        await _db.SaveChangesAsync();

        await _hub.Clients.All.SendAsync("ReservationUpdated", new
        {
            id = r.Id,
            tableNumber = r.Table!.Number,
            status = r.Status
        });

        await _hub.Clients.All.SendAsync("TableUpdated", new
        {
            tableNumber = r.Table!.Number,
            status = "Reserved"
        });

        return Ok(new { message = "Approved" });
    }

    // ===== API: CANCEL =====
    [HttpPut]
    [Route("api/manager/reservations/{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        var r = await _db.Reservations.Include(x => x.Table).FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return NotFound();

        if (r.Status == "Canceled") return Ok(new { message = "Already canceled" });

        r.Status = "Canceled";
        await _db.SaveChangesAsync();

        await _hub.Clients.All.SendAsync("ReservationCanceled", new
        {
            id = r.Id,
            tableNumber = r.Table!.Number,
            status = r.Status
        });

        await _hub.Clients.All.SendAsync("TableUpdated", new
        {
            tableNumber = r.Table!.Number,
            status = "Available"
        });

        return Ok(new { message = "Canceled" });
    }
}
