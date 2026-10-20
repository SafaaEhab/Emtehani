using System.Net;
using System.Net.Mail;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Emtehani.Infrastructure;
using Emtehani.Infrastructure.Persistence;
using Emtehani.Domain.Entities;
using Emtehani.Application.Contracts;
using System.Threading.RateLimiting;
using System.Security.Cryptography;
using Emtehani.Domain.Services;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddCors(o => o.AddPolicy("frontend", p => p.WithOrigins(builder.Configuration["FrontendOrigin"] ?? "http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));
var jwtKey = builder.Configuration["Jwt:Key"] ?? Environment.GetEnvironmentVariable("JWT_KEY");
if (string.IsNullOrWhiteSpace(jwtKey))
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException("Jwt:Key or JWT_KEY must be configured outside Development.");
    jwtKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
}
if (jwtKey.Length < 32) throw new InvalidOperationException("Jwt:Key must contain at least 32 characters.");
builder.Configuration["Jwt:Key"] = jwtKey;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => { o.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "Emtehani", ValidateAudience = true, ValidAudience = builder.Configuration["Jwt:Audience"] ?? "Emtehani.Client", ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)), ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30) }; });
builder.Services.AddAuthorization(); builder.Services.AddEndpointsApiExplorer(); builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    static RateLimitPartition<string> PerIp(HttpContext context, int permitLimit) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    o.AddPolicy("api", context => PerIp(context, 120));
    o.AddPolicy("auth", context => PerIp(context, 60));
    o.AddPolicy("join", context => PerIp(context, 30));
});
builder.Services.AddProblemDetails(); var app = builder.Build(); var finalizeLock = new SemaphoreSlim(1, 1);
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var isConcurrency = exception is DbUpdateConcurrencyException;
    if (isConcurrency)
        app.Logger.LogWarning(exception, "Submission save concurrency conflict. TraceId: {TraceId}", context.TraceIdentifier);
    else
        app.Logger.LogError(exception, "Unhandled API exception. TraceId: {TraceId}", context.TraceIdentifier);

    context.Response.StatusCode = isConcurrency ? StatusCodes.Status409Conflict : StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/problem+json";
    await context.Response.WriteAsJsonAsync(new
    {
        type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
        title = isConcurrency
            ? "لم يتم اعتماد التسليم بسبب تعارض أثناء حفظ الإجابات. إجاباتك المحفوظة لم تُحذف؛ انتظر لحظة ثم حاول التسليم مرة أخرى."
            : "تعذر إكمال العملية بسبب مشكلة مؤقتة في الخادم. احتفظ بالصفحة مفتوحة وحاول مرة أخرى. إذا تكررت المشكلة، أرسل رقم البلاغ للدعم.",
        message = isConcurrency
            ? "حدث تعارض أثناء حفظ إجابات الامتحان. انتظر لحظة ثم أعد المحاولة؛ لم يتم تأكيد التسليم بعد."
            : "حدثت مشكلة تقنية مؤقتة أثناء معالجة الطلب.",
        status = context.Response.StatusCode,
        traceId = context.TraceIdentifier
    });
}));
app.UseSwagger(); app.UseSwaggerUI(); app.UseCors("frontend"); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapGet("/api", () => Results.Ok(new { service = "Emtehani API", version = "1.0", docs = "/swagger" }));
string Hash(string password, string salt) => Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt), 120000, HashAlgorithmName.SHA256, 32));
string Salt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
string UserId(ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
async Task Audit(AppDbContext db, Guid? actor, string action, string type, string? id, string? details = null) { db.AuditEvents.Add(new AuditEvent { ActorId = actor, Action = action, EntityType = type, EntityId = id, Details = details }); await db.SaveChangesAsync(); }
async Task<bool> SendVerificationEmail(AppUser user, string verifyToken)
{
    var smtpHost = builder.Configuration["Email:SmtpHost"];
    if (string.IsNullOrWhiteSpace(smtpHost)) return false;
    var verifyUrl = $"{(builder.Configuration["PublicApiUrl"] ?? "http://localhost:5000").TrimEnd('/')}/api/auth/verify-email?token={Uri.EscapeDataString(verifyToken)}";
    try
    {
        using var mail = new MailMessage(builder.Configuration["Email:From"] ?? "no-reply@localhost", user.Email, "تأكيد البريد الإلكتروني - امتحاني",
            $"<div dir='rtl' style='font-family:Arial'><h2>أهلاً {WebUtility.HtmlEncode(user.FullName)}</h2><p>لتفعيل حسابك، اضغط على الرابط التالي:</p><p><a href='{verifyUrl}'>تأكيد البريد الإلكتروني</a></p><p>الرابط صالح لمدة 24 ساعة.</p></div>") { IsBodyHtml = true };
        using var client = new SmtpClient(smtpHost, int.TryParse(builder.Configuration["Email:SmtpPort"], out var port) ? port : 587) { EnableSsl = bool.TryParse(builder.Configuration["Email:EnableSsl"], out var ssl) && ssl };
        var username = builder.Configuration["Email:Username"]; var password = builder.Configuration["Email:Password"];
        if (!string.IsNullOrWhiteSpace(username)) client.Credentials = new NetworkCredential(username, password);
        await client.SendMailAsync(mail);
        return true;
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Verification email delivery failed.");
        return false;
    }
}
async Task FinalizeExpired(AppDbContext db)
{
    const int FinalizeGraceMinutes = 5;
    await finalizeLock.WaitAsync();
    try
    {
        var now = DateTime.UtcNow;
        var inProgress = await db.Submissions.Include(x => x.Answers).Include(x => x.Exam).Where(x => x.Status == "InProgress").ToListAsync();
        var expired = inProgress.Where(x => x.Exam is not null && ExamGrader.IsExpired(x, x.Exam.DurationMinutes + FinalizeGraceMinutes, now)).ToList();
        if (expired.Count == 0) return;
        var examIds = expired.Select(x => x.ExamId).Distinct().ToArray();
        var questions = await db.Questions.Where(x => examIds.Contains(x.ExamId)).ToListAsync();
        foreach (var submission in expired)
        {
            var examQuestions = questions.Where(x => x.ExamId == submission.ExamId).ToList();
            ExamGrader.Grade(submission, examQuestions);
            foreach (var answer in submission.Answers.Where(x => x.Id == Guid.Empty)) db.Answers.Add(answer);
            submission.SubmittedAtUtc = ExamGrader.Deadline(submission, submission.Exam!.DurationMinutes);
        }
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); }
    }
    finally { finalizeLock.Release(); }
}
app.MapPost("/api/auth/register", async (RegisterRequest req, AppDbContext db, Emtehani.Application.Abstractions.ITokenService tokens) =>
{
  if (req is null || string.IsNullOrWhiteSpace(req.FullName) || string.IsNullOrWhiteSpace(req.Email) ||
      string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8 || !req.Email.Contains('@'))
    return Results.BadRequest(new { message = "أدخل الاسم والبريد الصحيح وكلمة مرور 8 أحرف على الأقل." });

  var email = req.Email.Trim().ToLowerInvariant();
  if (await db.Users.AnyAsync(x => x.Email == email))
    return Results.Conflict(new { message = "البريد مستخدم بالفعل." });

  // Registration intentionally supports both roles without a teacher invite code.
  var role = string.Equals(req.Role, "Teacher", StringComparison.OrdinalIgnoreCase) ? "Teacher" : "Student";
  var salt = Salt();
  var smtpHost = builder.Configuration["Email:SmtpHost"];
  if (string.IsNullOrWhiteSpace(smtpHost))
  {
    // Development fallback: without SMTP, confirm the account automatically so the app stays usable locally.
    if (!app.Environment.IsDevelopment())
      return Results.Problem(statusCode: 503, title: "خدمة تأكيد البريد غير مهيأة. يرجى التواصل مع إدارة المنصة.", extensions: new Dictionary<string, object?> { ["message"] = "خدمة البريد الإلكتروني غير متاحة حاليًا." });
    db.Users.Add(new AppUser { FullName = req.FullName.Trim(), Email = email, Role = role, EmailConfirmed = true, PasswordHash = salt + ":" + Hash(req.Password, salt) });
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "تم إنشاء الحساب وتفعيله تلقائيًا (بيئة تطوير بدون SMTP). يمكنك تسجيل الدخول الآن." });
  }
  var verifyToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
  var user = new AppUser { FullName = req.FullName.Trim(), Email = email, Role = role, EmailConfirmed = false, EmailVerificationToken = verifyToken, EmailVerificationExpiresUtc = DateTime.UtcNow.AddHours(24), PasswordHash = salt + ":" + Hash(req.Password, salt) };
  db.Users.Add(user);
  await db.SaveChangesAsync();
  if (!await SendVerificationEmail(user, verifyToken))
  {
    db.Users.Remove(user); await db.SaveChangesAsync();
    return Results.Problem(statusCode: 503, title: "تعذر إرسال رسالة التأكيد.", extensions: new Dictionary<string, object?> { ["message"] = "لم نتمكن من إرسال رسالة التأكيد. تحقق من إعدادات البريد وحاول مرة أخرى." });
  }
  return Results.Ok(new { message = "تم إنشاء الحساب. افتح بريدك الإلكتروني واضغط على رابط التأكيد قبل تسجيل الدخول." });
}).RequireRateLimiting("auth");
app.MapGet("/api/auth/verify-email", async (string token, AppDbContext db) =>
{
  if (string.IsNullOrWhiteSpace(token)) return Results.BadRequest("رابط التأكيد غير صالح.");
  var user = await db.Users.FirstOrDefaultAsync(x => x.EmailVerificationToken == token);
  if (user is null) return Results.BadRequest("رابط التأكيد غير صالح أو سبق استخدامه.");
  if (user.EmailVerificationExpiresUtc < DateTime.UtcNow) return Results.BadRequest("انتهت صلاحية رابط التأكيد. اطلب إعادة إرسال رسالة التأكيد.");
  user.EmailConfirmed = true; user.EmailVerificationToken = null; user.EmailVerificationExpiresUtc = null;
  await db.SaveChangesAsync();
  return Results.Content("<html lang='ar' dir='rtl'><meta charset='utf-8'><body style='font-family:Arial;text-align:center;padding:4rem'><h1>تم تأكيد بريدك الإلكتروني بنجاح</h1><p>يمكنك الآن العودة إلى منصة امتحاني وتسجيل الدخول.</p></body></html>", "text/html; charset=utf-8");
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/resend-verification", async (ResendVerificationRequest req, AppDbContext db) =>
{
    var message = "إذا كان البريد مسجلاً وغير مؤكد، فسنرسل رابط تأكيد جديداً.";
    if (req is null || string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@')) return Results.BadRequest(new { message = "أدخل بريداً إلكترونياً صحيحاً." });
    var email = req.Email.Trim().ToLowerInvariant();
    var user = await db.Users.FirstOrDefaultAsync(x => x.Email == email && !x.EmailConfirmed);
    if (user is not null)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        user.EmailVerificationToken = token;
        user.EmailVerificationExpiresUtc = DateTime.UtcNow.AddHours(24);
        await db.SaveChangesAsync();
        await SendVerificationEmail(user, token);
    }
    return Results.Ok(new { message });
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/login", async (LoginRequest req, AppDbContext db, Emtehani.Application.Abstractions.ITokenService tokens) =>
{
  if (req is null || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrEmpty(req.Password))
    return Results.BadRequest(new { message = "أدخل البريد الإلكتروني وكلمة المرور." });
  var email = req.Email.Trim().ToLowerInvariant();
  var user = await db.Users.SingleOrDefaultAsync(x => x.Email == email);
  if (user is null) return Results.Unauthorized();
  var parts = user.PasswordHash.Split(':');
  if (parts.Length != 2 || Hash(req.Password, parts[0]) != parts[1]) return Results.Unauthorized();
  if (!user.EmailConfirmed) return Results.Json(new { message = "لازم تأكد بريدك الإلكتروني الأول. افتح رسالة التأكيد اللي وصلتك على بريدك ثم جرّب تسجيل الدخول." }, statusCode: StatusCodes.Status403Forbidden);
  var t = tokens.Create(user);
  return Results.Ok(new AuthResponse(t.Token, user.FullName, user.Email, user.Role, t.ExpiresAtUtc));
}).RequireRateLimiting("auth");
app.MapGet("/api/auth/me", async (ClaimsPrincipal p, AppDbContext db) =>
{
    var userId = p.FindFirstValue(ClaimTypes.NameIdentifier);

    if (!Guid.TryParse(userId, out var id))
        return Results.Unauthorized();

    var user = await db.Users
        .Where(x => x.Id == id)
        .Select(x => new
        {
            x.Id,
            x.FullName,
            x.Email,
            x.Role
        })
        .FirstOrDefaultAsync();

    return user is null
        ? Results.NotFound()
        : Results.Ok(user);
})
.RequireAuthorization();


app.MapPost("/api/exams", async (ExamCreateRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
  if (req is null || req.Questions is null || req.Questions.Count == 0 || req.DurationMinutes < 1 || req.DurationMinutes > 300 || req.MaxAttempts < 1 || req.MaxAttempts > 20 || string.IsNullOrWhiteSpace(req.Title))
    return Results.BadRequest(new { message = "بيانات الامتحان غير مكتملة أو المدة خارج النطاق." });
  var validTypes = new[] { "MultipleChoice", "TrueFalse", "ShortAnswer", "Essay" };
  if (req.Questions.Any(q => q is null || string.IsNullOrWhiteSpace(q.Text) || q.Points <= 0 || string.IsNullOrWhiteSpace(q.Type) || !validTypes.Contains(q.Type) ||
    (q.Type == "MultipleChoice" && ((q.Options?.Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0) < 2 || string.IsNullOrWhiteSpace(q.CorrectAnswer) || !(q.Options ?? []).Contains(q.CorrectAnswer, StringComparer.OrdinalIgnoreCase))) ||
    (q.Type == "TrueFalse" && !new[] { "صح", "خطأ" }.Contains(q.CorrectAnswer ?? "")))) return Results.BadRequest(new { message = "راجع نصوص الأسئلة والدرجات والاختيارات والإجابات الصحيحة." });
  if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
  if (!Guid.TryParse(UserId(p), out var uid)) return Results.Unauthorized();
  var code = RandomNumberGenerator.GetInt32(100000, 1_000_000).ToString(); while (await db.Exams.AnyAsync(x => x.JoinCode == code)) code = RandomNumberGenerator.GetInt32(100000, 1_000_000).ToString();
  var e = new Exam { TeacherId = uid, Title = req.Title.Trim(), Description = req.Description, DurationMinutes = req.DurationMinutes, MaxAttempts = Math.Clamp(req.MaxAttempts, 1, 20), AllowResumeAfterFocusLoss = req.AllowResumeAfterFocusLoss, JoinCode = code, Questions = req.Questions.Select((q, i) => new Question { Text = q.Text, Type = q.Type, OptionsJson = JsonSerializer.Serialize(q.Options ?? []), CorrectAnswer = q.CorrectAnswer, Points = q.Points, SortOrder = i }).ToList() };
  db.Exams.Add(e); await db.SaveChangesAsync(); await Audit(db, uid, "exam.created", "Exam", e.Id.ToString()); return Results.Created($"/api/exams/{e.Id}", new { e.Id, e.Title, e.Description, e.DurationMinutes, e.JoinCode, e.IsPublished, questionCount = e.Questions.Count });
}).RequireAuthorization().RequireRateLimiting("api");
app.MapGet("/api/exams", async (ClaimsPrincipal p, AppDbContext db, string? search, int page = 1, int pageSize = 10) =>
{
  page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100); var q = db.Exams.AsNoTracking().AsQueryable();

var userId = p.FindFirstValue(ClaimTypes.NameIdentifier);

if (p.FindFirstValue(ClaimTypes.Role) == "Teacher")
{
    if (!Guid.TryParse(userId, out var teacherId))
        return Results.Unauthorized();

    q = q.Where(x => x.TeacherId == teacherId);
}
else
{
    q = q.Where(x => x.IsPublished);
}
  if (!string.IsNullOrWhiteSpace(search)) q = q.Where(x => x.Title.Contains(search));
  var total = await q.CountAsync(); var canSeeJoinCode = p.FindFirstValue(ClaimTypes.Role) == "Teacher";
  var items = await q.OrderByDescending(x => x.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).Select(x => new { x.Id, x.Title, x.Description, x.DurationMinutes, x.MaxAttempts, x.AllowResumeAfterFocusLoss, TeacherName = db.Users.Where(u => u.Id == x.TeacherId).Select(u => u.FullName).FirstOrDefault(), JoinCode = canSeeJoinCode ? x.JoinCode : null, x.IsPublished, x.CreatedAtUtc, QuestionCount = x.Questions.Count }).ToListAsync();
  return Results.Ok(new { items, page, pageSize, total });
}).RequireAuthorization();
app.MapPut("/api/exams/{id:guid}/publish", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher")
        return Results.Forbid();
    if (!Guid.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var teacherId))
        return Results.Unauthorized();
    var exam = await db.Exams.FirstOrDefaultAsync(x => x.Id == id && x.TeacherId == teacherId);
    if (exam is null)
        return Results.NotFound();
    exam.IsPublished = !exam.IsPublished;
    await db.SaveChangesAsync();
    await Audit(db, teacherId, exam.IsPublished ? "exam.published" : "exam.unpublished", "Exam", id.ToString());
    return Results.Ok(new { exam.Id, exam.IsPublished, exam.JoinCode });
}).RequireAuthorization();

app.MapGet("/api/exams/join/{code}", async (string code, AppDbContext db) => { var e = await db.Exams.Include(x => x.Questions).FirstOrDefaultAsync(x => x.JoinCode == code && x.IsPublished); if (e is null) return Results.NotFound(); return Results.Ok(new { e.Id, e.Title, e.Description, e.DurationMinutes, Questions = e.Questions.OrderBy(q => q.SortOrder).Select(q => new { q.Id, q.Text, q.Type, Options = q.Type == "TrueFalse" ? new[] { "صح", "خطأ" } : JsonSerializer.Deserialize<string[]>(q.OptionsJson), q.Points }) }); }).RequireAuthorization().RequireRateLimiting("join");

app.MapPost("/api/exams/{id:guid}/start", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
  await FinalizeExpired(db);
  if (!Guid.TryParse(UserId(p), out var uid)) return Results.Unauthorized();
  var e = await db.Exams.Include(x => x.Questions).FirstOrDefaultAsync(x => x.Id == id && x.IsPublished); if (e is null) return Results.NotFound();
  var existing = await db.Submissions.FirstOrDefaultAsync(s => s.ExamId == id && s.StudentId == uid && s.Status == "InProgress");
  if (existing is not null)
  {
    if (!e.AllowResumeAfterFocusLoss && existing.FocusLossDetected) return Results.Conflict(new { message = "تم إيقاف استكمال المحاولة بعد مغادرة الامتحان وفقاً لإعدادات المدرس. تواصل مع المدرس إذا كان الخروج بسبب ظرف طارئ." });
    return Results.Ok(new { existing.Id, existing.StartedAtUtc, e.DurationMinutes });
  }
  var attemptsUsed = await db.Submissions.CountAsync(s => s.ExamId == id && s.StudentId == uid && s.Status != "InProgress" && !s.IsExceptionAttempt);
  var attemptNumber = await db.Submissions.CountAsync(s => s.ExamId == id && s.StudentId == uid) + 1;
  if (attemptsUsed >= e.MaxAttempts) return Results.Conflict(new { message = "لقد استنفدت عدد المحاولات المتاحة لهذا الامتحان." });
  var sub = new Submission { ExamId = id, StudentId = uid, AttemptNumber = attemptNumber, MaxScore = e.Questions.Sum(q => q.Points) };
  try
  {
    db.Submissions.Add(sub);
    await db.SaveChangesAsync();
  }
  catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
  {
    db.ChangeTracker.Clear();
    var raced = await db.Submissions.FirstOrDefaultAsync(s => s.ExamId == id && s.StudentId == uid && s.Status == "InProgress");
    return raced is null
      ? Results.Conflict(new { message = "تعذر بدء المحاولة بسبب محاولة أخرى قيد التنفيذ. أعد المحاولة بعد لحظة." })
      : Results.Ok(new { raced.Id, raced.StartedAtUtc, e.DurationMinutes });
  }
  await Audit(db, uid, "submission.started", "Submission", sub.Id.ToString());
  return Results.Ok(new { sub.Id, sub.StartedAtUtc, e.DurationMinutes });
}).RequireAuthorization().RequireRateLimiting("api");
app.MapPut("/api/submissions/{id:guid}/answers", async (Guid id, SubmitRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
  await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
  if (!Guid.TryParse(UserId(p), out var uid)) return Results.Unauthorized();
  var sub = await db.Submissions.Include(s => s.Answers).FirstOrDefaultAsync(s => s.Id == id && s.StudentId == uid && s.Status == "InProgress");
  if (sub is null) return Results.NotFound();
  var duration = await db.Exams.Where(e => e.Id == sub.ExamId).Select(e => e.DurationMinutes).FirstOrDefaultAsync();
  if (DateTime.UtcNow >= sub.StartedAtUtc.AddMinutes(duration)) return Results.BadRequest(new { message = "انتهى وقت الامتحان؛ تم إيقاف حفظ الإجابات الجديدة." });
  if (req.FocusLossDetected && !sub.FocusLossDetected) sub.FocusLossCount++; sub.FocusLossDetected |= req.FocusLossDetected;
  var validQuestionIds = await db.Questions.Where(q => q.ExamId == sub.ExamId).Select(q => q.Id).ToListAsync();
  foreach (var a in req.Answers.Where(a => validQuestionIds.Contains(a.QuestionId)).GroupBy(a => a.QuestionId).Select(g => g.Last()))
  {
    var answer = sub.Answers.FirstOrDefault(x => x.QuestionId == a.QuestionId);
    if (answer is null)
    {
      var created = new SubmissionAnswer { SubmissionId = sub.Id, QuestionId = a.QuestionId, AnswerText = a.AnswerText ?? "" };
      sub.Answers.Add(created); db.Answers.Add(created);
    }
    else answer.AnswerText = a.AnswerText ?? "";
  }
  await db.SaveChangesAsync(); await tx.CommitAsync(); return Results.Ok(new { saved = true, sub.FocusLossDetected, serverUtc = DateTime.UtcNow });
}).RequireAuthorization();
app.MapPost("/api/submissions/{id:guid}/submit", async (Guid id, SubmitRequest? req, ClaimsPrincipal p, AppDbContext db) =>
{
  if (!Guid.TryParse(UserId(p), out var uid)) return Results.Unauthorized();
  await using var tx = await db.Database.BeginTransactionAsync();
  var claimed = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Submissions SET Status = 'Processing' WHERE Id = {id} AND StudentId = {uid} AND Status = 'InProgress'");
  if (claimed == 0)
  {
    var alreadySubmitted = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.StudentId == uid && s.Status != "InProgress" && s.Status != "Processing");
    if (alreadySubmitted is not null) return Results.Ok(new { alreadySubmitted.Id, alreadySubmitted.Score, alreadySubmitted.MaxScore, alreadySubmitted.Status });
    return Results.NotFound();
  }
  var sub = await db.Submissions.Include(s => s.Answers).FirstOrDefaultAsync(s => s.Id == id && s.StudentId == uid);
  if (sub is null) return Results.NotFound();
  var duration = await db.Exams.Where(e => e.Id == sub.ExamId).Select(e => e.DurationMinutes).FirstOrDefaultAsync();
  var deadline = ExamGrader.Deadline(sub, duration);
  var isLate = DateTime.UtcNow >= deadline;
  var qs = await db.Questions.Where(q => q.ExamId == sub.ExamId).ToListAsync();
  // Accept the client's latest draft only before the deadline. A late request must not change answers.
  if (!isLate)
  {
    var validQuestionIds = qs.Select(q => q.Id).ToHashSet();
    foreach (var input in (req?.Answers ?? new List<AnswerInput>()).Where(a => validQuestionIds.Contains(a.QuestionId)).GroupBy(a => a.QuestionId).Select(g => g.Last()))
    {
      var existing = sub.Answers.FirstOrDefault(a => a.QuestionId == input.QuestionId);
      if (existing is null)
      {
        sub.Answers.Add(new SubmissionAnswer { Id = Guid.Empty, SubmissionId = sub.Id, QuestionId = input.QuestionId, AnswerText = input.AnswerText ?? "" });
      }
      else
        existing.AnswerText = input.AnswerText ?? "";
    }
    if (req?.FocusLossDetected == true && !sub.FocusLossDetected) sub.FocusLossCount++;
    if (req?.FocusLossDetected == true) sub.FocusLossDetected = true;
  }
  ExamGrader.Grade(sub, qs);
  foreach (var answer in sub.Answers.Where(x => x.Id == Guid.Empty)) db.Answers.Add(answer);
  sub.SubmittedAtUtc = isLate ? deadline : DateTime.UtcNow;
  await db.SaveChangesAsync(); await tx.CommitAsync();
  await Audit(db, uid, "submission.submitted", "Submission", id.ToString(), isLate ? "late=true" : null);
  return Results.Ok(new { sub.Id, sub.Score, sub.MaxScore, sub.Status });
}).RequireAuthorization();
app.MapGet("/api/results/mine", async (ClaimsPrincipal p, AppDbContext db) =>
{
  await FinalizeExpired(db);
  if (!Guid.TryParse(UserId(p), out var studentId)) return Results.Unauthorized();
  return Results.Ok(await db.Submissions.Where(s => s.StudentId == studentId && s.Status != "InProgress" && s.Status != "Abandoned").OrderByDescending(s => s.SubmittedAtUtc).Select(s => new { s.Id, s.ExamId, ExamTitle = s.Exam!.Title, TeacherName = db.Users.Where(u => u.Id == s.Exam.TeacherId).Select(u => u.FullName).FirstOrDefault(), Subject = s.Exam.Description, Score = s.Status == "Submitted" || s.Status == "Reviewed" ? s.Score : (decimal?)null, s.MaxScore, s.Status, s.SubmittedAtUtc, s.StartedAtUtc, DurationSeconds = s.SubmittedAtUtc.HasValue ? (int)(s.SubmittedAtUtc.Value - s.StartedAtUtc).TotalSeconds : (int?)null, s.AttemptNumber, s.FocusLossDetected, s.FocusLossCount, s.CopyAttemptCount, s.ScreenshotAttemptCount }).ToListAsync());
}).RequireAuthorization();
app.MapGet("/api/exams/{id:guid}/results", async (Guid id, ClaimsPrincipal p, AppDbContext db, int page = 1, int pageSize = 25) =>
{
  if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid(); page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);

var userId = p.FindFirstValue(ClaimTypes.NameIdentifier);

if (!Guid.TryParse(userId, out var teacherId))
    return Results.Unauthorized();

var exam = await db.Exams.FirstOrDefaultAsync(
    x => x.Id == id && x.TeacherId == teacherId);

if (exam is null)
    return Results.NotFound();
  await FinalizeExpired(db);
  var q = db.Submissions.Where(s => s.ExamId == id && s.Status != "InProgress"); var total = await q.CountAsync(); var items = await q.OrderByDescending(s => s.SubmittedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).Select(s => new { s.Id, Student = s.Student!.FullName, Email = s.Student.Email, s.Score, s.MaxScore, s.Status, s.SubmittedAtUtc, s.FocusLossDetected }).ToListAsync(); return Results.Ok(new { items, page, pageSize, total });
}).RequireAuthorization();
app.MapPost("/api/submissions/{id:guid}/grant-extra-attempt", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
  if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
  if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
  await FinalizeExpired(db);
  var submission = await db.Submissions.Include(x => x.Exam).FirstOrDefaultAsync(x => x.Id == id);
  if (submission is null) return Results.NotFound(new { message = "لم نعثر على محاولة الطالب المطلوبة." });
  if (submission.Exam?.TeacherId != teacherId) return Results.Forbid();
  if (submission.Status == "InProgress" && !submission.FocusLossDetected) return Results.Conflict(new { message = "الطالب لديه محاولة مفتوحة ولم يُرصد خروجه منها. لا يمكن منح استثناء في هذه الحالة." });
  if (submission.IsExceptionAttempt) return Results.Conflict(new { message = "تم استخدام استثناء هذه المحاولة من قبل." });
  if (submission.Status != "InProgress") {
    var latest = await db.Submissions.Where(x => x.ExamId == submission.ExamId && x.StudentId == submission.StudentId && x.Status != "InProgress").OrderByDescending(x => x.SubmittedAtUtc).Select(x => x.Id).FirstOrDefaultAsync();
    if (latest != submission.Id) return Results.Conflict(new { message = "يمكن منح الاستثناء للمحاولة الأخيرة فقط." });
  }
  submission.IsExceptionAttempt = true;
  if (submission.Status == "InProgress") { submission.Status = "Abandoned"; submission.SubmittedAtUtc = DateTime.UtcNow; }
  await db.SaveChangesAsync();
  await Audit(db, teacherId, "submission.extra-attempt-granted", "Submission", id.ToString());
  return Results.Ok(new { message = "تم منح الطالب محاولة إضافية استثنائية واحدة.", submissionId = id });
}).RequireAuthorization();

app.MapPut("/api/submissions/{id:guid}/answers/{answerId:guid}/grade", async (Guid id, Guid answerId, EssayGradeRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
  if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
  if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
  var sub = await db.Submissions.Include(s => s.Answers).FirstOrDefaultAsync(s => s.Id == id);
  if (sub is null) return Results.NotFound();
  var exam = await db.Exams.FirstOrDefaultAsync(e => e.Id == sub.ExamId && e.TeacherId == teacherId);
  if (exam is null) return Results.Forbid();
  if (sub.Status is not ("NeedsReview" or "Reviewed")) return Results.BadRequest(new { message = "لا يمكن تعديل درجة محاولة غير مسلّمة." });
  var a = sub.Answers.FirstOrDefault(x => x.Id == answerId);
  if (a is null) return Results.NotFound();
  var q = await db.Questions.FindAsync(a.QuestionId);
  if (q is null) return Results.NotFound();
  if (q.Type != "Essay") return Results.BadRequest(new { message = "يمكن تصحيح الأسئلة المقالية فقط." });
  if (req.AwardedPoints < 0 || req.AwardedPoints > q.Points) return Results.BadRequest(new { message = "الدرجة خارج النطاق." });
  var previousPoints = a.AwardedPoints;
  a.AwardedPoints = req.AwardedPoints;
  a.TeacherFeedback = req.Feedback;
  sub.Score = sub.Answers.Sum(x => x.AwardedPoints ?? 0);
  var essayQuestionIds = await db.Questions.Where(x => x.ExamId == sub.ExamId && x.Type == "Essay").Select(x => x.Id).ToListAsync();
  sub.Status = sub.Answers.Any(x => essayQuestionIds.Contains(x.QuestionId) && x.AwardedPoints == null) ? "NeedsReview" : "Reviewed";
  await db.SaveChangesAsync();
  await Audit(db, teacherId, "essay.graded", "Submission", id.ToString(), $"old={previousPoints}; new={req.AwardedPoints}");
  return Results.Ok(new { sub.Score, sub.MaxScore, sub.Status });
}).RequireAuthorization();
app.MapGet("/api/exams/{id:guid}/review", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
  if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();

var userId = p.FindFirstValue(ClaimTypes.NameIdentifier);

if (!Guid.TryParse(userId, out var teacherId))
    return Results.Unauthorized();

var exam = await db.Exams.FirstOrDefaultAsync(
    e => e.Id == id && e.TeacherId == teacherId);

if (exam is null)
    return Results.NotFound();
  await FinalizeExpired(db);
  var rows = await db.Submissions.AsNoTracking().Where(s => s.ExamId == id && s.Status == "NeedsReview").Select(s => new { s.Id, Student = s.Student!.FullName, s.Score, s.MaxScore, Answers = s.Answers.Select(a => new { a.Id, a.QuestionId, a.AnswerText, a.AwardedPoints, a.TeacherFeedback, Question = a.Question!.Text, Points = a.Question.Points, Type = a.Question.Type }).ToList() }).ToListAsync();
  return Results.Ok(rows);
}).RequireAuthorization();
app.MapGet("/api/exams/{id:guid}/export", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{

if (p.FindFirstValue(ClaimTypes.Role) != "Teacher")
    return Results.Forbid();

var userId = p.FindFirstValue(ClaimTypes.NameIdentifier);

if (!Guid.TryParse(userId, out var teacherId))
    return Results.Unauthorized();

var exam = await db.Exams.FirstOrDefaultAsync(
    e => e.Id == id && e.TeacherId == teacherId);

if (exam is null)
    return Results.NotFound();
  await FinalizeExpired(db);
  var rows = await db.Submissions.Where(s => s.ExamId == id && s.Status != "InProgress").Select(s => new { s.Student!.FullName, s.Student.Email, s.Score, s.MaxScore, s.Status, s.SubmittedAtUtc }).ToListAsync();
  static string Esc(string? x)
  {
    var value = (x ?? "").Replace("\r", " ").Replace("\n", " ");
    if (value.Length > 0 && "=+-@".Contains(value.TrimStart()[0])) value = "'" + value;
    return "\"" + value.Replace("\"", "\"\"") + "\"";
  }
  var csv = "Student,Email,Score,MaxScore,Status,SubmittedAtUtc\r\n" + string.Join("\r\n", rows.Select(x => string.Join(",", new[] { Esc(x.FullName), Esc(x.Email), x.Score.ToString(System.Globalization.CultureInfo.InvariantCulture), x.MaxScore.ToString(System.Globalization.CultureInfo.InvariantCulture), Esc(x.Status), Esc(x.SubmittedAtUtc?.ToString("O")) })));
  await Audit(db, teacherId, "results.exported", "Exam", id.ToString()); return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8", $"exam-{id}-results.csv");
}).RequireAuthorization();
app.MapGet("/api/audit", async (ClaimsPrincipal p, AppDbContext db, int page = 1, int pageSize = 50) => { if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid(); if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized(); page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100); var q = db.AuditEvents.AsNoTracking().Where(x => x.ActorId == teacherId).OrderByDescending(x => x.AtUtc); var total = await q.CountAsync(); return Results.Ok(new { items = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(), page, pageSize, total }); }).RequireAuthorization();

app.MapPost("/api/submissions/{id:guid}/events", async (Guid id, SubmissionEventRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
  if (!Guid.TryParse(UserId(p), out var uid)) return Results.Unauthorized();
  var sub = await db.Submissions.FirstOrDefaultAsync(s => s.Id == id && s.StudentId == uid && s.Status == "InProgress");
  if (sub is null) return Results.NotFound(new { message = "المحاولة غير موجودة أو تم إنهاؤها." });
  switch ((req.Type ?? "").ToLowerInvariant()) {
    case "focus-loss": sub.FocusLossCount++; sub.FocusLossDetected = true; break;
    case "copy": sub.CopyAttemptCount++; break;
    case "screenshot": sub.ScreenshotAttemptCount++; break;
    default: return Results.BadRequest(new { message = "نوع الحدث غير معروف." });
  }
  await db.SaveChangesAsync(); await Audit(db, uid, "exam.event." + req.Type, "Submission", id.ToString());
  return Results.Ok(new { recorded = true });
}).RequireAuthorization();

app.MapGet("/api/submissions/{id:guid}/review", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
  if (!Guid.TryParse(UserId(p), out var uid)) return Results.Unauthorized();
  await FinalizeExpired(db);
  var role = p.FindFirstValue(ClaimTypes.Role);
  var sub = await db.Submissions.Include(s => s.Exam).Include(s => s.Student).Include(s => s.Answers).FirstOrDefaultAsync(s => s.Id == id);
  if (sub is null) return Results.NotFound(new { message = "المحاولة المطلوبة غير موجودة." });
  if (role == "Student" && sub.StudentId != uid) return Results.Forbid();
  if (role == "Teacher" && sub.Exam!.TeacherId != uid) return Results.Forbid();
  var qs = await db.Questions.Where(q => q.ExamId == sub.ExamId).OrderBy(q => q.SortOrder).ToListAsync();
  var elapsed = (int)((sub.SubmittedAtUtc ?? DateTime.UtcNow) - sub.StartedAtUtc).TotalSeconds;
  var showScore = role == "Teacher" || sub.Status == "Submitted" || sub.Status == "Reviewed";
  return Results.Ok(new { sub.Id, sub.ExamId, ExamTitle = sub.Exam!.Title, TeacherName = await db.Users.Where(u => u.Id == sub.Exam.TeacherId).Select(u => u.FullName).FirstOrDefaultAsync(), StudentName = sub.Student!.FullName, sub.Status, Score = showScore ? sub.Score : (decimal?)null, MaxScore = showScore ? sub.MaxScore : (decimal?)null, sub.StartedAtUtc, sub.SubmittedAtUtc, DurationSeconds = elapsed, sub.AttemptNumber, sub.FocusLossCount, sub.CopyAttemptCount, sub.ScreenshotAttemptCount, Answers = qs.Select(q => { var a = sub.Answers.FirstOrDefault(x => x.QuestionId == q.Id); return new { q.Id, q.Text, q.Type, Options = JsonSerializer.Deserialize<string[]>(q.OptionsJson), CorrectAnswer = showScore ? q.CorrectAnswer : null, q.Points, StudentAnswer = a?.AnswerText ?? "", AwardedPoints = showScore ? a?.AwardedPoints : null, a?.TeacherFeedback }; }) });
}).RequireAuthorization();

app.Run();
