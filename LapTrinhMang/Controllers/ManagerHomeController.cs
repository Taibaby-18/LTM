using LapTrinhMang.Data;
using LapTrinhMang.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

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

    // ===== Helpers for day range =====
    private static DateTime ParseLocalDateOrToday(string? ymd)
    {
        if (!string.IsNullOrWhiteSpace(ymd) &&
            DateTime.TryParseExact(ymd, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d))
        {
            return d.Date;
        }
        return DateTime.Now.Date;
    }

    private static (DateTime dayOpenUtc, DateTime dayCloseUtc, DateTime dayLocal) GetDayUtcRange(string? date)
    {
        var dayLocal = ParseLocalDateOrToday(date);
        // coi như local = server local. Nếu bạn dùng VN thì server local cũng thường VN.
        // Nếu bạn đang dùng UTC server, bạn đổi theo TimeZoneInfo Asia/Ho_Chi_Minh.
        var openLocal = dayLocal.AddHours(0);
        var closeLocal = dayLocal.AddDays(1);

        var openUtc = openLocal.ToUniversalTime();
        var closeUtc = closeLocal.ToUniversalTime();
        return (openUtc, closeUtc, dayLocal);
    }

    // ===== Slot schedule (PHẢI giống bên user) =====
    private sealed record SlotUtc(DateTime startUtc, DateTime endUtc);

    private static List<SlotUtc> BuildSlotsUtc(DateTime dayLocal, string? type)
    {
        // Bạn đổi giờ mở/đóng + slot theo đúng bài của bạn nếu cần.
        // Mặc định: Normal 3 suất, VIP 2 suất, VVIP 1 suất.
        // Giờ mở: 17:00. Normal: (17-18)(18-19)(19-20)
        // VIP: (17-18:30)(18:30-20:00)
        // VVIP: (17-20)
        var t = (type ?? "Normal").ToLowerInvariant();
        var slots = new List<(DateTime sLocal, DateTime eLocal)>();

        var d = dayLocal.Date;

        if (t.Contains("vvip"))
        {
            slots.Add((d.AddHours(17), d.AddHours(20)));
        }
        else if (t.Contains("vip"))
        {
            slots.Add((d.AddHours(17), d.AddHours(18.5)));
            slots.Add((d.AddHours(18.5), d.AddHours(20)));
        }
        else
        {
            slots.Add((d.AddHours(17), d.AddHours(18)));
            slots.Add((d.AddHours(18), d.AddHours(19)));
            slots.Add((d.AddHours(19), d.AddHours(20)));
        }

        return slots.Select(x => new SlotUtc(x.sLocal.ToUniversalTime(), x.eLocal.ToUniversalTime())).ToList();
    }

    private static bool Overlap(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd)
        => aStart < bEnd && bStart < aEnd;

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
    // Trả theo ngày để biết slotCount => Full/Hết suất
    [HttpGet]
    [Route("api/manager/tables")]
    public async Task<IActionResult> GetTablesStatus([FromQuery] string? date)
    {
        var (dayOpenUtc, dayCloseUtc, dayLocal) = GetDayUtcRange(date);

        // lấy bàn + type
        var tables = await _db.Tables
            .AsNoTracking()
            .OrderBy(t => t.Number)
            .Select(t => new { t.Id, t.Number, t.Capacity, t.Type })
            .ToListAsync();

        // lấy reservations trong ngày (Pending/Approved)
        var dayRes = await _db.Reservations
            .AsNoTracking()
            .Where(r =>
                (r.Status == "Pending" || r.Status == "Approved") &&
                r.StartTime < dayCloseUtc && dayOpenUtc < r.EndTime
            )
            .Select(r => new { r.TableId, r.Status, r.StartTime, r.EndTime })
            .ToListAsync();

        var resByTable = dayRes
            .GroupBy(x => x.TableId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = tables.Select(t =>
        {
            resByTable.TryGetValue(t.Id, out var list);


            // ✅ FIX: dùng list đã có kiểu anonymous từ query, không gán dynamic
            var pendingCount = list?.Count(x => x.Status == "Pending") ?? 0;
            var approvedCount = list?.Count(x => x.Status == "Approved") ?? 0;


            // slotCount = tổng slot - slot đã bị đặt (overlap)
            var slots = BuildSlotsUtc(dayLocal, t.Type);
            var totalSlots = slots.Count;

            var bookedSlots = 0;
            if (list != null && list.Count > 0)
            {
                foreach (var s in slots)
                {
                    var hasOverlap = list.Any(r => Overlap(r.StartTime, r.EndTime, s.startUtc, s.endUtc));
                    if (hasOverlap) bookedSlots++;
                }
            }

            var slotCount = Math.Max(0, totalSlots - bookedSlots);

            // status ưu tiên: Full > Pending > Reserved > Available
            var status = "Available";
            if (slotCount <= 0) status = "Full";
            else if (pendingCount > 0) status = "Pending";
            else if (approvedCount > 0) status = "Reserved";

            return new
            {
                tableNumber = t.Number,
                capacity = t.Capacity,
                type = t.Type ?? "Normal",

                slotCount,
                pendingCount,
                approvedCount,

                status
            };
        });

        return Ok(result);
    }





    // ===== API: RESERVATIONS LIST (lọc theo ngày cho đúng) =====
    [HttpGet]
    [Route("api/manager/reservations")]
    public async Task<IActionResult> GetReservations([FromQuery] string? status, [FromQuery] int? tableNumber, [FromQuery] string? date)
    {
        var (dayOpenUtc, dayCloseUtc, _) = GetDayUtcRange(date);

        var q = _db.Reservations
            .AsNoTracking()
            .Include(r => r.Table)
            .Where(r => r.StartTime < dayCloseUtc && dayOpenUtc < r.EndTime) // ✅ chỉ trong ngày
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
            tableNumber = r.Table!.Number
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

        await _hub.Clients.All.SendAsync("ReservationCanceled", new
        {
            id = r.Id,
            tableNumber = r.Table!.Number,
            status = r.Status
        });

        await _hub.Clients.All.SendAsync("TableUpdated", new
        {
            tableNumber = r.Table!.Number
        });

        return Ok(new { id = r.Id, status = r.Status, message = "Canceled" });
    }
}
