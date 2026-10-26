using SwiftGet.Core.Models;

namespace SwiftGet.Engine.Categories;

public interface ICategoryManager
{
    Task InitializeAsync(CancellationToken ct = default);
    CategoryRule ResolveCategory(string fileName, string? contentType = null);
    string ResolveDestinationPath(string fileName, CategoryRule category);
    Task<IReadOnlyList<CategoryRule>> GetCategoriesAsync(CancellationToken ct = default);
    Task SaveCategoryAsync(CategoryRule category, CancellationToken ct = default);
    Task DeleteCategoryAsync(string id, CancellationToken ct = default);
}
