using Emtehani.Domain.Entities;
using Emtehani.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace Emtehani.Infrastructure.Persistence;

/// <summary>
/// Idempotent data upgrade that runs once per application start after the schema upgrade.
/// It never deletes accounts, exams, attempts or results. It only fills new fields and
/// creates the Free plan/default subscription rows required by the new platform.
/// </summary>
public static class PlatformDataUpgrade
{
    public const string FreePlanCode = "free";
    public const string FreeFeaturesSettingKey = "subscription.features.free";
    private const string DefaultFreeFeatures =
        "[\"create_exams\",\"manage_students\",\"review_essays\",\"view_results\",\"student_history\"]";

    public static async Task EnsureAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // 1. Normalise existing names for duplicate detection. Duplicate legacy names are
        //    disambiguated internally (never in the display name) so the unique index can
        //    be created without deleting or renaming any account.
        var users = await db.Users
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var usedStudentNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in users)
        {
            var normalised = IdentityNormalizer.NormalizeName(user.FullName);
            if (string.IsNullOrWhiteSpace(normalised))
                normalised = IdentityNormalizer.NormalizeName(user.Email.Split('@')[0]);
            if (string.IsNullOrWhiteSpace(normalised))
                normalised = "user";

            if (user.Role == "Student")
            {
                var candidate = normalised;
                if (!usedStudentNames.Add(candidate))
                {
                    var suffix = string.IsNullOrWhiteSpace(user.StudentCode)
                        ? user.Id.ToString("N")[..8]
                        : user.StudentCode;
                    candidate = $"{normalised} {suffix}";
                    var counter = 2;
                    while (!usedStudentNames.Add(candidate))
                        candidate = $"{normalised} {suffix}-{counter++}";
                }
                normalised = candidate;
            }

            if (user.NormalizedName != normalised)
                user.NormalizedName = normalised;
            if (user.UpdatedAtUtc == default)
                user.UpdatedAtUtc = user.CreatedAtUtc;
        }

        // 2. Academic year for legacy exams.
        var exams = await db.Exams.ToListAsync(cancellationToken);
        foreach (var exam in exams)
        {
            if (string.IsNullOrWhiteSpace(exam.AcademicYear))
                exam.AcademicYear = AcademicYear.For(exam.CreatedAtUtc);
        }

        // 2-a. Explicit Unlinked status for legacy rows that only had UnlinkedAtUtc set.
        // The history row is preserved; it simply stops counting as an active link.
        var legacyUnlinked = await db.TeacherStudentRequests
            .Where(x => x.UnlinkedAtUtc != null && x.Status == "Accepted")
            .ToListAsync(cancellationToken);
        foreach (var link in legacyUnlinked)
            link.Status = "Unlinked";

        // 2-b. Legacy students never selected a teacher count. Anchor their limit to
        // the number of currently accepted links (minimum 1) so the existing rows cannot
        // bypass the student-teacher limit enforced for new registrations.
        var acceptedLinkCounts = await db.TeacherStudentRequests.AsNoTracking()
            .Where(x => x.Status == "Accepted" && x.UnlinkedAtUtc == null)
            .GroupBy(x => x.StudentId)
            .Select(g => new { StudentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.StudentId, x => x.Count, cancellationToken);
        foreach (var student in users.Where(x => x.Role == "Student" && (x.TeacherCount is null || x.TeacherCount < 1)))
        {
            student.TeacherCount = Math.Max(1, acceptedLinkCounts.GetValueOrDefault(student.Id));
        }

        // 3. Immutable snapshots for legacy attempts (one-time backfill only).
        var usersById = users.ToDictionary(x => x.Id);
        var examsById = exams.ToDictionary(x => x.Id);
        var submissions = await db.Submissions.ToListAsync(cancellationToken);
        foreach (var submission in submissions)
        {
            if (submission.StageAtAttempt is not null
                || submission.GradeAtAttempt is not null
                || submission.AcademicYearAtAttempt is not null)
                continue;
            if (usersById.TryGetValue(submission.StudentId, out var student))
            {
                submission.StageAtAttempt = student.Stage;
                submission.GradeAtAttempt = student.Grade;
            }
            if (examsById.TryGetValue(submission.ExamId, out var exam))
                submission.AcademicYearAtAttempt = exam.AcademicYear;
        }

        // 4. Free plan and configurable features. Existing plan customisations are preserved.
        var freePlan = await db.SubscriptionPlans
            .FirstOrDefaultAsync(x => x.Code == FreePlanCode, cancellationToken);
        if (freePlan is null)
        {
            freePlan = new SubscriptionPlan
            {
                Code = FreePlanCode,
                Name = "الخطة المجانية",
                Description = "الخطة الأساسية المتاحة لكل المعلمين. لا تتضمن أي رسوم أو مدفوعات.",
                IsActive = true,
                IsDefault = true,
                SortOrder = 0,
                FeaturesJson = DefaultFreeFeatures,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.SubscriptionPlans.Add(freePlan);
        }

        var featureSetting = await db.SystemSettings
            .FirstOrDefaultAsync(x => x.Key == FreeFeaturesSettingKey, cancellationToken);
        if (featureSetting is null)
        {
            featureSetting = new SystemSetting
            {
                Key = FreeFeaturesSettingKey,
                Value = freePlan.FeaturesJson,
                UpdatedAtUtc = now
            };
            db.SystemSettings.Add(featureSetting);
        }

        // 5. Existing teachers keep full access: assign the Free plan once, as confirmed,
        //    so the upgrade never blocks an account that was already active.
        var teacherIds = users.Where(x => x.Role == "Teacher").Select(x => x.Id).ToList();
        if (teacherIds.Count > 0)
        {
            var subscribed = await db.TeacherSubscriptions
                .Where(x => teacherIds.Contains(x.TeacherId))
                .Select(x => x.TeacherId)
                .Distinct()
                .ToListAsync(cancellationToken);
            var subscribedSet = subscribed.ToHashSet();
            foreach (var teacher in users.Where(x => x.Role == "Teacher" && !subscribedSet.Contains(x.Id)))
            {
                db.TeacherSubscriptions.Add(new TeacherSubscription
                {
                    TeacherId = teacher.Id,
                    PlanId = freePlan.Id,
                    Status = "Confirmed",
                    StartedAtUtc = teacher.CreatedAtUtc,
                    ConfirmedAtUtc = now,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
