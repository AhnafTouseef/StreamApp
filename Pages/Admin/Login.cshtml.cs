using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MovieHub.Configuration;
using MovieHub.Services;

namespace MovieHub.Pages.Admin;

public class LoginModel : PageModel
{
    private readonly AdminAuthService _auth;
    private readonly LoginThrottle _throttle;
    private readonly AdminOptions _opt;
    private readonly ILogger<LoginModel> _log;

    public LoginModel(AdminAuthService auth, LoginThrottle throttle, IOptions<AdminOptions> opt, ILogger<LoginModel> log)
    {
        _auth = auth; _throttle = throttle; _opt = opt.Value; _log = log;
    }

    public bool Configured => _auth.IsConfigured;
    public string? Error { get; private set; }
    public string? ReturnUrl { get; private set; }

    public void OnGet(string? returnUrl) => ReturnUrl = returnUrl;

    public async Task<IActionResult> OnPostAsync(string? username, string? password, string? returnUrl)
    {
        ReturnUrl = returnUrl;
        var key = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (_throttle.IsLocked(key))
        {
            Error = "Too many failed attempts. Try again in a few minutes.";
            return Page();
        }

        if (!_auth.Validate(username, password))
        {
            _throttle.RecordFailure(key);
            _log.LogWarning("Failed admin login from {Address}.", key);
            Error = "Invalid username or password.";
            return Page();
        }

        _throttle.Reset(key);
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, _auth.Username), new Claim(ClaimTypes.Role, "admin") }, AdminAuthDefaults.Scheme);
        await HttpContext.SignInAsync(AdminAuthDefaults.Scheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(Math.Max(1, _opt.SessionHours)) });
        _log.LogInformation("Admin signed in from {Address}.", key);

        // Only local return URLs are honoured (prevents open redirects).
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/admin");
    }
}
