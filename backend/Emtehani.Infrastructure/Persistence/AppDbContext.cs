using Microsoft.EntityFrameworkCore; using Emtehani.Domain.Entities;
namespace Emtehani.Infrastructure.Persistence;
public sealed class AppDbContext(DbContextOptions<AppDbContext> options):DbContext(options) {
 public DbSet<AppUser> Users=>Set<AppUser>(); public DbSet<Exam> Exams=>Set<Exam>(); public DbSet<Question> Questions=>Set<Question>();
 public DbSet<Submission> Submissions=>Set<Submission>(); public DbSet<SubmissionAnswer> Answers=>Set<SubmissionAnswer>(); public DbSet<AuditEvent> AuditEvents=>Set<AuditEvent>();
 protected override void OnModelCreating(ModelBuilder b) {
  b.Entity<AppUser>().HasIndex(x=>x.Email).IsUnique(); b.Entity<Exam>().HasIndex(x=>x.JoinCode).IsUnique();
  b.Entity<Exam>().HasMany(x=>x.Questions).WithOne(x=>x.Exam).HasForeignKey(x=>x.ExamId).OnDelete(DeleteBehavior.Cascade);
  b.Entity<Submission>().HasOne(x=>x.Student).WithMany().HasForeignKey(x=>x.StudentId).OnDelete(DeleteBehavior.Restrict);
  b.Entity<Submission>().HasOne(x=>x.Exam).WithMany().HasForeignKey(x=>x.ExamId).OnDelete(DeleteBehavior.Restrict);
  b.Entity<Submission>().HasMany(x=>x.Answers).WithOne(x=>x.Submission).HasForeignKey(x=>x.SubmissionId).OnDelete(DeleteBehavior.Cascade);
  b.Entity<Submission>().HasIndex(x=>new{x.ExamId,x.StudentId}).HasDatabaseName("IX_Submissions_Exam_Student_InProgress").HasFilter("[Status] = 'InProgress'").IsUnique();
  b.Entity<SubmissionAnswer>().HasOne(x=>x.Question).WithMany().HasForeignKey(x=>x.QuestionId).OnDelete(DeleteBehavior.Restrict);
  b.Entity<SubmissionAnswer>().HasIndex(x=>new{x.SubmissionId,x.QuestionId}).IsUnique();
 }
}
