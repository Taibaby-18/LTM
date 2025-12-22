using Microsoft.AspNetCore.Mvc;

namespace LapTrinhMang.Controllers
{
    public class ContactController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
