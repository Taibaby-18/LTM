using LapTrinhMang.Data;
using LapTrinhMang.Dtos;
using LapTrinhMang.Hubs;
using LapTrinhMang.Models;
using LapTrinhMang.Services; // ✅ Thêm namespace Service
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
    private readonly SendMailService _mailService; // ✅ Service gửi mail

    // Inject thêm SendMailService
    public UserHomeController(AppDbContext db, IHubContext<BookingHub> hub, SendMailService mailService)
    {
        _db = db;
        _hub = hub;
        _mailService = mailService;
    }

    // ===== VIEW: TRANG CHỦ ĐẶT BÀN =====
    public IActionResult Index()
    {
        ViewBag.Name = User.FindFirst("name")?.Value ?? "";
        ViewBag.Phone = User.FindFirst("phone")?.Value ?? "";

        // Lấy Email từ Token (nếu có lưu trong claim) để hiển thị lên giao diện (nếu cần)
        // Lưu ý: Token cần có claim "email" lúc tạo thì mới lấy được ở đây
        ViewBag.Email = User.FindFirst("email")?.Value ?? "";

        ViewBag.Role = User.FindFirst(ClaimTypes.Role)?.Value ?? "";
        return View();
    }

    // ===== API: LẤY DANH SÁCH BÀN & TRẠNG THÁI =====
    [HttpGet]
    [Route("api/user/tables")]
    public async Task<IActionResult> GetTables()
    {
        var nowUtc = DateTime.UtcNow;

        // Lấy danh sách bàn
        var tables = await _db.Tables
            .AsNoTracking()
            .OrderBy(t => t.Number)
            .Select(t => new { t.Id, t.Number, t.Capacity, t.Type })
            .ToListAsync();

        // Lấy các đơn đặt bàn chưa hủy và chưa kết thúc
        var list = await _db.Reservations
            .AsNoTracking()
            .Where(r => r.Status != "Canceled" && r.EndTime > nowUtc)
            .Select(r => new
            {
                r.Id,
                r.TableId,
                r.StartTime,
                r.EndTime,
                r.Status,
                r.CustomerName,
                r.Phone
            })
            .ToListAsync();

        // Ghép dữ liệu để trả về
        var result = tables.Select(t =>
        {
            var byTable = list.Where(x => x.TableId == t.Id).ToList();

            // Đơn đang diễn ra (Active)
            var activeNow = byTable
                .Where(r => (r.Status == "Pending" || r.Status == "Approved")
                            && r.StartTime <= nowUtc && nowUtc < r.EndTime)
                .OrderByDescending(r => r.Id)
                .FirstOrDefault();

            // Đơn sắp tới (Upcoming)
            var upcoming = byTable
                .Where(r => (r.Status == "Pending" || r.Status == "Approved")
                            && r.StartTime > nowUtc)
                .OrderBy(r => r.StartTime)
                .FirstOrDefault();

            // Xác định trạng thái bàn
            var status = "Available";
            if (activeNow != null)
                status = activeNow.Status == "Approved" ? "Approved" : "Pending";

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
                currentStartTime = activeNow?.StartTime,
                currentEndTime = activeNow?.EndTime,

                nextReservationId = upcoming?.Id,
                nextCustomer = upcoming?.CustomerName,
                nextPhone = upcoming?.Phone,
                nextStartTime = upcoming?.StartTime,
                nextEndTime = upcoming?.EndTime,
                hasUpcoming = upcoming != null
            };
        });

        return Ok(result);
    }

    // ===== Helper: Convert Local Time -> UTC =====
    private static DateTime ParseClientLocalToUtc(DateTime dt)
    {
        var local = dt.Kind switch
        {
            DateTimeKind.Utc => dt.ToLocalTime(),
            DateTimeKind.Local => dt,
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Local)
        };
        return local.ToUniversalTime();
    }

    // ===== API: TẠO ĐƠN ĐẶT BÀN =====
    [HttpPost]
    [Route("api/user/reservations")]
    public async Task<IActionResult> CreateReservation([FromBody] CreateReservationDto dto)
    {
        // 1. Kiểm tra bàn tồn tại
        var table = await _db.Tables.FirstOrDefaultAsync(t => t.Number == dto.TableNumber);
        if (table is null) return BadRequest(new { message = "Bàn không tồn tại" });

        // 2. Validate thời gian
        if (dto.Hours <= 0 || dto.Hours > 12)
            return BadRequest(new { message = "Thời gian đặt không hợp lệ (1-12 tiếng)" });

        var startUtc = ParseClientLocalToUtc(dto.StartTime);
        var endUtc = startUtc.AddHours(dto.Hours);

        if (startUtc < DateTime.UtcNow.AddMinutes(1))
            return BadRequest(new { message = "Thời gian phải ở tương lai" });

        if (startUtc > DateTime.UtcNow.AddDays(30))
            return BadRequest(new { message = "Không được đặt trước quá 30 ngày" });

        // 3. Kiểm tra trùng lịch
        var overlapped = await _db.Reservations.AnyAsync(r =>
            r.TableId == table.Id &&
            r.Status != "Canceled" &&
            startUtc < r.EndTime && endUtc > r.StartTime);

        if (overlapped) return Conflict(new { message = "Khung giờ này đã có người đặt" });

        // 4. Lấy UserId từ Token
        int? userId = null;
        var sub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        if (int.TryParse(sub, out var id)) userId = id;

        // 5. Tạo Entity Reservation (Không lưu Email ở đây)
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

        // 6. GỬI EMAIL XÁC NHẬN (Tự động tìm email user)
        if (userId != null)
        {
            // Tìm Email trong bảng Users
            var userEmail = await _db.Users
                .Where(u => u.Id == userId)
                .Select(u => u.Email)
                .FirstOrDefaultAsync();

            // ... (đoạn code tìm userEmail phía trên giữ nguyên) ...

            if (!string.IsNullOrEmpty(userEmail))
            {
                var subject = $"✅ Xác nhận đặt bàn #{entity.Id} - ABC Restaurant";

                // 👇 THAY THẾ ĐOẠN BODY CŨ BẰNG ĐOẠN HTML NÀY 👇
                var body = $@"
    <!DOCTYPE html>
    <html>
    <head>
        <style>
            .email-container {{
                font-family: 'Helvetica Neue', Helvetica, Arial, sans-serif;
                line-height: 1.6;
                color: #333;
                max-width: 600px;
                margin: 20px auto;
                border: 1px solid #ddd;
                border-radius: 8px;
                overflow: hidden;
            }}
            .email-header {{
                background-color: #0f172a; /* Màu xanh đậm chủ đạo */
                color: #ffffff;
                padding: 20px;
                text-align: center;
            }}
            .email-header h1 {{
                margin: 0;
                font-size: 24px;
                font-weight: bold;
                letter-spacing: 1px;
            }}
            .email-body {{
                padding: 30px 20px;
                background-color: #ffffff;
            }}
            .email-body h2 {{
                color: #b5924a; /* Màu vàng accent */
                margin-top: 0;
            }}
            .info-table {{
                width: 100%;
                border-collapse: collapse;
                margin: 20px 0;
                font-size: 15px;
            }}
            .info-table td {{
                padding: 12px;
                border-bottom: 1px solid #eee;
            }}
            .info-table td:first-child {{
                font-weight: bold;
                color: #555;
                width: 40%;
            }}
            .status-badge {{
                display: inline-block;
                background-color: #fff3cd;
                color: #856404;
                padding: 6px 12px;
                border-radius: 20px;
                font-weight: bold;
                border: 1px solid #ffeeba;
            }}
            .email-footer {{
                background-color: #f8f9fa;
                color: #777;
                padding: 20px;
                text-align: center;
                font-size: 13px;
                border-top: 1px solid #eee;
            }}
            .email-footer p {{ margin: 5px 0; }}
            .contact-link {{ color: #b5924a; text-decoration: none; }}
        </style>
    </head>
    <body>
        <div class='email-container'>
            <div class='email-header'>
                <h1>ABC RESTAURANT</h1>
            </div>
            <div class='email-body'>
                <h2>Xin chào {entity.CustomerName},</h2>
                <p>Cảm ơn bạn đã lựa chọn ABC Restaurant. Chúng tôi đã nhận được yêu cầu đặt bàn của bạn với các thông tin chi tiết dưới đây:</p>
                
                <table class='info-table'>
                    <tr>
                        <td>Mã đặt bàn:</td>
                        <td style='font-family: monospace; font-size: 16px;'><b>#{entity.Id}</b></td>
                    </tr>
                    <tr>
                        <td>Vị trí bàn:</td>
                        <td>Bàn số <b>{table.Number}</b> <span style='color: #777;'>({table.Type})</span></td>
                    </tr>
                    <tr>
                        <td>Thời gian bắt đầu:</td>
                        <td>{dto.StartTime:HH:mm, ngày dd/MM/yyyy}</td>
                    </tr>
                    <tr>
                        <td>Thời lượng:</td>
                        <td>{dto.Hours} tiếng</td>
                    </tr>
                    <tr>
                        <td>Số điện thoại liên hệ:</td>
                        <td>{entity.Phone}</td>
                    </tr>
                    <tr>
                        <td>Trạng thái hiện tại:</td>
                        <td><span class='status-badge'>⏳ Đang chờ duyệt (Pending)</span></td>
                    </tr>
                </table>
                
                <p>Nhân viên của chúng tôi sẽ sớm kiểm tra và xác nhận đơn đặt bàn của bạn.</p>
                <p>Nếu có bất kỳ thay đổi nào, vui lòng liên hệ với chúng tôi qua hotline để được hỗ trợ nhanh nhất.</p>
                <p style='margin-top: 30px;'>Trân trọng,<br><b>Đội ngũ ABC Restaurant</b></p>
            </div>
            <div class='email-footer'>
                <p>Địa chỉ: 123 Đường ABC, Quận 1, TP.HCM</p>
                <p>Hotline: <a href='tel:0123456789' class='contact-link'>0123 456 789</a> | Email: hello@abcrestaurant.com</p>
                <p>&copy; {DateTime.Now.Year} ABC Restaurant. All rights reserved.</p>
            </div>
        </div>
    </body>
    </html>";

                // Đoạn gửi mail giữ nguyên (nếu đang test thì để await, chạy thật thì bỏ await cho nhanh)
                try
                {
                    await _mailService.SendEmailAsync(userEmail, subject, body);
                }
                catch (Exception ex)
                {
                    // Log lỗi (tùy chọn)
                    Console.WriteLine("Lỗi gửi mail: " + ex.Message);
                }
            }
        }

        // 7. Gửi SignalR realtime cho mọi người cập nhật
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
            status = "Pending", // Bàn chuyển sang màu vàng
            startTime = entity.StartTime,
            endTime = entity.EndTime,
            customerName = entity.CustomerName,
            phone = entity.Phone
        });

        return Ok(new { id = entity.Id, status = entity.Status });
    }

    // ===== API: HỦY ĐẶT BÀN =====
    [HttpPut]
    [Route("api/user/reservations/{id:int}/cancel")]
    public async Task<IActionResult> CancelReservation(int id)
    {
        // Check quyền: Chỉ User chủ đơn mới được hủy (Hoặc Manager)
        // Ở đây đơn giản hóa logic
        var r = await _db.Reservations.Include(x => x.Table).FirstOrDefaultAsync(x => x.Id == id);

        if (r == null) return NotFound();

        // Kiểm tra xem đơn này có phải của User đang đăng nhập không?
        var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        if (int.TryParse(userIdStr, out var userId) && r.UserId != userId)
        {
            return Forbid(); // Không phải đơn của mình thì cấm hủy
        }

        if (r.Status == "Canceled") return Ok(new { ok = true });

        r.Status = "Canceled";
        await _db.SaveChangesAsync();

        // Gửi SignalR cập nhật lại bàn thành Available
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

    // ===== API: LẤY CÁC KHUNG GIỜ CÒN TRỐNG (SLOTS) =====
    [HttpGet]
    [Route("api/user/tables/{tableNumber:int}/slots")]
    public async Task<IActionResult> GetAvailableSlots(int tableNumber, [FromQuery] DateOnly? date)
    {
        var table = await _db.Tables.AsNoTracking().FirstOrDefaultAsync(t => t.Number == tableNumber);
        if (table == null) return NotFound(new { message = "Table not found" });

        var day = date ?? DateOnly.FromDateTime(DateTime.Now);
        var localDayStart = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local);

        var dayStartUtc = localDayStart.ToUniversalTime();
        var dayEndUtc = localDayStart.AddDays(1).ToUniversalTime();

        var booked = await _db.Reservations
            .AsNoTracking()
            .Where(r => r.TableId == table.Id
                        && r.Status != "Canceled"
                        && r.StartTime < dayEndUtc
                        && r.EndTime > dayStartUtc)
            .Select(r => new { r.StartTime, r.EndTime })
            .ToListAsync();

        // Cấu hình khung giờ theo loại bàn
        (TimeSpan start, TimeSpan end)[] slotTemplates = table.Type switch
        {
            "VIP" => new[]
            {
                (new TimeSpan(17,0,0), new TimeSpan(19,0,0)),
                (new TimeSpan(19,0,0), new TimeSpan(21,0,0)),
            },
            "VVIP" => new[]
            {
                (new TimeSpan(18,0,0), new TimeSpan(21,0,0)),
            },
            _ => new[] // Normal
            {
                (new TimeSpan(17,0,0), new TimeSpan(18,0,0)),
                (new TimeSpan(18,0,0), new TimeSpan(19,0,0)),
                (new TimeSpan(19,0,0), new TimeSpan(20,0,0)),
                (new TimeSpan(20,0,0), new TimeSpan(21,0,0)), // Thêm slot tối muộn
            }
        };

        var result = new List<object>();

        for (int i = 0; i < slotTemplates.Length; i++)
        {
            var s = slotTemplates[i];
            var startLocal = DateTime.SpecifyKind(localDayStart.Date + s.start, DateTimeKind.Local);
            var endLocal = DateTime.SpecifyKind(localDayStart.Date + s.end, DateTimeKind.Local);

            var slotStartUtc = startLocal.ToUniversalTime();
            var slotEndUtc = endLocal.ToUniversalTime();

            // Bỏ qua quá khứ
            if (slotEndUtc <= DateTime.UtcNow) continue;

            // Kiểm tra trùng
            bool isTaken = booked.Any(r => slotStartUtc < r.EndTime && slotEndUtc > r.StartTime);
            if (isTaken) continue;

            var hours = (int)Math.Ceiling((slotEndUtc - slotStartUtc).TotalHours);

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

    // ===== VIEW: LỊCH SỬ ĐẶT BÀN =====
    [HttpGet]
    public async Task<IActionResult> BookingHistory()
    {
        // 1. Lấy ID an toàn
        var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;

        if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out int userId))
        {
            return RedirectToAction("Index");
        }

        // 2. Truy vấn Database (Chỉ lấy đơn của User này)
        var list = await _db.Reservations
            .AsNoTracking()
            .Include(r => r.Table)
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return View(list);
    }
}