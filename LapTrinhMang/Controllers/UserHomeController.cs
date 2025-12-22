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
        ViewBag.Name = User.FindFirst("name")?.Value ?? "";
        ViewBag.Phone = User.FindFirst("phone")?.Value ?? "";
        ViewBag.Role = User.FindFirst(ClaimTypes.Role)?.Value ?? "";
        return View();
    }

    // ===== API: GET TABLES =====
    // Trả trạng thái "đang diễn ra" + "sắp tới" (để UI vẫn đặt được suất khác)
    [HttpGet]
    [Route("api/user/tables")]
    public async Task<IActionResult> GetTables()
    {
        var nowUtc = DateTime.UtcNow;

        var tables = await _db.Tables
    .AsNoTracking()
    .OrderBy(t => t.Number)
    .Select(t => new { t.Id, t.Number, t.Capacity, t.Type }) // ✅ thêm Type
    .ToListAsync();


        var list = await _db.Reservations
            .AsNoTracking()
            .Where(r => r.Status != "Canceled" && r.EndTime > nowUtc)
            .Select(r => new
            {
                r.Id,
                r.TableId,
                r.StartTime,  // UTC (đang lưu UTC)
                r.EndTime,    // UTC
                r.Status,     // Pending / Approved
                r.CustomerName,
                r.Phone
            })
            .ToListAsync();

        var result = tables.Select(t =>
        {
            var byTable = list.Where(x => x.TableId == t.Id).ToList();

            var activeNow = byTable
                .Where(r => (r.Status == "Pending" || r.Status == "Approved")
                            && r.StartTime <= nowUtc && nowUtc < r.EndTime)
                .OrderByDescending(r => r.Id)
                .FirstOrDefault();

            var upcoming = byTable
                .Where(r => (r.Status == "Pending" || r.Status == "Approved")
                            && r.StartTime > nowUtc)
                .OrderBy(r => r.StartTime)
                .FirstOrDefault();

            var status = "Available";
            if (activeNow != null)
                status = activeNow.Status == "Approved" ? "Approved" : "Pending";

            // đảm bảo serialize ra UTC rõ ràng
            DateTime? activeStartUtc = activeNow?.StartTime;
            DateTime? activeEndUtc = activeNow?.EndTime;
            DateTime? nextStartUtc = upcoming?.StartTime;
            DateTime? nextEndUtc = upcoming?.EndTime;

            return new
            {
                tableId = t.Id,
                tableNumber = t.Number,
                capacity = t.Capacity,
                type = t.Type,
                status,

                currentReservationId = activeNow?.Id,
                currentCustomer = activeNow?.CustomerName,
                currentPhone = activeNow?.Phone,
                currentStartTime = activeStartUtc,
                currentEndTime = activeEndUtc,

                nextReservationId = upcoming?.Id,
                nextCustomer = upcoming?.CustomerName,
                nextPhone = upcoming?.Phone,
                nextStartTime = nextStartUtc,
                nextEndTime = nextEndUtc,
                hasUpcoming = upcoming != null
            };
        });

        return Ok(result);
    }

    // ===== Helper: parse StartTime từ client (local string) => UTC =====
    private static DateTime ParseClientLocalToUtc(DateTime dt)
    {
        // JSON "yyyy-MM-ddTHH:mm" => Kind thường Unspecified
        // => coi là Local server rồi convert sang UTC để lưu
        var local = dt.Kind switch
        {
            DateTimeKind.Utc => dt.ToLocalTime(), // nếu lỡ gửi UTC
            DateTimeKind.Local => dt,
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Local)
        };
        return local.ToUniversalTime();
    }

    // ===== API: USER CREATE RESERVATION =====
    [HttpPost]
    [Route("api/user/reservations")]
    public async Task<IActionResult> CreateReservation([FromBody] CreateReservationDto dto)
    {
        var table = await _db.Tables.FirstOrDefaultAsync(t => t.Number == dto.TableNumber);
        if (table is null) return BadRequest(new { message = "Table not found" });

        if (dto.Hours <= 0 || dto.Hours > 12)
            return BadRequest(new { message = "Hours invalid" });

        var startUtc = ParseClientLocalToUtc(dto.StartTime);
        var endUtc = startUtc.AddHours(dto.Hours);

        // bắt buộc tương lai (đệm 1 phút)
        var minStart = DateTime.UtcNow.AddMinutes(1);
        if (startUtc < minStart)
            return BadRequest(new { message = "Thời gian phải ở tương lai" });

        // chặn đặt quá xa 30 ngày
        var maxStart = DateTime.UtcNow.AddDays(30);
        if (startUtc > maxStart)
            return BadRequest(new { message = "Thời gian đặt quá 30 ngày" });

        // chặn trùng giờ với đơn chưa hủy
        var overlapped = await _db.Reservations.AnyAsync(r =>
            r.TableId == table.Id &&
            r.Status != "Canceled" &&
            startUtc < r.EndTime && endUtc > r.StartTime);

        if (overlapped) return Conflict(new { message = "Thời gian này đã có người đặt" });

        int? userId = null;
        var sub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        if (int.TryParse(sub, out var id)) userId = id;

        var entity = new ReservationEntity
        {
            TableId = table.Id,
            UserId = userId,
            StartTime = startUtc,     // ✅ lưu UTC
            EndTime = endUtc,         // ✅ lưu UTC
            CustomerName = (dto.CustomerName ?? "").Trim(),
            Phone = (dto.Phone ?? "").Trim(),
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };

        _db.Reservations.Add(entity);
        await _db.SaveChangesAsync();

        // realtime
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
            endTime = entity.EndTime,
            customerName = entity.CustomerName,
            phone = entity.Phone
        });

        return Ok(new { id = entity.Id, status = entity.Status });
    }

    // ===== API: USER CANCEL =====
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

    // ===== API: AVAILABLE SLOTS FOR A TABLE (slot theo loại bàn) =====
    // GET /api/user/tables/1/slots?date=2025-12-23
    [HttpGet]
    [Route("api/user/tables/{tableNumber:int}/slots")]
    public async Task<IActionResult> GetAvailableSlots(int tableNumber, [FromQuery] DateOnly? date)
    {
        var table = await _db.Tables.AsNoTracking().FirstOrDefaultAsync(t => t.Number == tableNumber);
        if (table == null) return NotFound(new { message = "Table not found" });

        // Ngày user chọn (local)
        var day = date ?? DateOnly.FromDateTime(DateTime.Now);

        // Mốc bắt đầu ngày local server
        var localDayStart = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local);

        var nowUtc = DateTime.UtcNow;
        var dayStartUtc = localDayStart.ToUniversalTime();
        var dayEndUtc = localDayStart.AddDays(1).ToUniversalTime();

        // Lấy các reservation đã đặt trong ngày (Pending/Approved)
        var booked = await _db.Reservations
            .AsNoTracking()
            .Where(r => r.TableId == table.Id
                        && r.Status != "Canceled"
                        && r.StartTime < dayEndUtc
                        && r.EndTime > dayStartUtc)
            .Select(r => new { r.StartTime, r.EndTime })
            .ToListAsync();

        // =========================
        // SLOT THEO LOẠI BÀN
        // =========================
        // Bạn chỉnh giờ theo ý muốn tại đây.
        // Slot là theo giờ LOCAL, sau đó convert UTC để check overlap
        (TimeSpan start, TimeSpan end)[] slotTemplates = table.Type switch
        {
            "VIP" => new[]
            {
            (new TimeSpan(17,0,0), new TimeSpan(19,0,0)), // 2h
            (new TimeSpan(19,0,0), new TimeSpan(21,0,0)), // 2h
        },
            "VVIP" => new[]
            {
            (new TimeSpan(18,0,0), new TimeSpan(21,0,0)), // 3h
        },
            _ => new[]
            {
            (new TimeSpan(17,0,0), new TimeSpan(18,0,0)), // 1h
            (new TimeSpan(18,0,0), new TimeSpan(19,0,0)), // 1h
            (new TimeSpan(19,0,0), new TimeSpan(20,0,0)), // 1h
        }
        };

        var result = new List<object>();

        for (int i = 0; i < slotTemplates.Length; i++)
        {
            var s = slotTemplates[i];

            var startLocal = DateTime.SpecifyKind(localDayStart.Date + s.start, DateTimeKind.Local);
            var endLocal = DateTime.SpecifyKind(localDayStart.Date + s.end, DateTimeKind.Local);

            var startUtc = startLocal.ToUniversalTime();
            var endUtc = endLocal.ToUniversalTime();

            // bỏ slot quá khứ
            if (endUtc <= nowUtc) continue;

            // overlap check
            bool isTaken = booked.Any(r => startUtc < r.EndTime && endUtc > r.StartTime);
            if (isTaken) continue; // đã có người đặt => không trả về

            // giờ = duration thực tế (đúng VIP/VVIP)
            var hours = (int)Math.Ceiling((endUtc - startUtc).TotalHours);

            result.Add(new
            {
                slotKey = $"{day:yyyyMMdd}-{i + 1}",
                startLocal = startLocal.ToString("yyyy-MM-ddTHH:mm"),
                endLocal = endLocal.ToString("yyyy-MM-ddTHH:mm"),
                hours
            });
        }

        return Ok(result);
    }

}
