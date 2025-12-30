using LapTrinhMang.Data;
using LapTrinhMang.Hubs;
using LapTrinhMang.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
var builder = WebApplication.CreateBuilder(args);

//thêm để host
builder.WebHost.UseUrls("http://0.0.0.0:5000");

// MVC + API
builder.Services.AddControllersWithViews();

// EF Core
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

// JWT Auth (đọc token từ Cookie accessToken)
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwt = builder.Configuration.GetSection("Jwt");

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,

            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwt["Key"]!)
            )
        };

        // ✅ QUAN TRỌNG: lấy token từ cookie để MVC [Authorize] tự nhận
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Cookies["accessToken"];
                if (!string.IsNullOrEmpty(token))
                    context.Token = token;

                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddSignalR();
builder.Services.AddAuthorization();
// Nhớ thêm using LapTrinhMang.Services;
builder.Services.AddTransient<SendMailService>();

var app = builder.Build();

// Pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

//tắt tạm
//app.UseHttpsRedirection();


app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Trang mặc định vào Login
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapControllers(); // API controllers (AuthController)
app.MapHub<LapTrinhMang.Hubs.BookingHub>("/hubs/booking");
app.MapHub<ChatHub>("/hubs/chat");
app.Run();

//mở tường lửa
//netsh advfirewall firewall add rule name="ASP.NET Core 5000" dir=in action=allow protocol=TCP localport=5000
