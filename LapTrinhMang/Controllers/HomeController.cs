using Microsoft.AspNetCore.Mvc;

namespace LapTrinhMang.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
