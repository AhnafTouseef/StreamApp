using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StreamApp.Configuration;
using StreamApp.Services;

namespace StreamApp.Pages.Admin;

/// <summary>Admin > Site: what visitors see (hide technical details and hints), the name, logo, favicon, colours and wording of the site.</summary>
public class SiteModel : AdminPageModel
{
    private readonly SiteSettingsService _site;
    private readonly SiteOptions _options;
    public SiteModel(SiteSettingsService site, IOptions<SiteOptions> options) { _site = site; _options = options.Value; }

    // Not validated by MVC: SiteSettings.Normalize() checks and limits every value (blank form fields arrive as null, which Normalize turns into the default).
    [BindProperty, Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever] public SiteSettings Input { get; set; } = new();
    [BindProperty] public IFormFile? LogoUpload { get; set; }
    [BindProperty] public IFormFile? FaviconUpload { get; set; }

    public SiteSettings Current => _site.Current;
    public string StorageDirectory => _site.DirectoryPath;

    public void OnGet() => Input = _site.Current.Clone();

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var problems = new List<string>();
        // The form does not carry the uploaded images; Update() keeps the ones already stored.
        _site.Update(Input);
        await StoreAsync(LogoUpload, BrandImage.Logo, "logo", problems);
        await StoreAsync(FaviconUpload, BrandImage.Favicon, "favicon", problems);
        if (problems.Count > 0) Fail(string.Join(" ", problems) + " The other changes were saved.");
        else Ok("Site settings saved. Every page now uses them.");
        return RedirectToPage();
    }

    private async Task StoreAsync(IFormFile? file, BrandImage kind, string label, List<string> problems)
    {
        if (file == null || file.Length == 0) return;
        var max = kind == BrandImage.Logo ? SiteSettingsService.MaxLogoBytes : SiteSettingsService.MaxFaviconBytes;
        if (file.Length > max) { problems.Add($"The {label} is too large ({file.Length / 1024} KB; the limit is {max / 1024} KB)."); return; }
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, HttpContext.RequestAborted);
        var error = _site.SetImage(kind, ms.ToArray());
        if (error != null) problems.Add($"The {label} was not changed: {error}");
    }

    /// <summary>The one-click button: hides the debugging details and the hints from visitors.</summary>
    public IActionResult OnPostHideAll()
    {
        _site.Change(s => { s.HideTechnicalDetails = true; s.HideHints = true; });
        Ok("Debugging details and hints are now hidden from visitors.");
        return RedirectToPage();
    }

    public IActionResult OnPostShowAll()
    {
        _site.Change(s => { s.HideTechnicalDetails = false; s.HideHints = false; });
        Ok("Debugging details and hints are visible again.");
        return RedirectToPage();
    }

    public IActionResult OnPostRemoveLogo() { _site.RemoveImage(BrandImage.Logo); Ok("Logo removed."); return RedirectToPage(); }
    public IActionResult OnPostRemoveFavicon() { _site.RemoveImage(BrandImage.Favicon); Ok("Favicon removed."); return RedirectToPage(); }

    public IActionResult OnPostReset()
    {
        _site.Reset(_options.Name);
        Ok("The site is back to its default look and wording.");
        return RedirectToPage();
    }
}
