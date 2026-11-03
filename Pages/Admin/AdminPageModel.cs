using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MovieHub.Pages.Admin;

public abstract class AdminPageModel : PageModel
{
    protected void Ok(string message) => TempData["Message"] = message;
    protected void Fail(string message) => TempData["Error"] = message;
}
