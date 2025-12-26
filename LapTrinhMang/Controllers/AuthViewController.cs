using Microsoft.AspNetCore.Mvc;

namespace RestaurantBookingMvc.Controllers;

public class AuthViewController : Controller
{
    public IActionResult Login() => View();
    public IActionResult Register() => View();
    public IActionResult RegisterManager() => View();
    [HttpPost]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("accessToken");
        return RedirectToAction("Index", "Home");
    }
}
