using LapTrinhMang.Data;
using LapTrinhMang.Dtos;
using LapTrinhMang.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace LapTrinhMang.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly PasswordHasher<AppUser> _hasher = new();

    public AuthController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterDto dto)
    {
        var phone = (dto.Phone ?? "").Trim();
        var fullName = (dto.FullName ?? "").Trim();

        if (phone.Length < 8) return BadRequest(new { message = "Phone invalid" });
        if (dto.Password is null || dto.Password.Length < 4)
            return BadRequest(new { message = "Password too short" });

        if (await _db.Users.AnyAsync(x => x.Phone == phone))
            return Conflict(new { message = "Phone already exists" });

        var user = new AppUser
        {
            Phone = phone,
            FullName = fullName,
            Role = "User"
        };

        user.PasswordHash = _hasher.HashPassword(user, dto.Password);

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return Ok(new { user.Id, user.Phone, user.FullName, user.Role });
    }

    [HttpPost("register-manager")]
    public async Task<IActionResult> RegisterManager(RegisterManagerDto dto)
    {
        var expected = _config["Manager:RegisterCode"];
        if (dto.RegisterCode != expected)
            return Unauthorized(new { message = "Invalid manager register code" });

        var phone = (dto.Phone ?? "").Trim();
        var fullName = (dto.FullName ?? "").Trim();

        if (phone.Length < 8) return BadRequest(new { message = "Phone invalid" });
        if (dto.Password is null || dto.Password.Length < 4)
            return BadRequest(new { message = "Password too short" });

        if (await _db.Users.AnyAsync(x => x.Phone == phone))
            return Conflict(new { message = "Phone already exists" });

        var user = new AppUser
        {
            Phone = phone,
            FullName = fullName,
            Role = "Manager"
        };

        user.PasswordHash = _hasher.HashPassword(user, dto.Password);

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return Ok(new { user.Id, user.Phone, user.FullName, user.Role });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginDto dto)
    {
        var phone = (dto.Phone ?? "").Trim();
        var user = await _db.Users.FirstOrDefaultAsync(x => x.Phone == phone);

        if (user is null) return Unauthorized(new { message = "Invalid credentials" });

        var verify = _hasher.VerifyHashedPassword(user, user.PasswordHash, dto.Password ?? "");
        if (verify == PasswordVerificationResult.Failed)
            return Unauthorized(new { message = "Invalid credentials" });

        var token = CreateJwt(user);

        // ✅ set cookie HttpOnly để MVC tự gửi token mỗi request
        Response.Cookies.Append("accessToken", token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true, // đang HTTPS
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddMinutes(120)
        });
        //ok
        return Ok(new
        {
            role = user.Role,
            user = new { user.Id, user.Phone, user.FullName }
        });
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("accessToken");
        return Ok(new { message = "Logged out" });
    }

    private string CreateJwt(AppUser user)
    {
        var jwt = _config.GetSection("Jwt");
        var key = jwt["Key"]!;
        var issuer = jwt["Issuer"]!;
        var audience = jwt["Audience"]!;
        var expireMinutes = int.TryParse(jwt["ExpireMinutes"], out var m) ? m : 120;

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var creds = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.Role, user.Role),
            new("phone", user.Phone),
            new("name", user.FullName)
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expireMinutes),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
