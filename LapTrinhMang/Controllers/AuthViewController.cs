using Microsoft.AspNetCore.Mvc;

namespace RestaurantBookingMvc.Controllers;

public class AuthViewController : Controller
{
    public IActionResult Login() => View();
    public IActionResult Register() => View();
    public IActionResult RegisterManager() => View();
}
