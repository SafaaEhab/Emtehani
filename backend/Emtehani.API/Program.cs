using System.Net;
using System.Net.Mail;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
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
var isProduction = builder.Environment.IsProduction();
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Services.AddInfrastructure(builder.Configuration);

var configuredOrigins = builder.Configuration["FrontendOrigin"];
if (string.IsNullOrWhiteSpace(configuredOrigins))
{
    if (isProduction)
        throw new InvalidOperationException("FrontendOrigin must be configured as an absolute HTTPS origin in Production.");
    configuredOrigins = "http://localhost:5173";
}
var allowedOrigins = configuredOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(origin => origin.TrimEnd('/'))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
if (allowedOrigins.Length == 0 || allowedOrigins.Any(origin => !IsValidOrigin(origin, requireHttps: isProduction)))
    throw new InvalidOperationException("FrontendOrigin must contain only valid absolute origins (HTTPS is required in Production), without paths, queries, or wildcards.");
builder.Services.AddCors(o => o.AddPolicy("frontend", p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders("Retry-After")
    .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

var configuredPublicApiUrl = builder.Configuration["PublicApiUrl"];
if (isProduction)
{
    if (!IsValidOrigin(configuredPublicApiUrl, requireHttps: true))
        throw new InvalidOperationException("PublicApiUrl must be configured as an absolute HTTPS origin in Production.");
}
else if (!IsValidOrigin(configuredPublicApiUrl, requireHttps: false))
{
    configuredPublicApiUrl = "http://localhost:5000";
}
builder.Configuration["PublicApiUrl"] = configuredPublicApiUrl!.TrimEnd('/');

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
var jwtKey = builder.Configuration["Jwt:Key"] ?? Environment.GetEnvironmentVariable("JWT_KEY");
if (string.IsNullOrWhiteSpace(jwtKey))
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException("Jwt:Key or JWT_KEY must be configured outside Development.");
    jwtKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
}
if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException("Jwt:Key must contain at least 32 bytes.");
builder.Configuration["Jwt:Key"] = jwtKey;
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "Emtehani";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "Emtehani.Client";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.SaveToken = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = jwtIssuer,
        ValidateAudience = true,
        ValidAudience = jwtAudience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = ClaimTypes.Name,
        RoleClaimType = ClaimTypes.Role
    };
});
builder.Services.AddAuthorization(); builder.Services.AddEndpointsApiExplorer(); builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    var apiPermitLimit = Math.Max(1, builder.Configuration.GetValue("RateLimiting:ApiPermitLimit", 120));
    var authPermitLimit = Math.Max(1, builder.Configuration.GetValue("RateLimiting:AuthPermitLimit", 60));
    var joinPermitLimit = Math.Max(1, builder.Configuration.GetValue("RateLimiting:JoinPermitLimit", 10));
    static string ClientKey(HttpContext context, bool perUser)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (perUser && !string.IsNullOrWhiteSpace(userId)) return "user:" + userId;
        return "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }
    static RateLimitPartition<string> Fixed(HttpContext context, int permitLimit, bool perUser = false) =>
        RateLimitPartition.GetFixedWindowLimiter(
            ClientKey(context, perUser),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true });
    o.AddPolicy("api", context => Fixed(context, apiPermitLimit));
    o.AddPolicy("auth", context => Fixed(context, authPermitLimit));
    // Code guessing is limited per authenticated account, not just per shared IP.
    o.AddPolicy("join", context => Fixed(context, joinPermitLimit, perUser: true));
    o.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        else
            context.HttpContext.Response.Headers.RetryAfter = "60";
        context.HttpContext.Response.ContentType = "application/problem+json; charset=utf-8";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            status = StatusCodes.Status429TooManyRequests,
            title = "طلبات كثيرة خلال وقت قصير.",
            message = "انتظر دقيقة ثم حاول مرة أخرى."
        }, cancellationToken);
    };
});
builder.Services.AddProblemDetails();
var swaggerEnabled = builder.Configuration.GetValue("Swagger:Enabled", builder.Environment.IsDevelopment());
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
    o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
});
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    await ExamWorkflowSchema.EnsureAsync(db);
}
app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["X-Permitted-Cross-Domain-Policies"] = "none";
    context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
    if (context.Request.Path.StartsWithSegments("/api"))
        context.Response.Headers["Cache-Control"] = "no-store";
    await next();
});
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var isConcurrency = exception is DbUpdateConcurrencyException
        || exception is DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: "40001" } }
        || exception is Npgsql.PostgresException { SqlState: "40001" };
    var isBadRequest = exception is BadHttpRequestException
        || exception is JsonException
        || exception is InvalidOperationException && exception.Message.Contains("Implicit body", StringComparison.Ordinal);
    var isUniqueViolation = exception is DbUpdateException { InnerException: not null } dbEx && IsUniqueViolation(dbEx.InnerException);
    var status = isConcurrency ? StatusCodes.Status409Conflict
        : isBadRequest ? StatusCodes.Status400BadRequest
        : isUniqueViolation ? StatusCodes.Status409Conflict
        : StatusCodes.Status500InternalServerError;
    if (status >= 500)
        app.Logger.LogError(exception, "Unhandled API exception. TraceId: {TraceId}", context.TraceIdentifier);
    else
        app.Logger.LogWarning(exception, "Rejected API request with status {Status}. TraceId: {TraceId}", status, context.TraceIdentifier);

    context.Response.StatusCode = status;
    context.Response.ContentType = "application/problem+json; charset=utf-8";
    await context.Response.WriteAsJsonAsync(new
    {
        type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
        title = status switch
        {
            StatusCodes.Status409Conflict => "لم يتم اعتماد التغيير بسبب تعارض في الحالة الحالية.",
            StatusCodes.Status400BadRequest => "الطلب غير صالح أو ناقص البيانات.",
            _ => "تعذر إكمال العملية بسبب مشكلة مؤقتة في الخادم."
        },
        message = status switch
        {
            StatusCodes.Status409Conflict => "انتظر لحظة ثم حدّث الصفحة وأعد المحاولة؛ لم يتم تأكيد أي تغيير.",
            StatusCodes.Status400BadRequest => "راجع البيانات المرسلة ثم أعد المحاولة.",
            _ => "احتفظ بالصفحة مفتوحة وحاول مرة أخرى. إذا تكررت المشكلة، أرسل رقم البلاغ للدعم."
        },
        status,
        traceId = context.TraceIdentifier
    });
}));
if (swaggerEnabled) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseCors("frontend");
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapGet("/api", () => Results.Ok(new { service = "Emtehani API", version = "1.0" }));
string Hash(string password, string salt) => Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt), 120000, HashAlgorithmName.SHA256, 32));
string Salt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
string UserId(ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
static bool IsValidOrigin(string? value, bool requireHttps)
{
    if (string.IsNullOrWhiteSpace(value) || value.Contains('*', StringComparison.Ordinal) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        return false;
    if (uri.Scheme != Uri.UriSchemeHttps && (!requireHttps && uri.Scheme != Uri.UriSchemeHttp)) return false;
    if (requireHttps && uri.Scheme != Uri.UriSchemeHttps) return false;
    return string.IsNullOrEmpty(uri.UserInfo)
        && (string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/")
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);
}
static bool FixedTimeEquals(string left, string right)
{
    var leftBytes = Encoding.UTF8.GetBytes(left);
    var rightBytes = Encoding.UTF8.GetBytes(right);
    return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}
static bool IsUniqueViolation(Exception? ex) => ex switch
{
    Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 } => true,
    Npgsql.PostgresException { SqlState: "23505" } => true,
    _ => false
};
async Task Audit(AppDbContext db, Guid? actor, string action, string type, string? id, string? details = null)
{
    try
    {
        db.AuditEvents.Add(new AuditEvent { ActorId = actor, Action = action, EntityType = type, EntityId = id, Details = details });
        await db.SaveChangesAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Audit write failed for {Action} {EntityType} {EntityId}.", action, type, id);
    }
}
async Task<string> NewStudentCode(AppDbContext db)
{
    string code;
    do { code = RandomNumberGenerator.GetInt32(100000, 1_000_000).ToString(); }
    while (await db.Users.AnyAsync(x => x.StudentCode == code) || await db.ExamParticipants.AnyAsync(x => x.Code == code));
    return code;
}
static long SubmissionLockKey(Guid submissionId)
{
    Span<byte> bytes = stackalloc byte[16];
    submissionId.TryWriteBytes(bytes);
    return BitConverter.ToInt64(bytes[..8]);
}
async Task AcquireSubmissionLock(AppDbContext db, Guid submissionId)
{
    if (!db.Database.IsNpgsql()) return;
    var key = SubmissionLockKey(submissionId);
    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})");
}
static int? RemainingSeconds(Submission? submission, Exam exam, DateTime now)
{
    if (submission is null) return null;
    if (submission.Status != "InProgress") return 0;
    var reference = exam.Status == "Paused" && exam.PausedAtUtc.HasValue ? exam.PausedAtUtc.Value : now;
    return Math.Max(0, (int)(ExamGrader.Deadline(submission, exam.DurationMinutes) - reference).TotalSeconds);
}
async Task CloseExam(AppDbContext db, Exam exam, DateTime now)
{
    await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
    var submissionIds = await db.Submissions.AsNoTracking()
        .Where(x => x.ExamId == exam.Id && x.Status == "InProgress")
        .Select(x => x.Id)
        .ToListAsync();
    var questions = await db.Questions.AsNoTracking().Where(x => x.ExamId == exam.Id).ToListAsync();
    foreach (var submissionId in submissionIds)
    {
        await AcquireSubmissionLock(db, submissionId);
        var submission = await db.Submissions.Include(x => x.Answers)
            .FirstOrDefaultAsync(x => x.Id == submissionId && x.Status == "InProgress");
        if (submission is null) continue;
        ExamGrader.Grade(submission, questions);
        submission.SubmittedAtUtc = now;
        var participant = await db.ExamParticipants.FirstOrDefaultAsync(x => x.ExamId == exam.Id && x.StudentId == submission.StudentId);
        if (participant is not null) { participant.Status = "Submitted"; participant.LastSeenAtUtc = now; }
    }
    await db.ExamParticipants
        .Where(x => x.ExamId == exam.Id && (x.Status == "Waiting" || x.Status == "Active"))
        .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "Submitted"));
    exam.Status = "Finished";
    exam.PausedAtUtc = null;
    await db.SaveChangesAsync();
    await tx.CommitAsync();
}
async Task<List<ExamParticipantView>> ParticipantViews(AppDbContext db, Exam exam, DateTime now)
{
    var participants = await db.ExamParticipants.AsNoTracking().Include(x => x.Student).Where(x => x.ExamId == exam.Id).OrderBy(x => x.Student!.FullName).ToListAsync();
    var submissions = await db.Submissions.AsNoTracking().Where(x => x.ExamId == exam.Id).ToListAsync();
    var latestByStudent = submissions.GroupBy(x => x.StudentId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.AttemptNumber).First());
    return participants.Select(participant =>
    {
        latestByStudent.TryGetValue(participant.StudentId, out var submission);
        var status = participant.Status;
        if (submission is not null)
        {
            if (submission.Status == "InProgress" && (exam.Status is "Active" or "Paused")) status = "Active";
            else if (submission.Status is "Submitted" or "Reviewed" or "NeedsReview" or "Processing" or "Abandoned") status = "Submitted";
        }
        if ((status is "Active" or "Waiting") && participant.LastSeenAtUtc.HasValue && now - participant.LastSeenAtUtc.Value > TimeSpan.FromSeconds(45)) status = "Disconnected";
        if (status == "Active" && !participant.LastSeenAtUtc.HasValue) status = "Disconnected";
        return new ExamParticipantView(
            participant.Id,
            participant.Student?.FullName ?? "طالب",
            participant.Student?.Email ?? "",
            participant.Code,
            status,
            participant.JoinedAtUtc,
            participant.LastSeenAtUtc,
            submission?.SubmittedAtUtc,
            submission is null || submission.Status == "InProgress" ? null : submission.Score,
            submission is null ? null : submission.MaxScore,
            RemainingSeconds(submission, exam, now));
    }).ToList();
}
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
    var now = DateTime.UtcNow;
    var expiredIds = await db.Submissions
        .AsNoTracking()
        .Where(x => x.Status == "InProgress" && x.Exam != null
            && now >= x.StartedAtUtc.AddMinutes(x.Exam.DurationMinutes + FinalizeGraceMinutes))
        .Select(x => x.Id)
        .ToListAsync();
    if (expiredIds.Count == 0) return;
    await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
    foreach (var submissionId in expiredIds)
    {
        await AcquireSubmissionLock(db, submissionId);
        var submission = await db.Submissions
            .Include(x => x.Answers)
            .Include(x => x.Exam)
            .FirstOrDefaultAsync(x => x.Id == submissionId && x.Status == "InProgress");
        if (submission?.Exam is null) continue;
        var deadline = ExamGrader.Deadline(submission, submission.Exam.DurationMinutes);
        if (now < deadline.AddMinutes(FinalizeGraceMinutes)) continue;
        var questions = await db.Questions.AsNoTracking().Where(x => x.ExamId == submission.ExamId).ToListAsync();
        ExamGrader.Grade(submission, questions);
        submission.SubmittedAtUtc = deadline;
        var participant = await db.ExamParticipants.FirstOrDefaultAsync(x => x.ExamId == submission.ExamId && x.StudentId == submission.StudentId);
        if (participant is not null) { participant.Status = "Submitted"; participant.LastSeenAtUtc = now; }
    }
    await db.SaveChangesAsync();
    await tx.CommitAsync();
}
app.MapPost("/api/auth/register", async (RegisterRequest req, AppDbContext db, Emtehani.Application.Abstractions.ITokenService tokens) =>
{
  if (req is null || string.IsNullOrWhiteSpace(req.FullName) || string.IsNullOrWhiteSpace(req.Email) ||
      string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8 || req.Password.Length > 128 ||
      req.FullName.Trim().Length is < 3 or > 120 || req.Email.Trim().Length > 254 || !req.Email.Contains('@'))
    return Results.BadRequest(new { message = "أدخل الاسم والبريد الصحيح وكلمة مرور بين 8 و128 حرفاً." });

  var email = req.Email.Trim().ToLowerInvariant();
  var role = string.Equals(req.Role, "Teacher", StringComparison.OrdinalIgnoreCase) ? "Teacher" : "Student";
  if (await db.Users.AnyAsync(x => x.Email == email))
    return Results.Conflict(new { message = "البريد مستخدم بالفعل." });

  var salt = Salt();
  var smtpHost = builder.Configuration["Email:SmtpHost"];
  var smtpUsername = builder.Configuration["Email:Username"];
  var smtpPassword = builder.Configuration["Email:Password"];
  var smtpConfigured = !string.IsNullOrWhiteSpace(smtpHost)
      && !string.IsNullOrWhiteSpace(smtpUsername)
      && !string.IsNullOrWhiteSpace(smtpPassword);
  var autoConfirmWithoutSmtp = builder.Configuration.GetValue("Auth:AutoConfirmWithoutSmtp", app.Environment.IsDevelopment());
  if (!smtpConfigured)
  {
    if (!autoConfirmWithoutSmtp)
      return Results.Problem(statusCode: 503, title: "خدمة تأكيد البريد غير مهيأة. يرجى التواصل مع إدارة المنصة.", extensions: new Dictionary<string, object?> { ["message"] = "خدمة البريد الإلكتروني غير متاحة حاليًا." });
    var newUser = new AppUser { FullName = req.FullName.Trim(), Email = email, Role = role, EmailConfirmed = true, PasswordHash = salt + ":" + Hash(req.Password, salt) };
    if (role == "Student") newUser.StudentCode = await NewStudentCode(db);
    db.Users.Add(newUser);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "تم إنشاء الحساب وتفعيله تلقائيًا. يمكنك تسجيل الدخول الآن.", studentCode = newUser.StudentCode });
  }
  var verifyToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
  var user = new AppUser { FullName = req.FullName.Trim(), Email = email, Role = role, EmailConfirmed = false, EmailVerificationToken = verifyToken, EmailVerificationExpiresUtc = DateTime.UtcNow.AddHours(24), PasswordHash = salt + ":" + Hash(req.Password, salt) };
  if (role == "Student") user.StudentCode = await NewStudentCode(db);
  db.Users.Add(user);
  await db.SaveChangesAsync();
  if (!await SendVerificationEmail(user, verifyToken))
  {
    db.Users.Remove(user); await db.SaveChangesAsync();
    return Results.Problem(statusCode: 503, title: "تعذر إرسال رسالة التأكيد.", extensions: new Dictionary<string, object?> { ["message"] = "لم نتمكن من إرسال رسالة التأكيد. تحقق من إعدادات البريد وحاول مرة أخرى." });
  }
  return Results.Ok(new { message = "تم إنشاء الحساب. افتح بريدك الإلكتروني واضغط على رابط التأكيد قبل تسجيل الدخول.", studentCode = user.StudentCode });
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
  if (parts.Length != 2 || !FixedTimeEquals(Hash(req.Password, parts[0]), parts[1])) return Results.Unauthorized();
  if (!user.EmailConfirmed) return Results.Json(new { message = "لازم تأكد بريدك الإلكتروني الأول. افتح رسالة التأكيد اللي وصلتك على بريدك ثم جرّب تسجيل الدخول." }, statusCode: StatusCodes.Status403Forbidden);
  if (user.Role == "Student" && string.IsNullOrWhiteSpace(user.StudentCode))
  {
      user.StudentCode = await NewStudentCode(db);
      await db.SaveChangesAsync();
  }
  var t = tokens.Create(user);
   return Results.Ok(new AuthResponse(t.Token, user.FullName, user.Email, user.Role, t.ExpiresAtUtc, user.StudentCode));
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
  var validTypes = new[] { "MultipleChoice", "TrueFalse", "ShortAnswer", "Essay", "FillBlank", "Matching" };
  if (req.Questions.Any(q => q is null || string.IsNullOrWhiteSpace(q.Text) || q.Points <= 0 || string.IsNullOrWhiteSpace(q.Type) || !validTypes.Contains(q.Type) ||
    (q.Type == "MultipleChoice" && ((q.Options?.Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0) < 2 || string.IsNullOrWhiteSpace(q.CorrectAnswer) || !(q.Options ?? []).Contains(q.CorrectAnswer, StringComparer.OrdinalIgnoreCase))) ||
    (q.Type == "TrueFalse" && !new[] { "صح", "خطأ" }.Contains(q.CorrectAnswer ?? "")) ||
    ((q.Type is "ShortAnswer" or "FillBlank") && string.IsNullOrWhiteSpace(q.CorrectAnswer)) ||
    (q.Type == "Matching" && ((q.Options?.Length ?? 0) < 2 || (q.Options ?? []).Any(option => !option.Contains("||", StringComparison.Ordinal))))))
    return Results.BadRequest(new { message = "راجع نصوص الأسئلة والدرجات والاختيارات والإجابات الصحيحة." });
  if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
  if (!Guid.TryParse(UserId(p), out var uid)) return Results.Unauthorized();
  var code = RandomNumberGenerator.GetInt32(100000, 1_000_000).ToString(); while (await db.Exams.AnyAsync(x => x.JoinCode == code)) code = RandomNumberGenerator.GetInt32(100000, 1_000_000).ToString();
  var e = new Exam { TeacherId = uid, Title = req.Title.Trim(), Description = req.Description, DurationMinutes = req.DurationMinutes, MaxAttempts = Math.Clamp(req.MaxAttempts, 1, 20), AllowResumeAfterFocusLoss = req.AllowResumeAfterFocusLoss, JoinCode = code, ScheduledAtUtc = req.ScheduledAtUtc?.ToUniversalTime(), Status = "Draft", Questions = req.Questions.Select((q, i) => new Question { Text = q.Text, Type = q.Type, OptionsJson = JsonSerializer.Serialize(q.Options ?? []), CorrectAnswer = q.Type == "Matching" ? string.Join(",", Enumerable.Range(0, q.Options?.Length ?? 0)) : q.CorrectAnswer, Points = q.Points, SortOrder = i }).ToList() };
  db.Exams.Add(e); await db.SaveChangesAsync(); await Audit(db, uid, "exam.created", "Exam", e.Id.ToString()); return Results.Created($"/api/exams/{e.Id}", new { e.Id, e.Title, e.Description, e.DurationMinutes, e.JoinCode, e.IsPublished, e.Status, e.ScheduledAtUtc, questionCount = e.Questions.Count });
}).RequireAuthorization().RequireRateLimiting("api");
app.MapGet("/api/exams", async (ClaimsPrincipal p, AppDbContext db, string? search, string? status, int page = 1, int pageSize = 10) =>
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
  if (!string.IsNullOrWhiteSpace(status) && status != "All") q = q.Where(x => x.Status == status);
  var total = await q.CountAsync(); var canSeeJoinCode = p.FindFirstValue(ClaimTypes.Role) == "Teacher";
  var items = await q.OrderByDescending(x => x.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).Select(x => new { x.Id, x.Title, x.Description, x.DurationMinutes, x.MaxAttempts, x.AllowResumeAfterFocusLoss, TeacherName = db.Users.Where(u => u.Id == x.TeacherId).Select(u => u.FullName).FirstOrDefault(), JoinCode = canSeeJoinCode ? x.JoinCode : null, x.IsPublished, x.Status, x.ScheduledAtUtc, x.CreatedAtUtc, QuestionCount = x.Questions.Count, ParticipantCount = x.Participants.Count }).ToListAsync();
  return Results.Ok(new { items, page, pageSize, total });
}).RequireAuthorization();
app.MapGet("/api/exams/{id:guid}", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
    if (!Guid.TryParse(UserId(p), out var userId)) return Results.Unauthorized();
    var role = p.FindFirstValue(ClaimTypes.Role);
    var exam = await db.Exams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    if (exam is null) return Results.NotFound();
    if (role == "Teacher" && exam.TeacherId != userId) return Results.Forbid();
    if (role != "Teacher")
    {
        if (!exam.IsPublished) return Results.NotFound();
        var joined = await db.ExamParticipants.AnyAsync(x => x.ExamId == id && x.StudentId == userId && x.Status != "Invited");
        if (!joined) return Results.Forbid();
    }
    return Results.Ok(new
    {
        exam.Id, exam.Title, exam.Description, exam.DurationMinutes, exam.MaxAttempts,
        exam.AllowResumeAfterFocusLoss, exam.IsPublished, exam.Status, exam.ScheduledAtUtc, exam.CreatedAtUtc,
        JoinCode = role == "Teacher" ? exam.JoinCode : null,
        QuestionCount = await db.Questions.CountAsync(x => x.ExamId == id),
        ParticipantCount = await db.ExamParticipants.CountAsync(x => x.ExamId == id)
    });
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
    if (exam.IsPublished && (exam.Status is "Active" or "Paused"))
        return Results.Conflict(new { message = "أوقف الامتحان أو أنهِ الجلسة قبل إلغاء نشره." });
    exam.IsPublished = !exam.IsPublished;
    exam.Status = exam.IsPublished
        ? exam.ScheduledAtUtc > DateTime.UtcNow ? "Scheduled" : "Waiting"
        : "Draft";
    await db.SaveChangesAsync();
    await Audit(db, teacherId, exam.IsPublished ? "exam.published" : "exam.unpublished", "Exam", id.ToString());
    return Results.Ok(new { exam.Id, exam.IsPublished, exam.Status, exam.JoinCode });
}).RequireAuthorization();

app.MapGet("/api/teacher/overview", async (ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var examIds = db.Exams.Where(x => x.TeacherId == teacherId).Select(x => x.Id);
    var totalExams = await examIds.CountAsync();
    var publishedExams = await db.Exams.CountAsync(x => x.TeacherId == teacherId && x.IsPublished);
    var activeExams = await db.Exams.CountAsync(x => x.TeacherId == teacherId && (x.Status == "Active" || x.Status == "Paused"));
    var participantCount = await db.ExamParticipants.CountAsync(x => examIds.Contains(x.ExamId));
    var studentsReached = await db.ExamParticipants.Where(x => examIds.Contains(x.ExamId)).Select(x => x.StudentId).Distinct().CountAsync();
    var pendingReview = await db.Submissions.CountAsync(x => examIds.Contains(x.ExamId) && x.Status == "NeedsReview");
    var submitted = await db.Submissions.CountAsync(x => examIds.Contains(x.ExamId) && x.Status != "InProgress");
    var recent = await db.Exams.Where(x => x.TeacherId == teacherId).OrderByDescending(x => x.CreatedAtUtc).Take(5)
        .Select(x => new { x.Id, x.Title, x.Description, x.Status, x.IsPublished, x.CreatedAtUtc, x.DurationMinutes, ParticipantCount = x.Participants.Count, QuestionCount = x.Questions.Count }).ToListAsync();
    var live = await db.Exams.Where(x => x.TeacherId == teacherId && (x.Status == "Active" || x.Status == "Paused")).OrderByDescending(x => x.ScheduledAtUtc).Take(6)
        .Select(x => new { x.Id, x.Title, x.Status, x.ScheduledAtUtc, ParticipantCount = x.Participants.Count }).ToListAsync();
    return Results.Ok(new { totalExams, publishedExams, activeExams, participantCount, studentsReached, pendingReview, submitted, recent, live, serverUtc = DateTime.UtcNow });
}).RequireAuthorization();

app.MapPost("/api/exams/{id:guid}/participants/generate", async (Guid id, ExamParticipantGenerateRequest? req, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var exam = await db.Exams.FirstOrDefaultAsync(x => x.Id == id && x.TeacherId == teacherId);
    if (exam is null) return Results.NotFound();
    if (exam.Status is "Active" or "Paused") return Results.Conflict(new { message = "لا يمكن إنشاء أكواد طلاب بعد بدء الجلسة." });
    var studentsQuery = db.Users.Where(x => x.Role == "Student");
    var requestedStudentIds = req?.StudentIds;
    if (requestedStudentIds is { Length: > 0 }) studentsQuery = studentsQuery.Where(x => requestedStudentIds.Contains(x.Id));
    var students = await studentsQuery.Select(x => new { x.Id, x.FullName }).ToListAsync();
    if (students.Count == 0) return Results.Conflict(new { message = "لا يوجد طلاب متاحون لإنشاء أكواد لهم." });
    var existing = await db.ExamParticipants.Where(x => x.ExamId == id).Select(x => x.StudentId).ToListAsync();
    var existingSet = existing.ToHashSet();
    var usedCodes = (await db.ExamParticipants.Select(x => x.Code).ToListAsync())
        .Concat(await db.Users.Where(x => x.StudentCode != "").Select(x => x.StudentCode).ToListAsync())
        .ToHashSet(StringComparer.Ordinal);
    var generated = 0;
    foreach (var student in students.Where(x => !existingSet.Contains(x.Id)))
    {
        string code;
        do { code = RandomNumberGenerator.GetInt32(100000, 1_000_000).ToString(); } while (usedCodes.Contains(code));
        usedCodes.Add(code);
        db.ExamParticipants.Add(new ExamParticipant { ExamId = exam.Id, StudentId = student.Id, Code = code, Status = "Invited" });
        generated++;
    }
    await db.SaveChangesAsync();
    await Audit(db, teacherId, "exam.participants.generated", "Exam", id.ToString(), $"count={generated}");
    return Results.Ok(new { generated, total = existing.Count + generated });
}).RequireAuthorization();

app.MapGet("/api/exams/{id:guid}/participants", async (Guid id, ClaimsPrincipal p, AppDbContext db, int page = 1, int pageSize = 25) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var exam = await db.Exams.FirstOrDefaultAsync(x => x.Id == id && x.TeacherId == teacherId);
    if (exam is null) return Results.NotFound();
    var now = DateTime.UtcNow;
    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 100);
    var views = await ParticipantViews(db, exam, now);
    return Results.Ok(new
    {
        examStatus = exam.Status,
        serverUtc = now,
        page,
        pageSize,
        total = views.Count,
        items = views.Skip((page - 1) * pageSize).Take(pageSize).ToList()
    });
}).RequireAuthorization();

app.MapPost("/api/exams/{id:guid}/participants/{participantId:guid}/regenerate", async (Guid id, Guid participantId, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var exam = await db.Exams.FirstOrDefaultAsync(x => x.Id == id && x.TeacherId == teacherId);
    if (exam is null) return Results.NotFound();
    var participant = await db.ExamParticipants.FirstOrDefaultAsync(x => x.Id == participantId && x.ExamId == id);
    if (participant is null) return Results.NotFound();
    if (participant.JoinedAtUtc.HasValue || participant.Status is "Active" or "Submitted")
        return Results.Conflict(new { message = "لا يمكن تغيير كود طالب دخل الامتحان بالفعل." });
    participant.Code = await NewStudentCode(db);
    await db.SaveChangesAsync();
    await Audit(db, teacherId, "exam.participant-code.regenerated", "ExamParticipant", participant.Id.ToString());
    return Results.Ok(new { participant.Id, participant.Code });
}).RequireAuthorization();

app.MapGet("/api/exams/{id:guid}/participants/export", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var exam = await db.Exams.FirstOrDefaultAsync(x => x.Id == id && x.TeacherId == teacherId);
    if (exam is null) return Results.NotFound();
    var rows = await ParticipantViews(db, exam, DateTime.UtcNow);
    static string Esc(string? value)
    {
        var text = (value ?? "").Replace("\r", " ").Replace("\n", " ");
        var trimmed = text.TrimStart();
        if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0])) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
    var csv = "Student,Email,Code,Status,JoinedAt,SubmittedAt,Score,MaxScore\r\n" + string.Join("\r\n", rows.Select(x => string.Join(",",
        Esc(x.StudentName), Esc(x.Email), Esc(x.Code), Esc(x.Status), Esc(x.JoinedAtUtc?.ToString("O")), Esc(x.SubmittedAtUtc?.ToString("O")),
        x.Score?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", x.MaxScore?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "")));
    await Audit(db, teacherId, "exam.participants.exported", "Exam", id.ToString());
    return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8", $"exam-{id}-student-codes.csv");
}).RequireAuthorization();

app.MapPut("/api/exams/{id:guid}/state", async (Guid id, ExamStateRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var exam = await db.Exams.FirstOrDefaultAsync(x => x.Id == id && x.TeacherId == teacherId);
    if (exam is null) return Results.NotFound();
    var action = (req?.Action ?? "").Trim().ToLowerInvariant();
    var now = DateTime.UtcNow;
    switch (action)
    {
        case "open":
            if (exam.Status == "Finished") return Results.Conflict(new { message = "هذه الجلسة منتهية. أنشئ امتحاناً جديداً لبدء جلسة أخرى." });
            exam.Status = "Active"; exam.IsPublished = true; exam.PausedAtUtc = null;
            await Audit(db, teacherId, "exam.opened", "Exam", id.ToString());
            break;
        case "pause":
            if (exam.Status != "Active") return Results.Conflict(new { message = "لا يمكن إيقاف الامتحان إلا أثناء الجلسة النشطة." });
            exam.Status = "Paused"; exam.PausedAtUtc = now;
            await Audit(db, teacherId, "exam.paused", "Exam", id.ToString());
            break;
        case "resume":
            if (exam.Status != "Paused") return Results.Conflict(new { message = "الجلسة ليست متوقفة حالياً." });
            var pausedSeconds = exam.PausedAtUtc.HasValue ? Math.Max(0, (int)(now - exam.PausedAtUtc.Value).TotalSeconds) : 0;
            var activeSubmissions = await db.Submissions.Where(x => x.ExamId == id && x.Status == "InProgress").ToListAsync();
            foreach (var submission in activeSubmissions) submission.ExtraTimeSeconds += pausedSeconds;
            exam.Status = "Active"; exam.PausedAtUtc = null;
            await db.SaveChangesAsync();
            await Audit(db, teacherId, "exam.resumed", "Exam", id.ToString(), $"pausedSeconds={pausedSeconds}");
            break;
        case "close":
            await CloseExam(db, exam, now);
            await Audit(db, teacherId, "exam.closed", "Exam", id.ToString());
            break;
        default:
            return Results.BadRequest(new { message = "إجراء غير معروف. استخدم open أو pause أو resume أو close." });
    }
    await db.SaveChangesAsync();
    return Results.Ok(new { exam.Id, exam.Status, serverUtc = now });
}).RequireAuthorization();

app.MapPost("/api/exams/enter-code", async (StudentCodeRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var studentId)) return Results.Unauthorized();
    var code = (req?.Code ?? "").Trim();
    if (code.Length != 6 || !code.All(char.IsDigit)) return Results.BadRequest(new { message = "أدخل كود الطالب المكوّن من 6 أرقام." });
    var student = await db.Users.FirstOrDefaultAsync(x => x.Id == studentId && x.Role == "Student");
    if (student is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(student.StudentCode))
    {
        student.StudentCode = await NewStudentCode(db);
        await db.SaveChangesAsync();
    }

    // Only a code explicitly assigned to this student (or the student's own permanent code) is accepted.
    var ownPermanentCode = FixedTimeEquals(code, student.StudentCode);
    ExamParticipant? participant;
    if (ownPermanentCode)
    {
        var publishedExam = await db.Exams
            .Where(x => x.IsPublished && x.Status != "Draft" && x.Status != "Finished")
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync();
        if (publishedExam is null) return Results.Conflict(new { message = "لا يوجد امتحان منشور متاح حالياً." });
        participant = await db.ExamParticipants.Include(x => x.Exam)
            .FirstOrDefaultAsync(x => x.ExamId == publishedExam.Id && x.StudentId == studentId);
        if (participant is null)
        {
            var nowJoined = DateTime.UtcNow;
            participant = new ExamParticipant { ExamId = publishedExam.Id, StudentId = studentId, Code = student.StudentCode, Status = "Waiting", JoinedAtUtc = nowJoined, LastSeenAtUtc = nowJoined, Exam = publishedExam };
            db.ExamParticipants.Add(participant);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex.InnerException))
            {
                // A concurrent request created the same participant; use the winner.
                db.Entry(participant).State = EntityState.Detached;
                participant = await db.ExamParticipants.Include(x => x.Exam)
                    .FirstOrDefaultAsync(x => x.ExamId == publishedExam.Id && x.StudentId == studentId);
                if (participant is null)
                    return Results.Conflict(new { message = "تعذر الانضمام للامتحان بسبب طلب متزامن. أعد المحاولة بعد لحظة." });
            }
        }
        else if (string.IsNullOrWhiteSpace(participant.Code))
        {
            participant.Code = student.StudentCode;
        }
    }
    else
    {
        participant = await db.ExamParticipants.Include(x => x.Exam)
            .FirstOrDefaultAsync(x => x.Code == code && x.StudentId == studentId);
        if (participant is null)
            return Results.NotFound(new { message = "كود الطالب غير صحيح. تأكد من الكود الذي أرسله المدرس." });
    }
    if (participant.StudentId != studentId || (!ownPermanentCode && !FixedTimeEquals(code, participant.Code)))
        return Results.NotFound(new { message = "كود الطالب غير صحيح. تأكد من الكود الذي أرسله المدرس." });
    if (participant.Exam is null)
        return Results.NotFound(new { message = "كود الطالب غير صحيح. تأكد من الكود الذي أرسله المدرس." });
    var exam = participant.Exam;
    if (!exam.IsPublished || exam.Status == "Draft")
        return Results.Conflict(new { message = "لم يبدأ الامتحان بعد. انتظر تعليمات المدرس." });
    if (exam.Status == "Finished")
        return Results.Conflict(new { message = "انتهت جلسة هذا الامتحان. تواصل مع المدرس." });
    var now = DateTime.UtcNow;
    participant.JoinedAtUtc ??= now;
    participant.LastSeenAtUtc = now;
    if (participant.Status is "Invited" or "Absent") participant.Status = "Waiting";
    await db.SaveChangesAsync();
    var submission = await db.Submissions.Where(x => x.ExamId == exam.Id && x.StudentId == studentId).OrderByDescending(x => x.AttemptNumber).FirstOrDefaultAsync();
    var teacherName = await db.Users.Where(x => x.Id == exam.TeacherId).Select(x => x.FullName).FirstOrDefaultAsync();
    return Results.Ok(new
    {
        examId = exam.Id, examTitle = exam.Title, examDescription = exam.Description, teacherName,
        durationMinutes = exam.DurationMinutes, scheduledAtUtc = exam.ScheduledAtUtc, examStatus = exam.Status,
        participantStatus = participant.Status, joinedAtUtc = participant.JoinedAtUtc, submissionId = submission?.Id,
        remainingSeconds = RemainingSeconds(submission, exam, now), serverUtc = now, maxAttempts = exam.MaxAttempts,
        questionCount = await db.Questions.CountAsync(x => x.ExamId == exam.Id),
        studentCode = student.StudentCode
    });
}).RequireAuthorization().RequireRateLimiting("join");

app.MapGet("/api/exams/{id:guid}/session", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var studentId)) return Results.Unauthorized();
    var participant = await db.ExamParticipants.Include(x => x.Exam).FirstOrDefaultAsync(x => x.ExamId == id && x.StudentId == studentId);
    if (participant?.Exam is null) return Results.Forbid();
    if (participant.Status == "Invited") return Results.Conflict(new { message = "أدخل كود الطالب أولاً." });
    var exam = participant.Exam;
    var now = DateTime.UtcNow;
    participant.LastSeenAtUtc = now;
    await db.SaveChangesAsync();
    var submission = await db.Submissions.Where(x => x.ExamId == exam.Id && x.StudentId == studentId).OrderByDescending(x => x.AttemptNumber).FirstOrDefaultAsync();
    var teacherName = await db.Users.Where(x => x.Id == exam.TeacherId).Select(x => x.FullName).FirstOrDefaultAsync();
    return Results.Ok(new
    {
        examId = exam.Id, examTitle = exam.Title, examDescription = exam.Description, teacherName,
        durationMinutes = exam.DurationMinutes, scheduledAtUtc = exam.ScheduledAtUtc, examStatus = exam.Status,
        participantStatus = participant.Status, joinedAtUtc = participant.JoinedAtUtc, submissionId = submission?.Id,
        remainingSeconds = RemainingSeconds(submission, exam, now), serverUtc = now, maxAttempts = exam.MaxAttempts,
        questionCount = await db.Questions.CountAsync(x => x.ExamId == exam.Id)
    });
}).RequireAuthorization();

app.MapGet("/api/exams/{id:guid}/paper", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var studentId)) return Results.Unauthorized();
    var exam = await db.Exams.Include(x => x.Questions).FirstOrDefaultAsync(x => x.Id == id && x.IsPublished);
    if (exam is null) return Results.NotFound();
    if (exam.Status is "Draft" or "Scheduled" or "Waiting") return Results.Conflict(new { message = "لم يبدأ الامتحان بعد. انتظر تعليمات المدرس." });
    if (exam.Status == "Finished") return Results.Conflict(new { message = "انتهت جلسة الامتحان." });
    var participant = await db.ExamParticipants.FirstOrDefaultAsync(x => x.ExamId == id && x.StudentId == studentId);
    if (participant is null || participant.Status == "Invited") return Results.Forbid();
    var submission = await db.Submissions.Include(x => x.Answers).FirstOrDefaultAsync(x => x.ExamId == id && x.StudentId == studentId && x.Status == "InProgress");
    if (submission is null) return Results.Conflict(new { message = "لا توجد محاولة نشطة لهذا الامتحان. ابدأ المحاولة من غرفة الانتظار." });
    var now = DateTime.UtcNow;
    participant.LastSeenAtUtc = now;
    await db.SaveChangesAsync();
    return Results.Ok(new
    {
        examId = exam.Id, examTitle = exam.Title, examDescription = exam.Description, examStatus = exam.Status,
        durationMinutes = exam.DurationMinutes, allowResumeAfterFocusLoss = exam.AllowResumeAfterFocusLoss,
        submission = new { submission.Id, submission.StartedAtUtc, submission.ExtraTimeSeconds, submission.MaxScore },
        remainingSeconds = RemainingSeconds(submission, exam, now), serverUtc = now,
        answers = submission.Answers.Select(a => new { a.QuestionId, a.AnswerText }),
        questions = exam.Questions.OrderBy(q => q.SortOrder).Select(q => new
        {
            q.Id, q.Text, q.Type, q.Points,
            options = q.Type == "TrueFalse" ? new[] { "صح", "خطأ" } : JsonSerializer.Deserialize<string[]>(q.OptionsJson)
        })
    });
}).RequireAuthorization();

app.MapGet("/api/exams/{id:guid}/live", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var exam = await db.Exams.FirstOrDefaultAsync(x => x.Id == id && x.TeacherId == teacherId);
    if (exam is null) return Results.NotFound();
    var now = DateTime.UtcNow;
    var items = await ParticipantViews(db, exam, now);
    return Results.Ok(new
    {
        examId = exam.Id, examTitle = exam.Title, examStatus = exam.Status, serverUtc = now,
        counts = new { total = items.Count, waiting = items.Count(x => x.Status is "Waiting" or "Invited"), active = items.Count(x => x.Status == "Active"), submitted = items.Count(x => x.Status == "Submitted"), disconnected = items.Count(x => x.Status == "Disconnected") },
        items
    });
}).RequireAuthorization();

app.MapGet("/api/exams/join/{code}", async (string code, ClaimsPrincipal p, AppDbContext db) => {
    if (!Guid.TryParse(UserId(p), out var userId)) return Results.Unauthorized();
    var e = await db.Exams.Include(x => x.Questions).FirstOrDefaultAsync(x => x.JoinCode == code && x.IsPublished);
    if (e is null) return Results.NotFound();
    var role = p.FindFirstValue(ClaimTypes.Role);
    if (role == "Teacher")
    {
        if (e.TeacherId != userId) return Results.NotFound();
    }
    else if (role == "Student")
    {
        var isParticipant = await db.ExamParticipants.AnyAsync(x => x.ExamId == e.Id && x.StudentId == userId && x.Status != "Invited");
        if (!isParticipant || e.Status is "Draft" or "Scheduled" or "Waiting" or "Finished") return Results.NotFound();
    }
    else return Results.NotFound();
    return Results.Ok(new { e.Id, e.Title, e.Description, e.DurationMinutes, e.Status,
        Questions = e.Questions.OrderBy(q => q.SortOrder).Select(q => new
        { q.Id, q.Text, q.Type, Options = q.Type == "TrueFalse" ? new[] { "صح", "خطأ" } :
        JsonSerializer.Deserialize<string[]>(q.OptionsJson), q.Points }) });
}).RequireAuthorization().RequireRateLimiting("join");

app.MapPost("/api/exams/{id:guid}/start", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
  await FinalizeExpired(db);
  if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
  if (!Guid.TryParse(UserId(p), out var uid)) return Results.Unauthorized();
  var e = await db.Exams.Include(x => x.Questions).FirstOrDefaultAsync(x => x.Id == id && x.IsPublished); if (e is null) return Results.NotFound();
  if (e.Status == "Paused") return Results.Conflict(new { message = "الامتحان متوقف مؤقتاً من قبل المدرس. انتظر استئناف الجلسة." });
  if (e.Status != "Active") return Results.Conflict(new { message = "لم يفتح المدرس الجلسة بعد. انتظر في غرفة الانتظار حتى يبدأ الامتحان." });
  var participant = await db.ExamParticipants.FirstOrDefaultAsync(x => x.ExamId == id && x.StudentId == uid);
  if (participant is null || participant.Status == "Invited")
    return Results.Forbid();
  var now = DateTime.UtcNow;
  var existing = await db.Submissions.FirstOrDefaultAsync(s => s.ExamId == id && s.StudentId == uid && s.Status == "InProgress");
  if (existing is not null)
  {
    if (!e.AllowResumeAfterFocusLoss && existing.FocusLossDetected) return Results.Conflict(new { message = "تم إيقاف استكمال المحاولة بعد مغادرة الامتحان وفقاً لإعدادات المدرس. تواصل مع المدرس إذا كان الخروج بسبب ظرف طارئ." });
    participant.Status = "Active"; participant.LastSeenAtUtc = now; await db.SaveChangesAsync();
    return Results.Ok(new { existing.Id, existing.StartedAtUtc, e.DurationMinutes, existing.ExtraTimeSeconds, remainingSeconds = RemainingSeconds(existing, e, now), serverUtc = now, examStatus = e.Status });
  }
  var attemptsUsed = await db.Submissions.CountAsync(s => s.ExamId == id && s.StudentId == uid && s.Status != "InProgress" && !s.IsExceptionAttempt);
  var attemptNumber = await db.Submissions.CountAsync(s => s.ExamId == id && s.StudentId == uid) + 1;
  if (attemptsUsed >= e.MaxAttempts) return Results.Conflict(new { message = "لقد استنفدت عدد المحاولات المتاحة لهذا الامتحان." });
  var sub = new Submission { ExamId = id, StudentId = uid, AttemptNumber = attemptNumber, MaxScore = e.Questions.Sum(q => q.Points), StartedAtUtc = now };
  try
  {
    db.Submissions.Add(sub);
    participant.Status = "Active"; participant.JoinedAtUtc ??= now; participant.LastSeenAtUtc = now;
    await db.SaveChangesAsync();
  }
  catch (DbUpdateException ex) when (IsUniqueViolation(ex.InnerException))
  {
    db.ChangeTracker.Clear();
    var raced = await db.Submissions.FirstOrDefaultAsync(s => s.ExamId == id && s.StudentId == uid && s.Status == "InProgress");
    return raced is null
      ? Results.Conflict(new { message = "تعذر بدء المحاولة بسبب محاولة أخرى قيد التنفيذ. أعد المحاولة بعد لحظة." })
      : Results.Ok(new { raced.Id, raced.StartedAtUtc, e.DurationMinutes, raced.ExtraTimeSeconds, remainingSeconds = RemainingSeconds(raced, e, now), serverUtc = now, examStatus = e.Status });
  }
  await Audit(db, uid, "submission.started", "Submission", sub.Id.ToString());
  return Results.Ok(new { sub.Id, sub.StartedAtUtc, e.DurationMinutes, sub.ExtraTimeSeconds, remainingSeconds = RemainingSeconds(sub, e, now), serverUtc = now, examStatus = e.Status });
}).RequireAuthorization().RequireRateLimiting("api");
app.MapPut("/api/submissions/{id:guid}/answers", async (Guid id, SubmitRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
  if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
  if (!Guid.TryParse(UserId(p), out var uid)) return Results.Unauthorized();
  await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
  await AcquireSubmissionLock(db, id);
  var sub = await db.Submissions.Include(s => s.Answers).FirstOrDefaultAsync(s => s.Id == id && s.StudentId == uid);
  if (sub is null) return Results.NotFound();
  if (sub.Status != "InProgress") return Results.Conflict(new { message = "تم تسليم هذه المحاولة بالفعل؛ لم يعد حفظ الإجابات متاحاً." });
  var examInfo = await db.Exams.Where(e => e.Id == sub.ExamId).Select(e => new { e.DurationMinutes, e.Status }).FirstOrDefaultAsync();
  if (examInfo is null) return Results.NotFound();
  if (examInfo.Status is "Paused" or "Finished") return Results.Conflict(new { message = examInfo.Status == "Paused" ? "الامتحان متوقف مؤقتاً. ستُستأنف الجلسة عند سماح المدرس." : "انتهت الجلسة؛ لم تعد الإجابات قابلة للتعديل." });
  if (DateTime.UtcNow >= ExamGrader.Deadline(sub, examInfo.DurationMinutes)) return Results.BadRequest(new { message = "انتهى وقت الامتحان؛ تم إيقاف حفظ الإجابات الجديدة." });
  if (req.FocusLossDetected && !sub.FocusLossDetected) { sub.FocusLossCount++; sub.FocusLossDetected = true; }
  var validQuestionIds = await db.Questions.Where(q => q.ExamId == sub.ExamId).Select(q => q.Id).ToListAsync();
  foreach (var a in (req.Answers ?? []).Where(a => validQuestionIds.Contains(a.QuestionId)).GroupBy(a => a.QuestionId).Select(g => g.Last()))
  {
    var answer = sub.Answers.FirstOrDefault(x => x.QuestionId == a.QuestionId);
    if (answer is null)
    {
      sub.Answers.Add(new SubmissionAnswer { Id = Guid.Empty, SubmissionId = sub.Id, QuestionId = a.QuestionId, AnswerText = a.AnswerText ?? "" });
    }
    else answer.AnswerText = a.AnswerText ?? "";
  }
  await db.SaveChangesAsync(); await tx.CommitAsync(); return Results.Ok(new { saved = true, sub.FocusLossDetected, serverUtc = DateTime.UtcNow });
}).RequireAuthorization();
app.MapPost("/api/submissions/{id:guid}/submit", async (Guid id, SubmitRequest? req, ClaimsPrincipal p, AppDbContext db) =>
{
  if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
  if (!Guid.TryParse(UserId(p), out var uid)) return Results.Unauthorized();
  await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
  await AcquireSubmissionLock(db, id);
  var claimed = await db.Submissions
      .Where(s => s.Id == id && s.StudentId == uid && s.Status == "InProgress")
      .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.Status, "Processing"));
  if (claimed == 0)
  {
    var alreadySubmitted = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.StudentId == uid);
    if (alreadySubmitted is null) return Results.NotFound();
    if (alreadySubmitted.Status == "Processing")
      return Results.Conflict(new { message = "التسليم قيد المعالجة بالفعل. انتظر لحظة ثم حدّث الصفحة." });
    if (alreadySubmitted.Status == "InProgress")
      return Results.Conflict(new { message = "تعذر قفل المحاولة. أعد المحاولة بعد لحظة." });
    return Results.Ok(new { alreadySubmitted.Id, alreadySubmitted.Score, alreadySubmitted.MaxScore, alreadySubmitted.Status });
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
        sub.Answers.Add(new SubmissionAnswer { Id = Guid.Empty, SubmissionId = sub.Id, QuestionId = input.QuestionId, AnswerText = input.AnswerText ?? "" });
      else
        existing.AnswerText = input.AnswerText ?? "";
    }
    if (req?.FocusLossDetected == true && !sub.FocusLossDetected) sub.FocusLossCount++;
    if (req?.FocusLossDetected == true) sub.FocusLossDetected = true;
  }
  ExamGrader.Grade(sub, qs);
  sub.SubmittedAtUtc = isLate ? deadline : DateTime.UtcNow;
  var participant = await db.ExamParticipants.FirstOrDefaultAsync(x => x.ExamId == sub.ExamId && x.StudentId == uid);
  if (participant is not null) { participant.Status = "Submitted"; participant.LastSeenAtUtc = DateTime.UtcNow; }
  await db.SaveChangesAsync(); await tx.CommitAsync();
  await Audit(db, uid, "submission.submitted", "Submission", id.ToString(), isLate ? "late=true" : null);
  return Results.Ok(new { sub.Id, sub.Score, sub.MaxScore, sub.Status });
}).RequireAuthorization();
app.MapGet("/api/results/mine", async (ClaimsPrincipal p, AppDbContext db) =>
{
  await FinalizeExpired(db);
  if (!Guid.TryParse(UserId(p), out var studentId)) return Results.Unauthorized();
  var submissions = await db.Submissions.Where(s => s.StudentId == studentId && s.Status != "InProgress" && s.Status != "Abandoned").OrderByDescending(s => s.SubmittedAtUtc).Select(s => new { s.Id, s.ExamId, ExamTitle = s.Exam!.Title, TeacherName = db.Users.Where(u => u.Id == s.Exam.TeacherId).Select(u => u.FullName).FirstOrDefault(), Subject = s.Exam.Description, Score = s.Status == "Submitted" || s.Status == "Reviewed" ? s.Score : (decimal?)null, s.MaxScore, s.Status, s.SubmittedAtUtc, s.StartedAtUtc, s.AttemptNumber, s.FocusLossDetected, s.FocusLossCount, s.CopyAttemptCount, s.ScreenshotAttemptCount }).ToListAsync();
  return Results.Ok(submissions.Select(s => new { s.Id, s.ExamId, s.ExamTitle, s.TeacherName, s.Subject, s.Score, s.MaxScore, s.Status, s.SubmittedAtUtc, s.StartedAtUtc, DurationSeconds = s.SubmittedAtUtc.HasValue ? (int)(s.SubmittedAtUtc.Value - s.StartedAtUtc).TotalSeconds : (int?)null, s.AttemptNumber, s.FocusLossDetected, s.FocusLossCount, s.CopyAttemptCount, s.ScreenshotAttemptCount }).ToList());
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
  var q = db.Submissions.Where(s => s.ExamId == id && s.Status != "InProgress"); var total = await q.CountAsync(); var items = await q.OrderByDescending(s => s.SubmittedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).Select(s => new { s.Id, Student = s.Student!.FullName, Email = s.Student.Email, s.Score, s.MaxScore, s.Status, s.SubmittedAtUtc, s.FocusLossDetected, s.IsExceptionAttempt }).ToListAsync(); return Results.Ok(new { items, page, pageSize, total });
}).RequireAuthorization();
app.MapPost("/api/submissions/{id:guid}/grant-extra-attempt", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
  if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
  if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
  await FinalizeExpired(db);
  await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
  await AcquireSubmissionLock(db, id);
  var submission = await db.Submissions.Include(x => x.Exam).FirstOrDefaultAsync(x => x.Id == id);
  if (submission is null) return Results.NotFound(new { message = "لم نعثر على محاولة الطالب المطلوبة." });
  if (submission.Exam?.TeacherId != teacherId) return Results.Forbid();
  if (submission.Status == "Processing") return Results.Conflict(new { message = "التسليم قيد المعالجة. انتظر لحظة ثم أعد المحاولة." });
  if (submission.Status == "InProgress" && !submission.FocusLossDetected) return Results.Conflict(new { message = "الطالب لديه محاولة مفتوحة ولم يُرصد خروجه منها. لا يمكن منح استثناء في هذه الحالة." });
  if (submission.IsExceptionAttempt) return Results.Conflict(new { message = "تم منح محاولة إضافية لهذه المحاولة بالفعل. حدّث القائمة قبل المحاولة مرة أخرى." });
  if (submission.Status != "InProgress") {
    var latest = await db.Submissions.Where(x => x.ExamId == submission.ExamId && x.StudentId == submission.StudentId && x.Status != "InProgress").OrderByDescending(x => x.SubmittedAtUtc).Select(x => x.Id).FirstOrDefaultAsync();
    if (latest != submission.Id) return Results.Conflict(new { message = "يمكن منح الاستثناء للمحاولة الأخيرة فقط." });
  }
  submission.IsExceptionAttempt = true;
  if (submission.Status == "InProgress") { submission.Status = "Abandoned"; submission.SubmittedAtUtc = DateTime.UtcNow; }
  await db.SaveChangesAsync();
  await tx.CommitAsync();
  await Audit(db, teacherId, "submission.extra-attempt-granted", "Submission", id.ToString());
  return Results.Ok(new { message = "تم منح الطالب محاولة إضافية استثنائية واحدة.", submissionId = id });
}).RequireAuthorization();

app.MapPut("/api/submissions/{id:guid}/answers/{answerId:guid}/grade", async (Guid id, Guid answerId, EssayGradeRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
  if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
  if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
  if (req is null || (req.Feedback?.Length ?? 0) > 2000) return Results.BadRequest(new { message = "بيانات التصحيح غير صالحة." });
  await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
  await AcquireSubmissionLock(db, id);
  var sub = await db.Submissions.Include(s => s.Answers).FirstOrDefaultAsync(s => s.Id == id);
  if (sub is null) return Results.NotFound();
  var exam = await db.Exams.FirstOrDefaultAsync(e => e.Id == sub.ExamId && e.TeacherId == teacherId);
  if (exam is null) return Results.Forbid();
  if (sub.Status is not ("NeedsReview" or "Reviewed")) return Results.BadRequest(new { message = "لا يمكن تعديل درجة محاولة غير مسلّمة." });
  var a = sub.Answers.FirstOrDefault(x => x.Id == answerId && x.SubmissionId == id);
  if (a is null) return Results.NotFound();
  var q = await db.Questions.FirstOrDefaultAsync(x => x.Id == a.QuestionId && x.ExamId == sub.ExamId);
  if (q is null) return Results.NotFound();
  if (q.Type != "Essay") return Results.BadRequest(new { message = "يمكن تصحيح الأسئلة المقالية فقط." });
  if (req.AwardedPoints < 0 || req.AwardedPoints > q.Points) return Results.BadRequest(new { message = "الدرجة خارج النطاق." });
  var previousPoints = a.AwardedPoints;
  a.AwardedPoints = req.AwardedPoints;
  a.TeacherFeedback = req.Feedback;
  sub.Score = sub.Answers.Sum(x => x.AwardedPoints ?? 0);
  sub.MaxScore = await db.Questions.Where(x => x.ExamId == sub.ExamId).SumAsync(x => x.Points);
  var essayQuestionIds = await db.Questions.Where(x => x.ExamId == sub.ExamId && x.Type == "Essay").Select(x => x.Id).ToListAsync();
  sub.Status = sub.Answers.Any(x => essayQuestionIds.Contains(x.QuestionId) && x.AwardedPoints == null) ? "NeedsReview" : "Reviewed";
  await db.SaveChangesAsync();
  await tx.CommitAsync();
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

if (!app.Environment.IsProduction())
{
    app.MapPost("/api/admin/reset", async (ClaimsPrincipal p, AppDbContext db) =>
    {
        var role = p.FindFirstValue(ClaimTypes.Role);
        if (role != "Admin") return Results.Forbid();
        if (!Guid.TryParse(UserId(p), out var actorId)) return Results.Unauthorized();
        var activeSubmissions = await db.Submissions
            .Where(s => s.Status == "InProgress")
            .Select(s => s.Id)
            .ToListAsync();
        foreach (var id in activeSubmissions)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM \"Answers\" WHERE \"SubmissionId\" = '{id}'");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM \"Submissions\" WHERE \"Id\" = '{id}'");
        }
        var activeParticipants = await db.ExamParticipants
            .Where(p => new[] { "Waiting", "Active", "Invited" }.Contains(p.Status))
            .Select(p => p.Id)
            .ToListAsync();
        foreach (var id in activeParticipants)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM \"ExamParticipants\" WHERE \"Id\" = '{id}'");
        }
        await Audit(db, actorId, "admin.reset", "Admin", null, $"clearedSubmissions={activeSubmissions.Count}, clearedParticipants={activeParticipants.Count}");
        return Results.Ok(new { message = "تم إعادة تعيين البيانات النشطة بنجاح.", clearedSubmissions = activeSubmissions.Count, clearedParticipants = activeParticipants.Count });
    }).RequireAuthorization();
}

app.MapGet("/api/audit", async (ClaimsPrincipal p, AppDbContext db, int page = 1, int pageSize = 50) => { if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid(); if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized(); page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100); var q = db.AuditEvents.AsNoTracking().Where(x => x.ActorId == teacherId).OrderByDescending(x => x.AtUtc); var total = await q.CountAsync(); return Results.Ok(new { items = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(), page, pageSize, total }); }).RequireAuthorization();

app.MapPost("/api/submissions/{id:guid}/events", async (Guid id, SubmissionEventRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
  if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
  if (!Guid.TryParse(UserId(p), out var uid)) return Results.Unauthorized();
  var eventType = (req.Type ?? "").Trim().ToLowerInvariant();
  if (eventType is not ("focus-loss" or "copy" or "screenshot"))
    return Results.BadRequest(new { message = "نوع الحدث غير معروف." });
  await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
  await AcquireSubmissionLock(db, id);
  var sub = await db.Submissions.FirstOrDefaultAsync(s => s.Id == id && s.StudentId == uid && s.Status == "InProgress");
  if (sub is null) return Results.NotFound(new { message = "المحاولة غير موجودة أو تم إنهاؤها." });
  switch (eventType) {
    case "focus-loss": sub.FocusLossCount++; sub.FocusLossDetected = true; break;
    case "copy": sub.CopyAttemptCount++; break;
    case "screenshot": sub.ScreenshotAttemptCount++; break;
  }
  await db.SaveChangesAsync();
  await tx.CommitAsync();
  await Audit(db, uid, "exam.event." + eventType, "Submission", id.ToString());
  return Results.Ok(new { recorded = true });
}).RequireAuthorization();

app.MapGet("/api/submissions/{id:guid}/review", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
  if (!Guid.TryParse(UserId(p), out var uid)) return Results.Unauthorized();
  var role = p.FindFirstValue(ClaimTypes.Role);
  if (role is not ("Student" or "Teacher")) return Results.Forbid();
  await FinalizeExpired(db);
  var sub = await db.Submissions.Include(s => s.Exam).Include(s => s.Student).Include(s => s.Answers).FirstOrDefaultAsync(s => s.Id == id);
  if (sub is null) return Results.NotFound(new { message = "المحاولة المطلوبة غير موجودة." });
  if (role == "Student" ? sub.StudentId != uid : sub.Exam!.TeacherId != uid) return Results.Forbid();
  var qs = await db.Questions.Where(q => q.ExamId == sub.ExamId).OrderBy(q => q.SortOrder).ToListAsync();
  var elapsed = (int)((sub.SubmittedAtUtc ?? DateTime.UtcNow) - sub.StartedAtUtc).TotalSeconds;
  var showScore = role == "Teacher" || sub.Status is "Submitted" or "NeedsReview" or "Reviewed";
  return Results.Ok(new { sub.Id, sub.ExamId, ExamTitle = sub.Exam!.Title, TeacherName = await db.Users.Where(u => u.Id == sub.Exam.TeacherId).Select(u => u.FullName).FirstOrDefaultAsync(), StudentName = sub.Student!.FullName, sub.Status, Score = showScore ? sub.Score : (decimal?)null, MaxScore = showScore ? sub.MaxScore : (decimal?)null, sub.StartedAtUtc, sub.SubmittedAtUtc, DurationSeconds = elapsed, sub.AttemptNumber, sub.FocusLossCount, sub.CopyAttemptCount, sub.ScreenshotAttemptCount, Answers = qs.Select(q => { var a = sub.Answers.FirstOrDefault(x => x.QuestionId == q.Id); return new { Id = a?.Id, QuestionId = q.Id, q.Text, q.Type, Options = JsonSerializer.Deserialize<string[]>(q.OptionsJson), CorrectAnswer = showScore ? q.CorrectAnswer : null, q.Points, StudentAnswer = a?.AnswerText ?? "", AwardedPoints = showScore ? a?.AwardedPoints : null, a?.TeacherFeedback }; }) });
}).RequireAuthorization();

app.Run();
