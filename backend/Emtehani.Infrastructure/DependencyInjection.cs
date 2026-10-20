using Emtehani.Application.Abstractions; using Emtehani.Infrastructure.Persistence; using Emtehani.Infrastructure.Security; using Microsoft.EntityFrameworkCore; using Microsoft.Extensions.Configuration; using Microsoft.Extensions.DependencyInjection;
namespace Emtehani.Infrastructure;
public static class DependencyInjection {
 public static IServiceCollection AddInfrastructure(this IServiceCollection services,IConfiguration config) {
  services.AddDbContext<AppDbContext>(o=>o.UseSqlServer(config.GetConnectionString("DefaultConnection")));
  services.AddScoped<ITokenService,TokenService>(); return services;
 }
}
