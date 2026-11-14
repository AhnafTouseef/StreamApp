using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace StreamApp.Pages;

[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
    public int Code { get; private set; } = 500;
    public void OnGet(int? code)
    {
        Code = code ?? 500;
        Response.StatusCode = Code;
    }
}
