using LapTrinhMang.Data;
using LapTrinhMang.Hubs;
using LapTrinhMang.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Security.Claims;

namespace LapTrinhMang.Controllers;

[Authorize(Roles = "Manager")]
public class ManagerHomeController : Controller
{
    private readonly AppDbContext _db;
    private readonly IHubContext<BookingHub> _hub;
    private readonly IServiceScopeFactory _scopeFactory;

    public ManagerHomeController(AppDbContext db, IHubContext<BookingHub> hub, IServiceScopeFactory scopeFactory)
    {
        _db = db;
        _hub = hub;
        _scopeFactory = scopeFactory;
    }

    // ===== VIEWS =====
    public IActionResult Index()
    {
        ViewBag.Name = User.FindFirst("name")?.Value ?? "";
        ViewBag.Role = User.FindFirst(ClaimTypes.Role)?.Value ?? "";
        return View();
    }

    public IActionResult Reservations()
    {
        ViewBag.Name = User.FindFirst("name")?.Value ?? "";
        return View();
    }

    [HttpGet]
    public IActionResult Statistics()
    {
        return View();
    }

    [HttpGet]
    public IActionResult Chat()
    {
        ViewBag.Name = User.FindFirst("name")?.Value ?? "";
        return View();
    }

    // ===== API: LẤY TRẠNG THÁI BÀN (QUAN TRỌNG) =====
    [HttpGet]
    [Route("api/manager/tables")]
    public async Task<IActionResult> GetTablesStatus([FromQuery] string? date)
    {
        // 1. Xác định ngày xem (Mặc định hôm nay nếu null)
        var (dayOpenUtc, dayCloseUtc, dayLocal) = GetDayUtcRange(date);

        // 2. Lấy danh sách tất cả các bàn
        var tables = await _db.Tables.AsNoTracking()
            .OrderBy(t => t.Number)
            .Select(t => new { t.Id, t.Number, t.Capacity, t.Type })
            .ToListAsync();

        // 3. Lấy đơn đặt trong ngày đó (Pending hoặc Approved)
        var dayRes = await _db.Reservations.AsNoTracking()
            .Where(r => (r.Status == "Pending" || r.Status == "Approved")
                     && r.StartTime < dayCloseUtc && dayOpenUtc < r.EndTime) // Logic trùng lịch
            .Select(r => new { r.TableId, r.Status, r.StartTime, r.EndTime })
            .ToListAsync();

        var resByTable = dayRes.GroupBy(x => x.TableId).ToDictionary(g => g.Key, g => g.ToList());

        // 4. Tính toán trạng thái từng bàn
        var result = tables.Select(t =>
        {
            resByTable.TryGetValue(t.Id, out var list);

            // Đếm số lượng đơn Pending/Approved của bàn này
            var pendingCount = list?.Count(x => x.Status == "Pending") ?? 0;
            var approvedCount = list?.Count(x => x.Status == "Approved") ?? 0;

            // Tính số slot còn trống
            var allSlots = BuildSlotsUtc(dayLocal, t.Type);
            var bookedSlots = 0;

            if (list != null && list.Count > 0)
            {
                foreach (var s in allSlots)
                {
                    // Nếu slot đã qua giờ hiện tại -> Coi như đã mất (booked)
                    if (s.startUtc <= DateTime.UtcNow) { bookedSlots++; continue; }

                    // Nếu có đơn đặt chồng lên slot này -> Booked
                    if (list.Any(r => Overlap(r.StartTime, r.EndTime, s.startUtc, s.endUtc)))
                        bookedSlots++;
                }
            }
            else
            {
                // Nếu không có đơn nào, chỉ check giờ quá khứ
                foreach (var s in allSlots) if (s.startUtc <= DateTime.UtcNow) bookedSlots++;
            }

            var slotCount = Math.Max(0, allSlots.Count - bookedSlots);

            // Logic trạng thái hiển thị trên thẻ
            var status = "Available";
            if (slotCount <= 0) status = "Full";       // Hết chỗ
            else if (pendingCount > 0) status = "Pending"; // Có đơn chờ duyệt -> Ưu tiên hiện màu vàng
            else if (approvedCount > 0) status = "Reserved"; // Đã có người đặt -> Màu xanh dương

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

    // ===== API: LẤY DANH SÁCH ĐƠN =====
    [HttpGet]
    [Route("api/manager/reservations")]
    public async Task<IActionResult> GetReservations([FromQuery] string? status, [FromQuery] int? tableNumber, [FromQuery] string? date)
    {
        var (dayOpenUtc, dayCloseUtc, _) = GetDayUtcRange(date);

        var q = _db.Reservations.AsNoTracking().Include(r => r.Table)
            .Where(r => r.StartTime < dayCloseUtc && dayOpenUtc < r.EndTime) // Lọc theo ngày
            .OrderByDescending(r => r.CreatedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(r => r.Status == status);

        if (tableNumber.HasValue)
            q = q.Where(r => r.Table != null && r.Table.Number == tableNumber.Value);

        var items = await q.Select(r => new {
            r.Id,
            tableNumber = r.Table!.Number,
            r.CustomerName,
            r.Phone,
            startTime = r.StartTime,
            endTime = r.EndTime,
            r.Status,
            r.CreatedAt
        }).ToListAsync();

        return Ok(items);
    }

    // ===== API: DUYỆT ĐƠN (APPROVE) =====
    [HttpPut]
    [Route("api/manager/reservations/{id:int}/approve")]
    public async Task<IActionResult> Approve(int id)
    {
        var r = await _db.Reservations.Include(x => x.Table).FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return NotFound(new { message = "Not found" });

        if (r.Status != "Pending") return BadRequest(new { message = "Chỉ đơn Pending mới được duyệt." });

        // Check trùng lịch lần cuối trước khi duyệt
        var overlapApproved = await _db.Reservations.AnyAsync(x =>
            x.Id != r.Id && x.TableId == r.TableId && x.Status == "Approved" &&
            x.StartTime < r.EndTime && r.StartTime < x.EndTime
        );

        if (overlapApproved) return Conflict(new { message = "Bàn này đã có người đặt (Approved) vào giờ đó rồi." });

        r.Status = "Approved";
        await _db.SaveChangesAsync();

        // Gửi mail (Chạy ngầm)
        if (r.UserId != null)
        {
            var userId = r.UserId.Value;
            var bookingId = r.Id;
            var tableInfo = $"{r.Table.Number} ({r.Table.Type})";
            var bookTime = r.StartTime;
            var custName = r.CustomerName;
            var hours = (int)(r.EndTime - r.StartTime).TotalHours;

            Task.Run(async () =>
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var dbScope = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var mailerScope = scope.ServiceProvider.GetRequiredService<SendMailService>();
                    try
                    {
                        var email = await dbScope.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync();
                        if (!string.IsNullOrEmpty(email))
                        {
                            var subject = $"🎉 Đặt bàn thành công! #{bookingId} - FOURMEN RESTAURANT";
                            var body = GetApprovedHtmlBody(custName, bookingId, tableInfo, bookTime, hours);
                            await mailerScope.SendEmailAsync(email, subject, body);
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"[MAIL ERROR] {ex.Message}"); }
                }
            });
        }

        // SignalR báo cập nhật
        await _hub.Clients.All.SendAsync("ReservationUpdated", new { id = r.Id, status = r.Status });
        await _hub.Clients.All.SendAsync("TableUpdated", new { tableNumber = r.Table!.Number });

        return Ok(new { id = r.Id, status = r.Status, message = "Approved" });
    }

    // ===== API: HỦY ĐƠN (CANCEL) =====
    [HttpPut]
    [Route("api/manager/reservations/{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        var r = await _db.Reservations.Include(x => x.Table).FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return NotFound(new { message = "Not found" });

        if (r.Status == "Canceled") return Ok(new { id = r.Id, status = r.Status, message = "Already canceled" });

        r.Status = "Canceled";
        await _db.SaveChangesAsync();

        await _hub.Clients.All.SendAsync("ReservationCanceled", new { id = r.Id, status = r.Status });
        await _hub.Clients.All.SendAsync("TableUpdated", new { tableNumber = r.Table!.Number });

        return Ok(new { id = r.Id, status = r.Status, message = "Canceled" });
    }

    // ===== API: THỐNG KÊ (CHO TRANG STATS) =====
    [HttpGet]
    [Route("api/manager/stats")]
    public async Task<IActionResult> GetStats()
    {
        var today = DateTime.UtcNow.Date;
        var statusCounts = await _db.Reservations.GroupBy(r => r.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync();
        var todayCount = await _db.Reservations.CountAsync(r => r.StartTime >= today && r.StartTime < today.AddDays(1));
        var totalOrders = await _db.Reservations.CountAsync();

        return Ok(new { statusData = statusCounts, todayCount, totalOrders });
    }

    // ===== HELPERS (LOGIC TÍNH TOÁN NGÀY GIỜ) =====
    private static DateTime ParseLocalDateOrToday(string? ymd)
    {
        if (!string.IsNullOrWhiteSpace(ymd) &&
            DateTime.TryParseExact(ymd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d.Date;
        }
        return DateTime.Now.Date;
    }

    private static (DateTime dayOpenUtc, DateTime dayCloseUtc, DateTime dayLocal) GetDayUtcRange(string? date)
    {
        var dayLocal = ParseLocalDateOrToday(date);
        var openLocal = dayLocal;
        var closeLocal = dayLocal.AddDays(1);
        var openUtc = openLocal.ToUniversalTime();
        var closeUtc = closeLocal.ToUniversalTime();
        return (openUtc, closeUtc, dayLocal);
    }

    private sealed record SlotUtc(DateTime startUtc, DateTime endUtc);

    private static List<SlotUtc> BuildSlotsUtc(DateTime dayLocal, string? type)
    {
        var t = (type ?? "Normal").ToLowerInvariant();
        var slots = new List<(DateTime sLocal, DateTime eLocal)>();
        var d = dayLocal.Date;

        if (t.Contains("vvip")) slots.Add((d.AddHours(18), d.AddHours(21)));
        else if (t.Contains("vip")) { slots.Add((d.AddHours(17), d.AddHours(19))); slots.Add((d.AddHours(19), d.AddHours(21))); }
        else
        {
            slots.Add((d.AddHours(17), d.AddHours(18))); slots.Add((d.AddHours(18), d.AddHours(19)));
            slots.Add((d.AddHours(19), d.AddHours(20))); slots.Add((d.AddHours(20), d.AddHours(21)));
            slots.Add((d.AddHours(21), d.AddHours(22))); slots.Add((d.AddHours(22), d.AddHours(23)));
        }
        return slots.Select(x => new SlotUtc(x.sLocal.ToUniversalTime(), x.eLocal.ToUniversalTime())).ToList();
    }

    private static bool Overlap(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd) => aStart < bEnd && bStart < aEnd;

    // ===== EMAIL TEMPLATE =====
    private static string GetApprovedHtmlBody(string custName, int bookingId, string tableInfo, DateTime startTimeUtc, int hours)
    {
        return $@"
        <!DOCTYPE html><html><head><style>
            body {{ font-family: Arial, sans-serif; color: #333; }}
            .container {{ max-width: 600px; margin: 0 auto; border: 1px solid #10b981; border-radius: 8px; overflow: hidden; }}
            .header {{ background: #10b981; color: #fff; padding: 20px; text-align: center; }}
            .content {{ padding: 20px; }}
            .info td {{ padding: 10px; border-bottom: 1px solid #eee; }}
            .badge {{ background: #d1fae5; color: #065f46; padding: 4px 10px; border-radius: 12px; font-weight: bold; font-size: 12px; }}
        </style></head><body>
            <div class='container'>
                <div class='header'><h1>FOURMEN RESTAURANT</h1><p>Đặt bàn thành công!</p></div>
                <div class='content'>
                    <h2 style='color: #059669;'>Xin chào {custName},</h2>
                    <p>Đơn đặt bàn của bạn đã được duyệt.</p>
                    <table>
                        <tr><td><b>Mã đơn:</b></td><td>#{bookingId}</td></tr>
                        <tr><td><b>Bàn:</b></td><td>{tableInfo}</td></tr>
                        <tr><td><b>Giờ:</b></td><td>{startTimeUtc.ToLocalTime():HH:mm dd/MM/yyyy}</td></tr>
                        <tr><td><b>Trạng thái:</b></td><td><span class='badge'>APPROVED</span></td></tr>
                    </table>
                </div>
            </div>
        </body></html>";
    }
}