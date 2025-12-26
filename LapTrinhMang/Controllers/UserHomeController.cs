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

            if (!string.IsNullOrEmpty(userEmail))
            {
                var subject = $"[ABC Restaurant] Xác nhận đặt bàn #{entity.Id}";
                var body = $@"
                    <div style='font-family: Arial, sans-serif; color: #333;'>
                        <h2 style='color: #b5924a;'>Cảm ơn {entity.CustomerName} đã đặt bàn!</h2>
                        <p>Chúng tôi đã nhận được yêu cầu của bạn:</p>
                        <ul>
                            <li><b>Mã đơn:</b> #{entity.Id}</li>
                            <li><b>Bàn số:</b> {table.Number} ({table.Type})</li>
                            <li><b>Thời gian:</b> {dto.StartTime:dd/MM/yyyy HH:mm}</li>
                            <li><b>Thời lượng:</b> {dto.Hours} tiếng</li>
                        </ul>
                        <p>Trạng thái hiện tại: <b style='color: orange;'>Chờ duyệt (Pending)</b></p>
                        <p>Vui lòng chờ nhân viên xác nhận(nhớ kiểm tra phần lịch sử trên web nhé).</p>
                        <hr>
                        <small>ABC Restaurant - Hotline: 0123 456 789</small>
                    </div>";

                // Gọi Service gửi mail (Background task)
                _ = _mailService.SendEmailAsync(userEmail, subject, body);
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