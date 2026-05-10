using System.Reflection;
using Microsoft.EntityFrameworkCore;
using StudyScheduler.Bot.Core.Entities;

namespace StudyScheduler.Bot.Core;

public class AppDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Student> Students { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }
}