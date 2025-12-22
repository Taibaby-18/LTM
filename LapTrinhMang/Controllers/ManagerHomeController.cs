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
        return View();
    }

    public IActionResult Reservations()
    {
        ViewBag.Name = User.FindFirst("name")?.Value ?? "";
        ViewBag.Phone = User.FindFirst("phone")?.Value ?? "";
        ViewBag.Role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "";
        return View();
    }

    private async Task<(string status, string? customer, string? phone, DateTime? endTimeUtc)> GetCurrentTableState(int tableId, DateTime nowUtc)
    {
        var a = await _db.Reservations
            .AsNoTracking()
            .Where(r => r.TableId == tableId
                        && (r.Status == "Pending" || r.Status == "Approved")
                        && r.StartTime <= nowUtc && nowUtc < r.EndTime)
            .OrderByDescending(r => r.Id)
            .Select(r => new { r.Status, r.CustomerName, r.Phone, r.EndTime })
            .FirstOrDefaultAsync();

        if (a == null) return ("Available", null, null, null);

        var st = a.Status == "Pending" ? "Pending" : "Reserved";
        return (st, a.CustomerName, a.Phone, DateTime.SpecifyKind(a.EndTime, DateTimeKind.Utc));
    }

    // ===== API: TABLES (grid bàn) =====
    [HttpGet]
    [Route("api/manager/tables")]
    public async Task<IActionResult> GetTablesStatus()
    {
        var now = DateTime.UtcNow;

        var tables = await _db.Tables
            .AsNoTracking()
            .OrderBy(t => t.Number)
            .Select(t => new { t.Id, t.Number, t.Capacity })
            .ToListAsync();

        var candidates = await _db.Reservations
            .AsNoTracking()
            .Where(r => (r.Status == "Pending" || r.Status == "Approved") && r.EndTime > now)
            .OrderBy(r => r.StartTime)
            .Select(r => new
            {
                r.TableId,
                r.Status,
                r.CustomerName,
                r.Phone,
                r.StartTime,
                r.EndTime
            })
            .ToListAsync();

        var nextByTable = candidates
            .GroupBy(x => x.TableId)
            .ToDictionary(g => g.Key, g => g.First());

        var result = tables.Select(t =>
        {
            nextByTable.TryGetValue(t.Id, out var a);

            var status = "Available";
            if (a != null) status = (a.Status == "Pending") ? "Pending" : "Reserved";

            var isUpcoming = a != null && a.StartTime > now;

            return new
            {
                tableNumber = t.Number,
                capacity = t.Capacity,
                status,
                currentCustomer = a?.CustomerName,
                currentPhone = a?.Phone,
                currentStartTime = a == null ? (DateTime?)null : DateTime.SpecifyKind(a.StartTime, DateTimeKind.Utc),
                currentEndTime = a == null ? (DateTime?)null : DateTime.SpecifyKind(a.EndTime, DateTimeKind.Utc),
                isUpcoming
            };
        });

        return Ok(result);
    }

    // ===== API: RESERVATIONS LIST =====
    [HttpGet]
    [Route("api/manager/reservations")]
    public async Task<IActionResult> GetReservations([FromQuery] string? status, [FromQuery] int? tableNumber)
    {
        var q = _db.Reservations
            .AsNoTracking()
            .Include(r => r.Table)
            .OrderByDescending(r => r.Id)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(r => r.Status == status);

        if (tableNumber.HasValue)
            q = q.Where(r => r.Table != null && r.Table.Number == tableNumber.Value);

        var items = await q.Select(r => new
        {
            r.Id,
            tableNumber = r.Table!.Number,
            r.CustomerName,
            r.Phone,
            startTime = DateTime.SpecifyKind(r.StartTime, DateTimeKind.Utc),
            endTime = DateTime.SpecifyKind(r.EndTime, DateTimeKind.Utc),
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
        if (r is null) return NotFound(new { message = "Not found" });

        if (r.Status == "Canceled")
            return BadRequest(new { message = "Already canceled" });

        if (r.Status != "Pending")
            return BadRequest(new { message = "Only Pending can be approved" });

        var overlapApproved = await _db.Reservations.AnyAsync(x =>
            x.Id != r.Id &&
            x.TableId == r.TableId &&
            x.Status == "Approved" &&
            x.StartTime < r.EndTime &&
            r.StartTime < x.EndTime
        );
        if (overlapApproved)
            return Conflict(new { message = "Overlapping with another approved reservation" });

        r.Status = "Approved";
        await _db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var tableState = await GetCurrentTableState(r.TableId, now);

        await _hub.Clients.All.SendAsync("ReservationUpdated", new
        {
            id = r.Id,
            tableNumber = r.Table!.Number,
            status = r.Status,
            startTime = DateTime.SpecifyKind(r.StartTime, DateTimeKind.Utc),
            endTime = DateTime.SpecifyKind(r.EndTime, DateTimeKind.Utc),
            customerName = r.CustomerName,
            phone = r.Phone
        });

        await _hub.Clients.All.SendAsync("TableUpdated", new
        {
            tableNumber = r.Table!.Number,
            status = tableState.status,
            currentCustomer = tableState.customer,
            currentPhone = tableState.phone,
            currentEndTime = tableState.endTimeUtc
        });

        return Ok(new { id = r.Id, status = r.Status, message = "Approved" });
    }

    // ===== API: CANCEL =====
    [HttpPut]
    [Route("api/manager/reservations/{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        var r = await _db.Reservations.Include(x => x.Table).FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return NotFound(new { message = "Not found" });

        if (r.Status == "Canceled")
            return Ok(new { id = r.Id, status = r.Status, message = "Already canceled" });

        r.Status = "Canceled";
        await _db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var tableState = await GetCurrentTableState(r.TableId, now);

        await _hub.Clients.All.SendAsync("ReservationCanceled", new
        {
            id = r.Id,
            tableNumber = r.Table!.Number,
            status = r.Status
        });

        await _hub.Clients.All.SendAsync("TableUpdated", new
        {
            tableNumber = r.Table!.Number,
            status = tableState.status,
            currentCustomer = tableState.customer,
            currentPhone = tableState.phone,
            currentEndTime = tableState.endTimeUtc
        });

        return Ok(new { id = r.Id, status = r.Status, message = "Canceled" });
    }
}
