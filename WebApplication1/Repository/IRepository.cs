using System.Linq.Expressions;

namespace WebApplication1.Repository
{
    public interface IRepository<T> where T : class
    {
        Task<T> GetAsync(int id);
        Task<IQueryable<T>> GetAll(params Expression<Func<T, object>>[] includeProperties);
        Task AddAsync(T entity);
        Task UpdateAsync(T entity);
        Task DeleteAsync(int id);
    }
}
