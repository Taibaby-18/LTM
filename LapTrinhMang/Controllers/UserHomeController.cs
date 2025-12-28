using LapTrinhMang.Data;
using LapTrinhMang.Dtos;
using LapTrinhMang.Hubs;
using LapTrinhMang.Models;
using LapTrinhMang.Services;
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
    private readonly IServiceScopeFactory _scopeFactory;

    public UserHomeController(AppDbContext db, IHubContext<BookingHub> hub, IServiceScopeFactory scopeFactory)
    {
        _db = db;
        _hub = hub;
        _scopeFactory = scopeFactory;
    }

    // ===== VIEW: TRANG CHỦ =====
    public IActionResult Index()
    {
        ViewBag.Name = User.FindFirst("name")?.Value ?? "";
        ViewBag.Phone = User.FindFirst("phone")?.Value ?? "";
        ViewBag.Email = User.FindFirst("email")?.Value ?? "";
        ViewBag.Role = User.FindFirst(ClaimTypes.Role)?.Value ?? "";
        return View();
    }

    // ===== VIEW: LỊCH SỬ =====
    [HttpGet]
    public async Task<IActionResult> BookingHistory()
    {
        var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out int userId))
        {
            return RedirectToAction("Index");
        }

        var list = await _db.Reservations
            .AsNoTracking()
            .Include(r => r.Table)
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return View(list);
    }

    // ===== API: LẤY DANH SÁCH BÀN =====
    [HttpGet]
    [Route("api/user/tables")]
    public async Task<IActionResult> GetTables()
    {
        var nowUtc = DateTime.UtcNow;

        var tables = await _db.Tables.AsNoTracking()
            .OrderBy(t => t.Number)
            .Select(t => new { t.Id, t.Number, t.Capacity, t.Type })
            .ToListAsync();

        var list = await _db.Reservations.AsNoTracking()
            .Where(r => r.Status != "Canceled" && r.EndTime > nowUtc)
            .Select(r => new { r.Id, r.TableId, r.StartTime, r.EndTime, r.Status, r.CustomerName, r.Phone })
            .ToListAsync();

        var result = tables.Select(t =>
        {
            var byTable = list.Where(x => x.TableId == t.Id).ToList();
            var activeNow = byTable.Where(r => r.StartTime <= nowUtc && nowUtc < r.EndTime)
                                   .OrderByDescending(r => r.Id).FirstOrDefault();

            var status = "Available";
            if (activeNow != null) status = activeNow.Status == "Approved" ? "Approved" : "Pending";

            return new
            {
                tableNumber = t.Number,
                capacity = t.Capacity,
                type = t.Type,
                status,
                slotCount = 0
            };
        });

        return Ok(result);
    }

    // ===== API: TẠO ĐƠN ĐẶT BÀN =====
    [HttpPost]
    [Route("api/user/reservations")]
    public async Task<IActionResult> CreateReservation([FromBody] CreateReservationDto dto)
    {
        // 1. Validate
        var table = await _db.Tables.FirstOrDefaultAsync(t => t.Number == dto.TableNumber);
        if (table is null) return BadRequest(new { message = "Bàn không tồn tại" });
        if (dto.Hours <= 0 || dto.Hours > 12) return BadRequest(new { message = "Thời gian không hợp lệ" });

        var startUtc = ParseClientLocalToUtc(dto.StartTime);
        var endUtc = startUtc.AddHours(dto.Hours);

        if (startUtc <= DateTime.UtcNow)
        {
            return BadRequest(new { message = "Suất này đã bắt đầu hoặc đã qua. Vui lòng chọn khung giờ khác." });
        }

        if (startUtc > DateTime.UtcNow.AddDays(30))
            return BadRequest(new { message = "Không được đặt trước quá 30 ngày" });

        var overlapped = await _db.Reservations.AnyAsync(r =>
            r.TableId == table.Id && r.Status != "Canceled" && startUtc < r.EndTime && endUtc > r.StartTime);
        if (overlapped) return Conflict(new { message = "Khung giờ này đã có người đặt." });

        // 2. Lấy User
        int? userId = null;
        var sub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        if (int.TryParse(sub, out var id)) userId = id;

        // 3. Lưu DB
        var entity = new ReservationEntity
        {
            TableId = table.Id,
            UserId = userId,
            StartTime = startUtc,
            EndTime = endUtc,
            CustomerName = (dto.CustomerName ?? "").Trim(),
            Phone = (dto.Phone ?? "").Trim(),
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };
        _db.Reservations.Add(entity);
        await _db.SaveChangesAsync();

        // 4. GỬI EMAIL (CHẠY NGẦM)
        if (userId != null)
        {
            var currentUserId = userId.Value;
            var bookingId = entity.Id;
            var tableInfo = $"{table.Number} ({table.Type})";
            var custName = entity.CustomerName;
            var custPhone = entity.Phone;
            var bookTime = entity.StartTime;
            var hours = dto.Hours;

            Task.Run(async () =>
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var dbScope = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var mailerScope = scope.ServiceProvider.GetRequiredService<SendMailService>();

                    try
                    {
                        var email = await dbScope.Users
                            .Where(u => u.Id == currentUserId)
                            .Select(u => u.Email)
                            .FirstOrDefaultAsync();

                        if (!string.IsNullOrEmpty(email))
                        {
                            // ✏️ SỬA TIÊU ĐỀ: Dùng icon đồng hồ cát và từ ngữ "Đã nhận yêu cầu"
                            var subject = $"⏳ Đã nhận yêu cầu đặt bàn #{bookingId} - ABC Restaurant";

                            var body = GetHtmlEmailBody(custName, bookingId, tableInfo, bookTime, hours, custPhone);

                            await mailerScope.SendEmailAsync(email, subject, body);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[MAIL ERROR] {ex.Message}");
                    }
                }
            });
        }

        // 5. SIGNALR
        await _hub.Clients.All.SendAsync("TableUpdated", new { tableNumber = table.Number });
        await _hub.Clients.All.SendAsync("ReservationCreated", new { id = entity.Id });

        return Ok(new { id = entity.Id, status = entity.Status });
    }

    // ===== API: HỦY ĐẶT BÀN =====
    [HttpPut]
    [Route("api/user/reservations/{id:int}/cancel")]
    public async Task<IActionResult> CancelReservation(int id)
    {
        var r = await _db.Reservations.Include(x => x.Table).FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return NotFound();

        var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        if (int.TryParse(userIdStr, out var userId) && r.UserId != userId) return Forbid();

        if (r.Status == "Canceled") return Ok(new { ok = true });

        r.Status = "Canceled";
        await _db.SaveChangesAsync();

        await _hub.Clients.All.SendAsync("TableUpdated", new { tableNumber = r.Table.Number });
        await _hub.Clients.All.SendAsync("ReservationCanceled", new { id = r.Id });

        return Ok(new { ok = true });
    }

    // ===== API: LẤY SLOT (GIỜ TRỐNG) =====
    [HttpGet]
    [Route("api/user/tables/{tableNumber:int}/slots")]
    public async Task<IActionResult> GetAvailableSlots(int tableNumber, [FromQuery] DateOnly? date)
    {
        var table = await _db.Tables.AsNoTracking().FirstOrDefaultAsync(t => t.Number == tableNumber);
        if (table == null) return NotFound();

        var day = date ?? DateOnly.FromDateTime(DateTime.Now);
        var localDayStart = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local);
        var dayStartUtc = localDayStart.ToUniversalTime();
        var dayEndUtc = localDayStart.AddDays(1).ToUniversalTime();

        var booked = await _db.Reservations.AsNoTracking()
            .Where(r => r.TableId == table.Id && r.Status != "Canceled" && r.StartTime < dayEndUtc && r.EndTime > dayStartUtc)
            .Select(r => new { r.StartTime, r.EndTime })
            .ToListAsync();

        (TimeSpan start, TimeSpan end)[] slotTemplates = table.Type switch
        {
            "VIP" => new[] { (new TimeSpan(17, 0, 0), new TimeSpan(19, 0, 0)), (new TimeSpan(19, 0, 0), new TimeSpan(21, 0, 0)) },
            "VVIP" => new[] { (new TimeSpan(18, 0, 0), new TimeSpan(21, 0, 0)) },
            _ => new[] {
                (new TimeSpan(17,0,0), new TimeSpan(18,0,0)),
                (new TimeSpan(18,0,0), new TimeSpan(19,0,0)),
                (new TimeSpan(19,0,0), new TimeSpan(20,0,0)),
                (new TimeSpan(20,0,0), new TimeSpan(21,0,0))
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

            if (startUtc <= DateTime.UtcNow) continue;

            if (booked.Any(r => startUtc < r.EndTime && endUtc > r.StartTime)) continue;

            result.Add(new
            {
                startLocal = startLocal.ToString("yyyy-MM-ddTHH:mm"),
                endLocal = endLocal.ToString("yyyy-MM-ddTHH:mm"),
                hours = (int)(endUtc - startUtc).TotalHours
            });
        }
        return Ok(result);
    }

    private static DateTime ParseClientLocalToUtc(DateTime dt)
    {
        var local = dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
        if (dt.Kind == DateTimeKind.Unspecified) local = DateTime.SpecifyKind(dt, DateTimeKind.Local);
        return local.ToUniversalTime();
    }

    // ===== HELPER: HTML EMAIL (NỘI DUNG MỚI) =====
    private static string GetHtmlEmailBody(string custName, int bookingId, string tableInfo, DateTime startTimeUtc, int hours, string phone)
    {
        return $@"
        <!DOCTYPE html>
        <html>
        <head>
            <style>
                body {{ font-family: 'Segoe UI', Arial, sans-serif; color: #333; }}
                .container {{ max-width: 600px; margin: 0 auto; border: 1px solid #ddd; border-radius: 8px; overflow: hidden; }}
                .header {{ background: #f59e0b; color: #fff; padding: 20px; text-align: center; }} /* Màu vàng cam cho trạng thái chờ */
                .content {{ padding: 20px; }}
                .info {{ width: 100%; border-collapse: collapse; margin: 15px 0; }}
                .info td {{ padding: 10px; border-bottom: 1px solid #eee; }}
                .footer {{ background: #f8f9fa; padding: 15px; text-align: center; font-size: 12px; color: #777; }}
                .badge {{ background: #fff3cd; color: #856404; padding: 4px 10px; border-radius: 12px; font-weight: bold; font-size: 12px; }}
                .note {{ background: #fff7ed; border-left: 4px solid #f97316; padding: 10px; margin-top: 20px; font-size: 0.9rem; color: #c2410c; }}
            </style>
        </head>
        <body>
            <div class='container'>
                <div class='header'>
                    <h1>ABC RESTAURANT</h1>
                    <p>Yêu cầu đặt bàn đã được tiếp nhận</p>
                </div>
                <div class='content'>
                    <h2 style='color: #d97706; margin-top: 0;'>Xin chào {custName},</h2>
                    <p>Cảm ơn bạn đã gửi yêu cầu đặt bàn. Chúng tôi đang kiểm tra tình trạng bàn và sẽ phản hồi sớm nhất.</p>
                    
                    <table class='info'>
                        <tr><td><b>Mã đơn:</b></td><td>#{bookingId}</td></tr>
                        <tr><td><b>Bàn số:</b></td><td>{tableInfo}</td></tr>
                        <tr><td><b>Thời gian:</b></td><td>{startTimeUtc.ToLocalTime():HH:mm dd/MM/yyyy}</td></tr>
                        <tr><td><b>Thời lượng:</b></td><td>{hours} tiếng</td></tr>
                        <tr><td><b>SĐT Liên hệ:</b></td><td>{phone}</td></tr>
                        <tr><td><b>Trạng thái:</b></td><td><span class='badge'>PENDING (Đang chờ duyệt)</span></td></tr>
                    </table>

                    <div class='note'>
                        <b>Lưu ý quan trọng:</b><br>
                        Đây chưa phải là xác nhận đặt bàn thành công. Vui lòng chờ email <b>Xác nhận (Approved)</b> từ quản lý nhà hàng trước khi đến.
                    </div>
                </div>
                <div class='footer'>
                    <p>123 Đường ABC, Quận 1, TP.HCM | Hotline: 0123 456 789</p>
                    <p>&copy; {DateTime.Now.Year} ABC Restaurant</p>
                </div>
            </div>
        </body>
        </html>";
    }
}