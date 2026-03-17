namespace BeanBot.Services;

public interface IPunCatalogService
{
    Task<IReadOnlyList<string>> GetPunsAsync(CancellationToken cancellationToken = default);
}
