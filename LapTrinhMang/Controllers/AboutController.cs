using Microsoft.AspNetCore.Mvc;

namespace LapTrinhMang.Controllers
{
    public class AboutController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
