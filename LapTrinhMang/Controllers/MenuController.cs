using Microsoft.AspNetCore.Mvc;

namespace LapTrinhMang.Controllers
{
    public class MenuController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
