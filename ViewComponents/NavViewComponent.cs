using Microsoft.AspNetCore.Mvc;
using StreamApp.Services;

namespace StreamApp.ViewComponents;

/// <summary>Primary navigation built from the category table, so a category created in the admin panel appears immediately.</summary>
public class NavViewComponent : ViewComponent
{
    private readonly CategoryService _categories;
    public NavViewComponent(CategoryService categories) => _categories = categories;

    public async Task<IViewComponentResult> InvokeAsync() =>
        View(await _categories.GetNavigationAsync(HttpContext.RequestAborted));
}
