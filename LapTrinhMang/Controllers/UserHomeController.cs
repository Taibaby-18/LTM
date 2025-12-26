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
    private readonly SendMailService _mailService;

    public UserHomeController(AppDbContext db, IHubContext<BookingHub> hub, SendMailService mailService)
    {
        _db = db;
        _hub = hub;
        _mailService = mailService;
    }

    // ===== VIEW: TRANG CHỦ =====
    public IActionResult Index()
    {
        ViewBag.Name = User.FindFirst("name")?.Value ?? "";
        ViewBag.Phone = User.FindFirst("phone")?.Value ?? "";
        // Lấy Email từ Token để JS điền sẵn vào popup
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
                slotCount = 0 // Frontend sẽ tự gọi API slots để điền số này
            };
        });

        return Ok(result);
    }

    // ===== API: TẠO ĐƠN ĐẶT BÀN (QUAN TRỌNG) =====
    [HttpPost]
    [Route("api/user/reservations")]
    public async Task<IActionResult> CreateReservation([FromBody] CreateReservationDto dto)
    {
        // 1. Validate dữ liệu
        var table = await _db.Tables.FirstOrDefaultAsync(t => t.Number == dto.TableNumber);
        if (table is null) return BadRequest(new { message = "Bàn không tồn tại" });
        if (dto.Hours <= 0 || dto.Hours > 12) return BadRequest(new { message = "Thời gian đặt không hợp lệ" });

        var startUtc = ParseClientLocalToUtc(dto.StartTime);
        var endUtc = startUtc.AddHours(dto.Hours);

        if (startUtc < DateTime.UtcNow.AddMinutes(1)) return BadRequest(new { message = "Thời gian phải ở tương lai" });

        // Check trùng
        var overlapped = await _db.Reservations.AnyAsync(r =>
            r.TableId == table.Id && r.Status != "Canceled" && startUtc < r.EndTime && endUtc > r.StartTime);
        if (overlapped) return Conflict(new { message = "Khung giờ này đã có người đặt" });

        // 2. Lấy UserId
        int? userId = null;
        var sub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        if (int.TryParse(sub, out var id)) userId = id;

        // 3. Lưu vào DB
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

        // 4. GỬI EMAIL (CHẠY NGẦM - KHÔNG TREO UI)
        if (userId != null)
        {
            // Lấy email từ bảng Users (để chắc chắn có email)
            var userEmail = await _db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync();

            if (!string.IsNullOrEmpty(userEmail))
            {
                // Chuẩn bị nội dung HTML
                var subject = $"✅ Xác nhận đặt bàn #{entity.Id} - ABC Restaurant";
                var body = GetHtmlEmailBody(entity, table, dto.Hours); // Gọi hàm helper bên dưới

                // 🔥 Task.Run: Chạy luồng riêng để trả về OK ngay lập tức cho khách
                Task.Run(async () =>
                {
                    try
                    {
                        await _mailService.SendEmailAsync(userEmail, subject, body);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Lỗi Gửi Mail Ngầm]: {ex.Message}");
                    }
                });
            }
        }

        // 5. Gửi SignalR cập nhật Realtime
        await _hub.Clients.All.SendAsync("TableUpdated", new { tableNumber = table.Number });

        return Ok(new { id = entity.Id, status = entity.Status });
    }

    // ===== API: HỦY ĐẶT BÀN =====
    [HttpPut]
    [Route("api/user/reservations/{id:int}/cancel")]
    public async Task<IActionResult> CancelReservation(int id)
    {
        var r = await _db.Reservations.Include(x => x.Table).FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return NotFound();

        // Check quyền sở hữu
        var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        if (int.TryParse(userIdStr, out var userId) && r.UserId != userId) return Forbid();

        if (r.Status == "Canceled") return Ok(new { ok = true });

        r.Status = "Canceled";
        await _db.SaveChangesAsync();

        await _hub.Clients.All.SendAsync("TableUpdated", new { tableNumber = r.Table.Number });
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

        // Cấu hình khung giờ
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

            if (endUtc <= DateTime.UtcNow) continue; // Quá khứ
            if (booked.Any(r => startUtc < r.EndTime && endUtc > r.StartTime)) continue; // Trùng

            result.Add(new
            {
                startLocal = startLocal.ToString("yyyy-MM-ddTHH:mm"),
                endLocal = endLocal.ToString("yyyy-MM-ddTHH:mm"),
                hours = (int)(endUtc - startUtc).TotalHours
            });
        }
        return Ok(result);
    }

    // ===== HELPER: Chuyển đổi giờ =====
    private static DateTime ParseClientLocalToUtc(DateTime dt)
    {
        var local = dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
        if (dt.Kind == DateTimeKind.Unspecified) local = DateTime.SpecifyKind(dt, DateTimeKind.Local);
        return local.ToUniversalTime();
    }

    // ===== HELPER: HTML EMAIL TEMPLATE (Đã tách ra cho gọn) =====
    private string GetHtmlEmailBody(ReservationEntity entity, TableEntity table, int hours)
    {
        return $@"
        <!DOCTYPE html>
        <html>
        <head>
            <style>
                body {{ font-family: Arial, sans-serif; color: #333; }}
                .container {{ max-width: 600px; margin: 0 auto; border: 1px solid #ddd; border-radius: 8px; overflow: hidden; }}
                .header {{ background: #0f172a; color: #fff; padding: 20px; text-align: center; }}
                .content {{ padding: 20px; }}
                .info {{ width: 100%; border-collapse: collapse; margin: 15px 0; }}
                .info td {{ padding: 10px; border-bottom: 1px solid #eee; }}
                .footer {{ background: #f8f9fa; padding: 15px; text-align: center; font-size: 12px; color: #777; }}
                .badge {{ background: #fff3cd; color: #856404; padding: 4px 10px; border-radius: 12px; font-weight: bold; font-size: 12px; }}
            </style>
        </head>
        <body>
            <div class='container'>
                <div class='header'><h1>ABC RESTAURANT</h1></div>
                <div class='content'>
                    <h2 style='color: #b5924a; margin-top: 0;'>Xác nhận đặt bàn thành công!</h2>
                    <p>Xin chào <b>{entity.CustomerName}</b>,</p>
                    <p>Cảm ơn bạn đã lựa chọn chúng tôi. Dưới đây là thông tin chi tiết đơn đặt bàn của bạn:</p>
                    <table class='info'>
                        <tr><td><b>Mã đơn:</b></td><td>#{entity.Id}</td></tr>
                        <tr><td><b>Bàn số:</b></td><td>{table.Number} ({table.Type})</td></tr>
                        <tr><td><b>Thời gian:</b></td><td>{entity.StartTime.ToLocalTime():HH:mm dd/MM/yyyy}</td></tr>
                        <tr><td><b>Thời lượng:</b></td><td>{hours} tiếng</td></tr>
                        <tr><td><b>SĐT Liên hệ:</b></td><td>{entity.Phone}</td></tr>
                        <tr><td><b>Trạng thái:</b></td><td><span class='badge'>PENDING</span></td></tr>
                    </table>
                    <p>Vui lòng đến đúng giờ. Nếu cần thay đổi, hãy gọi hotline.</p>
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