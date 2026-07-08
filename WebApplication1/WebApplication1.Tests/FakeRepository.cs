using System.Linq.Expressions;
using WebApplication1.Repository;

namespace WebApplication1.Tests;

/// <summary>
/// In-memory <see cref="IRepository{T}"/> used to drive the controllers in tests without a
/// SQL Server / EF Core dependency. It mirrors the relevant behaviour of the real repository:
/// <c>GetAll</c> returns a queryable snapshot of the seeded items and <c>AddAsync</c> appends.
/// </summary>
public class FakeRepository<T> : IRepository<T> where T : class
{
    private readonly List<T> _items;

    public FakeRepository(IEnumerable<T>? seed = null)
    {
        _items = seed?.ToList() ?? new List<T>();
    }

    /// <summary>Direct access to the backing list so tests can append live points mid-run.</summary>
    public List<T> Items => _items;

    public Task AddAsync(T entity)
    {
        _items.Add(entity);
        return Task.CompletedTask;
    }

    public Task<IQueryable<T>> GetAll(params Expression<Func<T, object>>[] includeProperties)
        => Task.FromResult(_items.ToList().AsQueryable());

    public Task<T> GetAsync(int id)
        => Task.FromResult(_items.FirstOrDefault(e => GetId(e) == id)!);

    public Task UpdateAsync(T entity) => Task.CompletedTask;

    public Task DeleteAsync(int id)
    {
        var entity = _items.FirstOrDefault(e => GetId(e) == id);
        if (entity != null) _items.Remove(entity);
        return Task.CompletedTask;
    }

    private static int GetId(T entity)
    {
        var prop = typeof(T).GetProperty("Id");
        return prop != null && prop.PropertyType == typeof(int) ? (int)prop.GetValue(entity)! : -1;
    }
}
