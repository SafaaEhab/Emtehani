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
using Emtehani.API;
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

// Forwarded headers are a security boundary, not a convenience. The previous
// configuration trusted every proxy, so any client could rotate X-Forwarded-For
// and receive a fresh rate-limit partition. X-Forwarded-Proto is safe to accept
// for HTTPS detection, but X-Forwarded-For is only honoured when an operator has
// explicitly listed the hosting proxy addresses that are allowed to supply it.
var trustForwardedFor = builder.Configuration.GetValue("Proxy:TrustForwardedFor", false);
var knownProxies = builder.Configuration.GetSection("Proxy:KnownProxies").Get<string[]>() ?? [];
var knownNetworks = builder.Configuration.GetSection("Proxy:KnownNetworks").Get<string[]>() ?? [];
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = trustForwardedFor
        ? ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        : ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    if (!trustForwardedFor) return;

    if (knownProxies.Length == 0 && knownNetworks.Length == 0)
        throw new InvalidOperationException(
            "Proxy:TrustForwardedFor=true requires Proxy:KnownProxies or Proxy:KnownNetworks. Refusing to trust arbitrary X-Forwarded-For headers.");

    foreach (var proxy in knownProxies)
    {
        if (!IPAddress.TryParse(proxy.Trim(), out var address))
            throw new InvalidOperationException($"Proxy:KnownProxies contains an invalid IP address: {proxy}");
        options.KnownProxies.Add(address);
    }
    foreach (var network in knownNetworks)
    {
        var parts = network.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var prefix) || !int.TryParse(parts[1], out var prefixLength))
            throw new InvalidOperationException($"Proxy:KnownNetworks contains an invalid CIDR range: {network}");
        options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, prefixLength));
    }
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
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Emtehani.API.LoginThrottle>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    var apiPermitLimit = Math.Max(1, builder.Configuration.GetValue("RateLimiting:ApiPermitLimit", 120));
    var authPermitLimit = Math.Max(1, builder.Configuration.GetValue("RateLimiting:AuthPermitLimit", 60));
    var joinPermitLimit = Math.Max(1, builder.Configuration.GetValue("RateLimiting:JoinPermitLimit", 10));
    var lookupPermitLimit = Math.Max(1, builder.Configuration.GetValue("RateLimiting:LookupPermitLimit", 12));
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
    // Phone lookup is an enumeration surface; keep it per teacher, not per IP.
    o.AddPolicy("lookup", context => Fixed(context, lookupPermitLimit, perUser: true));
    // Fallback protection for every API route, including the few endpoints that
    // intentionally do not opt into a stricter named policy.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        context.Request.Path.StartsWithSegments("/api")
            ? Fixed(context, apiPermitLimit)
            : RateLimitPartition.GetNoLimiter("non-api"));
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
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new UtcDateTimeJsonConverter()));
var swaggerEnabled = builder.Configuration.GetValue("Swagger:Enabled", builder.Environment.IsDevelopment());
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
    o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
});
var app = builder.Build();
app.Logger.LogInformation("Forwarded X-Forwarded-For trust enabled: {Trusted}", trustForwardedFor);
if (builder.Configuration.GetValue("Database:BootstrapOnStartup", true))
{
    var startupTimer = System.Diagnostics.Stopwatch.StartNew();
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    await ExamWorkflowSchema.EnsureAsync(db);
    await PlatformDataUpgrade.EnsureAsync(db);
    startupTimer.Stop();
    app.Logger.LogInformation("Database bootstrap completed in {ElapsedMs} ms.", startupTimer.ElapsedMilliseconds);
}
else
{
    app.Logger.LogWarning("Database bootstrap skipped by Database:BootstrapOnStartup=false. Schema upgrades must be applied by the deployment pipeline.");
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
// The OpenAPI document is public discovery metadata and contains no secrets.
// The interactive Swagger UI stays behind Swagger:Enabled (development only).
app.UseSwagger();
if (swaggerEnabled) { app.UseSwaggerUI(); }
app.UseCors("frontend");
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
// A teacher account with a Pending subscription may only read/confirm its plan and
// use authentication/public endpoints. Every teacher workspace API is server-gated
// here so the restriction cannot be bypassed from the browser.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var isTeacher = context.User.Identity?.IsAuthenticated == true
        && context.User.FindFirstValue(ClaimTypes.Role) == "Teacher";
    if (!isTeacher || !path.StartsWithSegments("/api"))
    {
        await next();
        return;
    }
    var exempt = path == "/api"
        || path.StartsWithSegments("/api/auth")
        || path.StartsWithSegments("/api/public")
        || path.StartsWithSegments("/api/teacher/subscription")
        || path.StartsWithSegments("/api/subscriptions")
        || path.StartsWithSegments("/api/admin");
    if (exempt)
    {
        await next();
        return;
    }
    if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var teacherId))
    {
        await next();
        return;
    }
    var db = context.RequestServices.GetRequiredService<AppDbContext>();
    var confirmed = await db.TeacherSubscriptions.AsNoTracking()
        .AnyAsync(x => x.TeacherId == teacherId
            && x.Status == "Confirmed"
            && (x.EndsAtUtc == null || x.EndsAtUtc > DateTime.UtcNow));
    if (!confirmed)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            message = "اشتراكك بانتظار التأكيد. اختر باقة Free أولاً لاستخدام مساحة المدرس.",
            code = "subscription_pending"
        });
        return;
    }
    await next();
});
app.MapHealthChecks("/health");
app.MapGet("/api", () => Results.Ok(new { service = "Emtehani API", version = "1.0" }));
// Public directory used only for choosing teachers during student registration. It never
// exposes students, answer keys, or private data, and registration still validates IDs.
app.MapGet("/api/public/teachers", async (AppDbContext db, string? search) =>
{
    var term = (search ?? "").Trim();
    var rows = await db.Users.AsNoTracking()
        .Where(x => x.Role == "Teacher" && x.EmailConfirmed)
        .Where(x => term.Length == 0 || x.FullName.Contains(term))
        .OrderBy(x => x.FullName)
        .Take(200)
        .Select(x => new { id = x.Id, fullName = x.FullName })
        .ToListAsync();
    return Results.Ok(rows);
}).RequireRateLimiting("api");
app.MapGet("/api/public/invitations/{token}", async (string token, AppDbContext db) =>
{
    var invitation = await db.TeacherStudentInvitations.AsNoTracking()
        .FirstOrDefaultAsync(x => x.Token == token);
    if (invitation is null)
        return Results.NotFound(new { message = "رابط الدعوة غير صحيح." });
    if (invitation.Status != "Pending")
        return Results.Conflict(new { message = "تم استخدام رابط الدعوة بالفعل. سجّل الدخول إلى حسابك أو اطلب دعوة جديدة." });
    if (invitation.ExpiresAtUtc <= DateTime.UtcNow)
        return Results.Conflict(new { message = "انتهت صلاحية رابط الدعوة. اطلب دعوة جديدة من المدرس." });
    var teacher = await db.Users.AsNoTracking()
        .Where(x => x.Id == invitation.TeacherId)
        .Select(x => new { x.Id, x.FullName })
        .FirstOrDefaultAsync();
    return Results.Ok(new
    {
        invitation.StudentName,
        invitation.StudentPhone,
        invitation.ParentPhone,
        invitation.ParentName,
        invitation.Stage,
        invitation.Grade,
        invitation.ExpiresAtUtc,
        teacherId = invitation.TeacherId,
        teacherName = teacher?.FullName
    });
}).RequireRateLimiting("api");
app.MapPublicMcpServer();
string Hash(string password, string salt) => Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt), 120000, HashAlgorithmName.SHA256, 32));
string Salt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
static string? PasswordError(string? password)
{
    if (string.IsNullOrEmpty(password) || password.Length < 10 || password.Length > 128)
        return "كلمة المرور يجب أن تكون بين 10 و128 حرفاً.";
    if (!string.Equals(password, password.Trim(), StringComparison.Ordinal))
        return "لا تبدأ أو تنتهي كلمة المرور بمسافات.";
    var common = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "1234567890", "qwertyuiop",
        "admin12345", "iloveyou", "welcome123", "letmein123", "abcdefghij"
    };
    if (common.Contains(password))
        return "كلمة المرور شائعة جداً. اختر عبارة أطول وغير متوقعة.";
    return null;
}
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
static DateTime? ToUtc(DateTime? value)
{
    if (value is null) return null;
    var v = value.Value;
    return v.Kind switch
    {
        DateTimeKind.Utc => v,
        DateTimeKind.Local => v.ToUniversalTime(),
        _ => DateTime.SpecifyKind(v, DateTimeKind.Utc)
    };
}
static string? NormalizeResultsReleaseMode(string? value)
{
    var mode = (value ?? "").Trim();
    if (mode.Length == 0) return null;
    if (mode.Equals("AfterSubmission", StringComparison.OrdinalIgnoreCase)) return "AfterSubmission";
    if (mode.Equals("AfterExamEnd", StringComparison.OrdinalIgnoreCase)) return "AfterExamEnd";
    return "invalid";
}
static string HashRefreshToken(string rawToken) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
static string IssueRefreshToken(AppUser user, DateTime sessionExpiresAtUtc)
{
    var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    user.RefreshTokenHash = HashRefreshToken(raw);
    user.RefreshTokenCreatedAtUtc = DateTime.UtcNow;
    // The refresh token can never outlive the fixed 24-hour login session.
    user.RefreshTokenExpiresUtc = sessionExpiresAtUtc;
    return raw;
}
static DateTime NewSessionExpiry() => DateTime.UtcNow.AddHours(24);
static string[] SafeJsonArray(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return [];
    try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
    catch (JsonException) { return []; }
}
static string MaskStudentName(string? fullName)
{
    var parts = (fullName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) return "طالب";
    if (parts.Length == 1) return parts[0].Length <= 2 ? parts[0][..1] + "…" : parts[0][..1] + "…";
    return parts[0] + " " + parts[1][..1] + "…";
}
async Task<int> AcceptedTeacherCount(AppDbContext db, Guid studentId) =>
    await db.TeacherStudentRequests.CountAsync(x => x.StudentId == studentId
        && x.Status == "Accepted" && x.UnlinkedAtUtc == null);
async Task<TeacherSubscription?> CurrentSubscription(AppDbContext db, Guid teacherId) =>
    await db.TeacherSubscriptions.AsNoTracking()
        .Include(x => x.Plan)
        .Where(x => x.TeacherId == teacherId)
        .OrderByDescending(x => x.Status == "Confirmed")
        .ThenByDescending(x => x.UpdatedAtUtc)
        .FirstOrDefaultAsync();
async Task<(string? Error, int Status, bool Changed)> ApplyStudentProfile(
    AppDbContext db, AppUser student, StudentProfileUpdateRequest req, Guid actorId, string actorRole, string source)
{
    var fullName = IdentityNormalizer.NormalizeDisplayName(req?.FullName);
    var stage = req?.Stage?.Trim();
    var grade = req?.Grade?.Trim();
    if (fullName.Length is < 3 or > 120)
        return ("أدخل اسماً صحيحاً بين 3 و120 حرفاً.", StatusCodes.Status400BadRequest, false);
    // Legacy accounts may have a three-part name. They must be able to update their
    // phone/grade/guardian data without being forced to rename themselves; if they
    // change the name, the four-part rule applies immediately.
    var nameChanged = !string.Equals(
        IdentityNormalizer.NormalizeName(student.FullName),
        IdentityNormalizer.NormalizeName(fullName),
        StringComparison.Ordinal);
    if (nameChanged)
    {
        var nameError = IdentityNormalizer.StudentFullNameError(fullName);
        if (nameError is not null)
            return (nameError, StatusCodes.Status400BadRequest, false);
    }
    var parentName = IdentityNormalizer.NormalizeDisplayName(req?.ParentName);
    var parentNameError = IdentityNormalizer.ParentNameError(parentName);
    if (parentNameError is not null)
        return (parentNameError, StatusCodes.Status400BadRequest, false);
    if (!EducationCatalog.IsValid(stage, grade))
        return ("اختر المرحلة التعليمية والصف الدراسي المناسب لها.", StatusCodes.Status400BadRequest, false);
    var studentPhone = IdentityNormalizer.NormalizePhone(req?.StudentPhone);
    var parentPhone = IdentityNormalizer.NormalizePhone(req?.ParentPhone);
    if (studentPhone is null)
        return (IdentityNormalizer.PhoneConflictMessage("student"), StatusCodes.Status400BadRequest, false);
    if (parentPhone is null)
        return (IdentityNormalizer.PhoneConflictMessage("parent"), StatusCodes.Status400BadRequest, false);
    if (studentPhone == parentPhone)
        return ("رقم هاتف الطالب يجب أن يختلف عن رقم هاتف ولي الأمر.", StatusCodes.Status400BadRequest, false);
    var normalizedName = IdentityNormalizer.NormalizeName(fullName);
    if (normalizedName.Length == 0)
        return ("أدخل اسماً صحيحاً يحتوي على حروف.", StatusCodes.Status400BadRequest, false);
    if (nameChanged && await db.Users.AnyAsync(x => x.Id != student.Id && x.Role == "Student" && x.NormalizedName == normalizedName))
        return ("يوجد حساب طالب آخر مسجل بنفس الاسم. لا يمكن تكرار اسم الطالب.", StatusCodes.Status409Conflict, false);
    if (await db.Users.AnyAsync(x => x.Id != student.Id && x.StudentPhone == studentPhone))
        return ("رقم هاتف الطالب مستخدم بالفعل في حساب آخر.", StatusCodes.Status409Conflict, false);

    var changed = student.Stage != stage || student.Grade != grade;
    if (changed)
    {
        db.StudentAcademicChanges.Add(new StudentAcademicChange
        {
            StudentId = student.Id,
            PreviousStage = student.Stage,
            PreviousGrade = student.Grade,
            NewStage = stage,
            NewGrade = grade,
            ChangedByUserId = actorId,
            ChangedByRole = actorRole,
            Source = source
        });
    }
    student.FullName = fullName;
    if (nameChanged) student.NormalizedName = normalizedName;
    student.StudentPhone = studentPhone;
    student.ParentPhone = parentPhone;
    if (parentName.Length > 0) student.ParentName = parentName;
    student.Stage = stage;
    student.Grade = grade;
    student.UpdatedAtUtc = DateTime.UtcNow;
    return (null, StatusCodes.Status200OK, changed);
}

// Optional, config-gated admin bootstrap. It never exposes a public signup and never
// creates a second Admin; production can omit these settings entirely.
var bootstrapAdminEmail = builder.Configuration["Admin:BootstrapEmail"]?.Trim().ToLowerInvariant();
var bootstrapAdminPassword = builder.Configuration["Admin:BootstrapPassword"];
if (!string.IsNullOrWhiteSpace(bootstrapAdminEmail) && !string.IsNullOrWhiteSpace(bootstrapAdminPassword)
    && bootstrapAdminPassword.Length is >= 8 and <= 128)
{
    using var bootstrapScope = app.Services.CreateScope();
    var bootstrapDb = bootstrapScope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (!await bootstrapDb.Users.AnyAsync(x => x.Role == "Admin"))
    {
        if (!await bootstrapDb.Users.AnyAsync(x => x.Email == bootstrapAdminEmail))
        {
            var bootstrapSalt = Salt();
            bootstrapDb.Users.Add(new AppUser
            {
                FullName = "مدير المنصة",
                NormalizedName = IdentityNormalizer.NormalizeName("مدير المنصة"),
                Email = bootstrapAdminEmail,
                Role = "Admin",
                RegistrationSource = "Admin",
                EmailConfirmed = true,
                PasswordHash = bootstrapSalt + ":" + Hash(bootstrapAdminPassword, bootstrapSalt)
            });
            await bootstrapDb.SaveChangesAsync();
            app.Logger.LogInformation("Admin bootstrap account created from configuration.");
        }
    }
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
    if (db.Database.IsNpgsql())
    {
        var key = SubmissionLockKey(submissionId);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})");
        return;
    }

    if (db.Database.IsSqlServer())
    {
        var resource = $"submission:{submissionId}";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sp_getapplock
                @Resource = {resource},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 30000;
            IF @result < 0
                THROW 51000, 'Could not acquire the submission lock.', 1;
            """);
    }
}
static DateTime EffectiveDeadline(Submission submission, Exam exam, DateTime now)
{
    var deadline = ExamGrader.Deadline(submission, exam.DurationMinutes, exam.AvailableUntilUtc);
    if (exam.Status == "Paused" && exam.PausedAtUtc.HasValue)
        deadline = deadline.Add(now - exam.PausedAtUtc.Value);
    return deadline;
}
static int? RemainingSeconds(Submission? submission, Exam exam, DateTime now)
{
    if (submission is null) return null;
    if (submission.Status != "InProgress") return 0;
    return Math.Max(0, (int)Math.Ceiling((EffectiveDeadline(submission, exam, now) - now).TotalSeconds));
}
// A Submission row is only created when an attempt actually starts. Refreshing or
// reopening the page never creates a new row, so it cannot consume an extra attempt.
// Exception attempts are bonus attempts granted by the teacher and are reported
// separately without reducing MaxAttempts. Abandoned rows are technical/administrative
// invalidations (duplicate cleanup or a granted exception), never a student's completed
// attempt, so they do not consume MaxAttempts either. Used and Completed are still
// deliberately different numbers because InProgress is used before it is completed.
static AttemptSummaryDto AttemptSummaryFor(int maxAttempts, IEnumerable<Submission> submissions)
{
    var rows = submissions as IReadOnlyCollection<Submission> ?? submissions.ToList();
    var used = rows.Count(x => !x.IsExceptionAttempt && x.Status != "Abandoned");
    var completed = rows.Count(x => !x.IsExceptionAttempt
        && x.Status is "Submitted" or "Reviewed" or "NeedsReview" or "Processing");
    var inProgress = rows.Count(x => x.Status == "InProgress");
    var abandoned = rows.Count(x => x.Status == "Abandoned" && !x.IsExceptionAttempt);
    var exceptionUsed = rows.Count(x => x.IsExceptionAttempt);
    var allowed = Math.Max(1, maxAttempts);
    return new AttemptSummaryDto(
        Allowed: allowed,
        Used: used,
        Completed: completed,
        Remaining: Math.Max(0, allowed - used),
        InProgress: inProgress,
        Abandoned: abandoned,
        ExceptionUsed: exceptionUsed,
        Total: rows.Count);
}

static string? CleanProfileUrl(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;
    var value = raw.Trim();
    if (value.Length > 500) return "invalid";
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "invalid";
    // Public portfolio assets and links must be HTTPS; reject javascript:, data:, ftp: etc.
    if (uri.Scheme != Uri.UriSchemeHttps) return "invalid";
    if (!string.IsNullOrEmpty(uri.UserInfo)) return "invalid";
    return uri.ToString();
}

static SocialLinkInput[] SafeSocialLinks(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return [];
    try { return (JsonSerializer.Deserialize<SocialLinkInput[]>(json) ?? []).Where(x => x is not null).ToArray(); }
    catch (JsonException) { return []; }
}

static object PublicTeacherProfile(TeacherProfile profile, AppUser teacher) => new
{
    id = profile.Id,
    teacherId = profile.TeacherId,
    slug = profile.PublicSlug,
    fullName = teacher.FullName,
    professionalName = profile.ProfessionalName,
    specialty = profile.Specialty,
    bio = profile.Bio,
    qualifications = profile.Qualifications,
    experience = profile.Experience,
    yearsOfExperience = profile.YearsOfExperience,
    teachingStyle = profile.TeachingStyle,
    extraInfo = profile.ExtraInfo,
    avatarUrl = profile.AvatarUrl,
    coverUrl = profile.CoverUrl,
    stages = SafeJsonArray(profile.StagesJson),
    grades = SafeJsonArray(profile.GradesJson),
    socialLinks = SafeSocialLinks(profile.SocialLinksJson),
    email = profile.ShowEmail ? teacher.Email : null,
    phone = profile.ShowPhone ? (profile.ContactPhone ?? teacher.StudentPhone) : null,
    status = profile.Status,
    publishedAtUtc = profile.PublishedAtUtc,
    updatedAtUtc = profile.UpdatedAtUtc
};

async Task<TeacherStudentRequest?> ActiveTeacherStudentLink(AppDbContext db, Guid teacherId, Guid studentId) =>
    await db.TeacherStudentRequests.FirstOrDefaultAsync(x => x.TeacherId == teacherId && x.StudentId == studentId
        && x.Status == "Accepted" && x.UnlinkedAtUtc == null);
async Task<string?> ExamAccessRestriction(AppDbContext db, Guid examId, Guid studentId)
{
    var examInfo = await db.Exams.AsNoTracking()
        .Where(x => x.Id == examId)
        .Select(x => new { x.TeacherId, x.Stage, x.Grade })
        .FirstOrDefaultAsync();
    if (examInfo is null) return "الامتحان غير موجود.";
    var student = await db.Users.AsNoTracking()
        .Where(x => x.Id == studentId && x.Role == "Student")
        .Select(x => new { x.Stage, x.Grade })
        .FirstOrDefaultAsync();
    if (student is null) return "حساب الطالب غير موجود.";
    if (!EducationCatalog.Matches(examInfo.Stage, examInfo.Grade, student.Stage, student.Grade))
        return "هذا الامتحان غير متاح لمرحلتك التعليمية أو صفك الدراسي.";
    // Only the current active link may grant access. Older Unlinked/Rejected rows are
    // history and must never shadow a newer Accepted relationship.
    var relationship = await db.TeacherStudentRequests.AsNoTracking()
        .Where(x => x.TeacherId == examInfo.TeacherId && x.StudentId == studentId
            && x.Status == "Accepted" && x.UnlinkedAtUtc == null)
        .OrderByDescending(x => x.DecidedAtUtc ?? x.CreatedAtUtc)
        .Select(x => new { x.SuspendedAtUtc })
        .FirstOrDefaultAsync();
    if (relationship is null)
        return "أنت غير مسجل مع مدرس هذا الامتحان.";
    if (relationship.SuspendedAtUtc.HasValue)
        return "تم إيقاف وصولك إلى امتحانات هذا المدرس. تواصل معه لإعادة التفعيل.";
    if (await db.TeacherStudentExamBans.AnyAsync(x => x.ExamId == examId && x.StudentId == studentId))
        return "تم منعك من دخول هذا الامتحان من قبل المدرس.";
    return null;
}
async Task<bool> IsEligibleForExam(AppDbContext db, Guid examId, Guid studentId) =>
    await ExamAccessRestriction(db, examId, studentId) is null;
// Ending an exam only stops attempts and finalizes submissions. Legacy exams (no
// ResultsReleaseMode) still need an explicit teacher publication; exams created with one
// of the two release modes follow that mode.
static bool ResultsVisible(Exam exam, Submission submission, DateTime now) =>
    ResultsVisibleFor(exam.ResultsReleaseMode, submission.Status, exam.Status, exam.AvailableUntilUtc, exam.ResultsPublishedAtUtc, now);
static bool ResultsVisibleFor(string? mode, string submissionStatus, string examStatus, DateTime? availableUntilUtc, DateTime? publishedAtUtc, DateTime now)
{
    if (submissionStatus != "Reviewed") return false;
    return mode switch
    {
        "AfterSubmission" => true,
        "AfterExamEnd" => examStatus == "Finished" || (availableUntilUtc.HasValue && now >= availableUntilUtc.Value),
        _ => examStatus == "Finished" && publishedAtUtc.HasValue
    };
}
async Task AutoPublishAfterExamEnd(AppDbContext db, Exam exam, DateTime now)
{
    if (exam.ResultsReleaseMode != "AfterExamEnd" || exam.ResultsPublishedAtUtc.HasValue) return;
    if (await db.Submissions.AnyAsync(x => x.ExamId == exam.Id && (x.Status == "NeedsReview" || x.Status == "Processing" || x.Status == "InProgress"))) return;
    exam.ResultsPublishedAtUtc = now;
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
    await AutoPublishAfterExamEnd(db, exam, now);
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
async Task<bool> SendPasswordResetEmail(AppUser user, string resetToken)
{
    var smtpHost = builder.Configuration["Email:SmtpHost"];
    if (string.IsNullOrWhiteSpace(smtpHost)) return false;
    var resetUrl = $"{allowedOrigins[0]}/?resetToken={Uri.EscapeDataString(resetToken)}";
    try
    {
        using var mail = new MailMessage(builder.Configuration["Email:From"] ?? "no-reply@localhost", user.Email, "إعادة تعيين كلمة المرور - امتحاني",
            $"<div dir='rtl' style='font-family:Arial'><h2>أهلاً {WebUtility.HtmlEncode(user.FullName)}</h2><p>لإعادة تعيين كلمة المرور، اضغط على الرابط التالي:</p><p><a href='{resetUrl}'>إعادة تعيين كلمة المرور</a></p><p>الرابط صالح لمدة ساعة واحدة. إذا لم تطلب ذلك، تجاهل الرسالة.</p></div>") { IsBodyHtml = true };
        using var client = new SmtpClient(smtpHost, int.TryParse(builder.Configuration["Email:SmtpPort"], out var smtpPort) ? smtpPort : 587) { EnableSsl = bool.TryParse(builder.Configuration["Email:EnableSsl"], out var ssl) && ssl };
        var username = builder.Configuration["Email:Username"]; var password = builder.Configuration["Email:Password"];
        if (!string.IsNullOrWhiteSpace(username)) client.Credentials = new NetworkCredential(username, password);
        await client.SendMailAsync(mail);
        return true;
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Password reset email delivery failed.");
        return false;
    }
}
async Task FinalizeExpired(AppDbContext db)
{
    const int FinalizeGraceMinutes = 5;
    var now = DateTime.UtcNow;
    var candidates = await db.Submissions
        .AsNoTracking()
        .Where(x => x.Status == "InProgress" && x.Exam != null && x.Exam.Status != "Paused")
        .Select(x => new { x.Id, x.StartedAtUtc, x.ExtraTimeSeconds, x.Exam!.DurationMinutes, x.Exam.AvailableUntilUtc })
        .ToListAsync();
    var expiredIds = candidates
        .Where(x =>
        {
            var effective = ExamGrader.Deadline(new Submission { StartedAtUtc = x.StartedAtUtc, ExtraTimeSeconds = x.ExtraTimeSeconds }, x.DurationMinutes, x.AvailableUntilUtc);
            var cappedByWindow = x.AvailableUntilUtc.HasValue && effective == x.AvailableUntilUtc.Value;
            return now >= effective.AddMinutes(cappedByWindow ? 0 : FinalizeGraceMinutes);
        })
        .Select(x => x.Id)
        .ToList();
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
        var deadline = ExamGrader.Deadline(submission, submission.Exam.DurationMinutes, submission.Exam.AvailableUntilUtc);
        var cappedByWindow = submission.Exam.AvailableUntilUtc.HasValue && deadline == submission.Exam.AvailableUntilUtc.Value;
        if (now < deadline.AddMinutes(cappedByWindow ? 0 : FinalizeGraceMinutes)) continue;
        var questions = await db.Questions.AsNoTracking().Where(x => x.ExamId == submission.ExamId).ToListAsync();
        ExamGrader.Grade(submission, questions);
        submission.SubmittedAtUtc = deadline;
        var participant = await db.ExamParticipants.FirstOrDefaultAsync(x => x.ExamId == submission.ExamId && x.StudentId == submission.StudentId);
        if (participant is not null) { participant.Status = "Submitted"; participant.LastSeenAtUtc = now; }
    }
    await db.SaveChangesAsync();
    await tx.CommitAsync();
}
app.MapPost("/api/auth/register", async (RegisterRequest req, AppDbContext db) =>
{
  if (req is null || string.IsNullOrWhiteSpace(req.FullName) ||
      string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password) ||
      req.FullName.Trim().Length is < 3 or > 120 || req.Email.Trim().Length > 254 || !req.Email.Contains('@'))
    return Results.BadRequest(new { message = "أدخل الاسم والبريد الصحيح وكلمة مرور بين 10 و128 حرفاً." });
  var passwordError = PasswordError(req.Password);
  if (passwordError is not null)
    return Results.BadRequest(new { message = passwordError });

  var email = req.Email.Trim().ToLowerInvariant();
  var role = string.Equals(req.Role, "Teacher", StringComparison.OrdinalIgnoreCase) ? "Teacher" : "Student";
  var fullName = IdentityNormalizer.NormalizeDisplayName(req.FullName);
  if (fullName.Length is < 3 or > 120)
    return Results.BadRequest(new { message = "أدخل اسماً صحيحاً بين 3 و120 حرفاً.", field = "fullName" });
  if (role == "Student")
  {
    var nameError = IdentityNormalizer.StudentFullNameError(fullName);
    if (nameError is not null)
      return Results.BadRequest(new { message = nameError, field = "fullName" });
  }
  var normalizedName = IdentityNormalizer.NormalizeName(fullName);
  if (normalizedName.Length == 0)
    return Results.BadRequest(new { message = "أدخل اسماً صحيحاً." });
  var stage = req.Stage?.Trim();
  var grade = req.Grade?.Trim();
  string? studentPhone = null;
  string? parentPhone = null;
  string? parentName = IdentityNormalizer.NormalizeDisplayName(req.ParentName);
  var parentNameError = IdentityNormalizer.ParentNameError(parentName);
  if (parentNameError is not null)
    return Results.BadRequest(new { message = parentNameError, field = "parentName" });
  if (parentName.Length == 0) parentName = null;
  int? teacherCount = null;
  Guid[] teacherIds = [];
  TeacherStudentInvitation? invitation = null;

  if (await db.Users.AnyAsync(x => x.Email == email))
    return Results.Conflict(new { message = "البريد مستخدم بالفعل." });

  if (role == "Student")
  {
    if (!EducationCatalog.IsValid(stage, grade))
      return Results.BadRequest(new { message = "اختر المرحلة التعليمية والصف الدراسي المناسب لها." });
    studentPhone = IdentityNormalizer.NormalizePhone(req.StudentPhone);
    parentPhone = IdentityNormalizer.NormalizePhone(req.ParentPhone);
    if (studentPhone is null)
      return Results.BadRequest(new { message = IdentityNormalizer.PhoneConflictMessage("student"), field = "studentPhone" });
    if (parentPhone is null)
      return Results.BadRequest(new { message = IdentityNormalizer.PhoneConflictMessage("parent"), field = "parentPhone" });
    if (studentPhone == parentPhone)
      return Results.BadRequest(new { message = "رقم هاتف الطالب يجب أن يختلف عن رقم هاتف ولي الأمر.", field = "parentPhone" });
    if (await db.Users.AnyAsync(x => x.Role == "Student" && x.NormalizedName == normalizedName))
      return Results.Conflict(new { message = "يوجد حساب طالب مسجل بنفس الاسم بالفعل. إذا كان هذا حسابك، سجّل الدخول بدلاً من إنشاء حساب جديد.", field = "fullName" });
    if (await db.Users.AnyAsync(x => x.StudentPhone == studentPhone))
      return Results.Conflict(new { message = "رقم هاتف الطالب مستخدم بالفعل في حساب آخر. سجّل الدخول أو استخدم رقماً مختلفاً.", field = "studentPhone" });

    teacherCount = req.TeacherCount ?? 0;
    if (teacherCount is < 1 or > 20)
      return Results.BadRequest(new { message = "اختر عدد المدرسين (من 1 إلى 20).", field = "teacherCount" });
    teacherIds = (req.TeacherIds ?? []).Where(id => id != Guid.Empty).Distinct().ToArray();
    if (teacherIds.Length < 1 || teacherIds.Length > teacherCount.Value)
      return Results.BadRequest(new { message = "اختر المدرسين ضمن العدد الذي أدخلته (مدرس واحد على الأقل).", field = "teacherIds" });
    var validTeacherCount = await db.Users.CountAsync(t => teacherIds.Contains(t.Id) && t.Role == "Teacher" && t.EmailConfirmed);
    if (validTeacherCount != teacherIds.Length)
      return Results.BadRequest(new { message = "أحد المدرسين المختارين غير موجود أو غير مفعّل. حدّث القائمة وحاول مجدداً.", field = "teacherIds" });

    if (!string.IsNullOrWhiteSpace(req.InvitationToken))
    {
      var token = req.InvitationToken.Trim();
      invitation = await db.TeacherStudentInvitations.FirstOrDefaultAsync(x => x.Token == token);
      if (invitation is null || invitation.Status != "Pending" || invitation.ExpiresAtUtc <= DateTime.UtcNow)
        return Results.BadRequest(new { message = "رابط دعوة المدرس غير صالح أو منتهي الصلاحية. اطلب دعوة جديدة من المدرس.", field = "invitationToken" });
      if (invitation.StudentPhone != studentPhone)
        return Results.BadRequest(new { message = "رقم هاتف الطالب لا يطابق الرقم الذي سجله المدرس في الدعوة.", field = "studentPhone" });
      if (!teacherIds.Contains(invitation.TeacherId))
        return Results.BadRequest(new { message = "يجب اختيار المدرس الذي أرسل لك الدعوة ضمن قائمة المدرسين.", field = "teacherIds" });
    }
  }
  else
  {
    stage = null; grade = null;
  }

  var salt = Salt();
  var smtpHost = builder.Configuration["Email:SmtpHost"];
  var smtpUsername = builder.Configuration["Email:Username"];
  var smtpPassword = builder.Configuration["Email:Password"];
  var smtpConfigured = !string.IsNullOrWhiteSpace(smtpHost)
      && !string.IsNullOrWhiteSpace(smtpUsername)
      && !string.IsNullOrWhiteSpace(smtpPassword);
  var autoConfirmWithoutSmtp = builder.Configuration.GetValue("Auth:AutoConfirmWithoutSmtp", app.Environment.IsDevelopment());
  if (!smtpConfigured && !autoConfirmWithoutSmtp)
    return Results.Problem(statusCode: 503, title: "خدمة تأكيد البريد غير مهيأة. يرجى التواصل مع إدارة المنصة.", extensions: new Dictionary<string, object?> { ["message"] = "خدمة البريد الإلكتروني غير متاحة حاليًا." });

  var verifyToken = smtpConfigured ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) : null;
  var user = new AppUser
  {
    FullName = fullName,
    NormalizedName = normalizedName,
    Email = email,
    Role = role,
    Stage = stage,
    Grade = grade,
    StudentPhone = studentPhone,
    ParentPhone = parentPhone,
    ParentName = parentName,
    TeacherCount = teacherCount,
    RegistrationSource = invitation is not null ? "TeacherInvitation" : "Self",
    EmailConfirmed = !smtpConfigured,
    EmailVerificationToken = verifyToken,
    EmailVerificationExpiresUtc = verifyToken is null ? null : DateTime.UtcNow.AddHours(24),
    PasswordHash = salt + ":" + Hash(req.Password, salt)
  };
  if (role == "Student") user.StudentCode = await NewStudentCode(db);
  db.Users.Add(user);

  if (role == "Student")
  {
    foreach (var teacherId in teacherIds)
    {
      var accepted = invitation is not null && invitation.TeacherId == teacherId;
      db.TeacherStudentRequests.Add(new TeacherStudentRequest
      {
        TeacherId = teacherId,
        StudentId = user.Id,
        Status = accepted ? "Accepted" : "Pending",
        InitiatedBy = accepted ? "Teacher" : "Student",
        CreatedAtUtc = DateTime.UtcNow,
        DecidedAtUtc = accepted ? DateTime.UtcNow : null
      });
    }
    if (invitation is not null)
    {
      invitation.Status = "Accepted";
      invitation.AcceptedAtUtc = DateTime.UtcNow;
      invitation.AcceptedStudentId = user.Id;
    }
  }
  else
  {
    // Teachers explicitly choose the Free package after registration. The pending row
    // lets the UI persist the choice without blocking the rest of the account.
    var freePlan = await db.SubscriptionPlans.FirstOrDefaultAsync(x => x.Code == PlatformDataUpgrade.FreePlanCode);
    if (freePlan is not null)
    {
      db.TeacherSubscriptions.Add(new TeacherSubscription
      {
        TeacherId = user.Id,
        PlanId = freePlan.Id,
        Status = "Pending",
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow
      });
    }
  }

  try
  {
    await db.SaveChangesAsync();
  }
  catch (DbUpdateException ex) when (IsUniqueViolation(ex.InnerException))
  {
    db.ChangeTracker.Clear();
    return Results.Conflict(new { message = "تعذر إنشاء الحساب لأن البريد أو الاسم أو رقم الهاتف مستخدم بالفعل. سجّل الدخول أو استخدم بيانات مختلفة." });
  }

  if (verifyToken is null)
    return Results.Ok(new { message = "تم إنشاء الحساب وتفعيله تلقائيًا. يمكنك تسجيل الدخول الآن.", studentCode = user.StudentCode });

  if (!await SendVerificationEmail(user, verifyToken))
  {
    // Never leave a half-created account behind if verification delivery fails.
    var requests = await db.TeacherStudentRequests.Where(x => x.StudentId == user.Id).ToListAsync();
    db.TeacherStudentRequests.RemoveRange(requests);
    if (invitation is not null)
    {
      invitation.Status = "Pending";
      invitation.AcceptedAtUtc = null;
      invitation.AcceptedStudentId = null;
    }
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
app.MapPost("/api/auth/forgot-password", async (ForgotPasswordRequest req, AppDbContext db) =>
{
    var generic = "إذا كان البريد مسجلاً فسيصلك رابط لإعادة تعيين كلمة المرور.";
    if (req is null || string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
        return Results.BadRequest(new { message = "أدخل بريداً إلكترونياً صحيحاً." });
    var email = req.Email.Trim().ToLowerInvariant();
    var user = await db.Users.FirstOrDefaultAsync(x => x.Email == email);
    if (user is null) return Results.Ok(new { message = generic });
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    user.PasswordResetToken = token;
    user.PasswordResetExpiresUtc = DateTime.UtcNow.AddHours(1);
    await db.SaveChangesAsync();
    var sent = await SendPasswordResetEmail(user, token);
    if (!sent && app.Environment.IsDevelopment())
        return Results.Ok(new { message = generic, resetToken = token });
    return Results.Ok(new { message = generic });
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/reset-password", async (ResetPasswordRequest req, AppDbContext db) =>
{
    if (req is null || string.IsNullOrWhiteSpace(req.Token) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { message = "رابط إعادة التعيين غير صالح أو كلمة المرور غير مطابقة للشروط." });
    var resetPasswordError = PasswordError(req.Password);
    if (resetPasswordError is not null)
        return Results.BadRequest(new { message = resetPasswordError });
    var user = await db.Users.FirstOrDefaultAsync(x => x.PasswordResetToken == req.Token);
    if (user is null || user.PasswordResetExpiresUtc < DateTime.UtcNow)
        return Results.BadRequest(new { message = "رابط إعادة التعيين غير صالح أو منتهي الصلاحية. اطلب رابطاً جديداً." });
    var salt = Salt();
    user.PasswordHash = salt + ":" + Hash(req.Password, salt);
    user.PasswordResetToken = null;
    user.PasswordResetExpiresUtc = null;
    user.RefreshTokenHash = null;
    user.RefreshTokenExpiresUtc = null;
    user.RefreshTokenCreatedAtUtc = null;
    await db.SaveChangesAsync();
    await Audit(db, user.Id, "auth.password.reset", "AppUser", user.Id.ToString());
    return Results.Ok(new { message = "تم تغيير كلمة المرور. يمكنك تسجيل الدخول الآن." });
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/login", async (LoginRequest req, AppDbContext db, Emtehani.Application.Abstractions.ITokenService tokens, HttpContext http, LoginThrottle throttle) =>
{
  if (req is null || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrEmpty(req.Password))
    return Results.BadRequest(new { message = "أدخل البريد الإلكتروني وكلمة المرور." });
  var email = req.Email.Trim().ToLowerInvariant();
  if (!email.Contains('@'))
    return Results.BadRequest(new { message = "أدخل بريداً إلكترونياً صحيحاً." });
  if (throttle.IsLocked(email, out var retryAfterSeconds))
  {
    http.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    return Results.Json(new { message = "تم إيقاف المحاولات مؤقتاً بعد عدد كبير من كلمات المرور الخاطئة. حاول لاحقاً.", code = "login_locked" }, statusCode: StatusCodes.Status429TooManyRequests);
  }
  var user = await db.Users.SingleOrDefaultAsync(x => x.Email == email);
  if (user is null)
  {
    throttle.RegisterFailure(email);
    return Results.Unauthorized();
  }
  var parts = user.PasswordHash.Split(':');
  if (parts.Length != 2 || !FixedTimeEquals(Hash(req.Password, parts[0]), parts[1]))
  {
    throttle.RegisterFailure(email);
    return Results.Unauthorized();
  }
  if (!user.EmailConfirmed) return Results.Json(new { message = "لازم تأكد بريدك الإلكتروني الأول. افتح رسالة التأكيد اللي وصلتك على بريدك ثم جرّب تسجيل الدخول." }, statusCode: StatusCodes.Status403Forbidden);
  throttle.Clear(email);
  if (user.Role == "Student" && string.IsNullOrWhiteSpace(user.StudentCode))
  {
      user.StudentCode = await NewStudentCode(db);
      await db.SaveChangesAsync();
  }
  // Every successful login starts a new fixed 24-hour session. Refresh rotation never extends it.
  var sessionExpiresAtUtc = NewSessionExpiry();
  user.SessionExpiresAtUtc = sessionExpiresAtUtc;
  user.UpdatedAtUtc = DateTime.UtcNow;
  var t = tokens.Create(user, sessionExpiresAtUtc);
  var refreshToken = IssueRefreshToken(user, sessionExpiresAtUtc);
  var subscription = user.Role == "Teacher" ? await CurrentSubscription(db, user.Id) : null;
  await db.SaveChangesAsync();
  return Results.Ok(new AuthResponse(t.Token, user.FullName, user.Email, user.Role, t.ExpiresAtUtc, user.StudentCode, user.Stage, user.Grade, refreshToken,
      SessionExpiresAtUtc: sessionExpiresAtUtc, ServerUtc: DateTime.UtcNow,
      SubscriptionStatus: subscription?.Status, SubscriptionPlan: subscription?.Plan?.Code, UserId: user.Id));
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/refresh", async (RefreshTokenRequest req, AppDbContext db, Emtehani.Application.Abstractions.ITokenService tokens) =>
{
    if (req is null || string.IsNullOrWhiteSpace(req.RefreshToken))
        return Results.Json(new { message = "جلسة التجديد غير صالحة. سجّل الدخول مرة أخرى.", code = "refresh_invalid" }, statusCode: StatusCodes.Status401Unauthorized);
    var hash = HashRefreshToken(req.RefreshToken.Trim());
    var user = await db.Users.FirstOrDefaultAsync(x => x.RefreshTokenHash == hash);
    var now = DateTime.UtcNow;
    if (user is null || !user.EmailConfirmed || user.RefreshTokenExpiresUtc is null || user.RefreshTokenExpiresUtc <= now)
        return Results.Json(new { message = "انتهت صلاحية تجديد الجلسة. سجّل الدخول مرة أخرى دون فقدان إجاباتك المحفوظة.", code = "refresh_expired" }, statusCode: StatusCodes.Status401Unauthorized);
    // Legacy sessions (created before the 24-hour rule) are anchored to the original login
    // time, never to the rotation time, so refreshing cannot extend them indefinitely.
    var sessionExpiresAtUtc = user.SessionExpiresAtUtc
        ?? (user.RefreshTokenCreatedAtUtc ?? now).AddHours(24);
    if (sessionExpiresAtUtc <= now)
        return Results.Json(new { message = "انتهت جلسة الدخول (24 ساعة). سجّل الدخول مرة أخرى دون فقدان إجاباتك المحفوظة.", code = "session_expired" }, statusCode: StatusCodes.Status401Unauthorized);
    user.SessionExpiresAtUtc = sessionExpiresAtUtc;
    var t = tokens.Create(user, sessionExpiresAtUtc);
    // Rotation prevents replay of a previously observed refresh token.
    var refreshToken = IssueRefreshToken(user, sessionExpiresAtUtc);
    var subscription = user.Role == "Teacher" ? await CurrentSubscription(db, user.Id) : null;
    await db.SaveChangesAsync();
    return Results.Ok(new AuthResponse(t.Token, user.FullName, user.Email, user.Role, t.ExpiresAtUtc, user.StudentCode, user.Stage, user.Grade, refreshToken,
        SessionExpiresAtUtc: sessionExpiresAtUtc, ServerUtc: DateTime.UtcNow,
        SubscriptionStatus: subscription?.Status, SubscriptionPlan: subscription?.Plan?.Code, UserId: user.Id));
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/logout", async (RefreshTokenRequest req, AppDbContext db) =>
{
    if (req is not null && !string.IsNullOrWhiteSpace(req.RefreshToken))
    {
        var hash = HashRefreshToken(req.RefreshToken.Trim());
        var user = await db.Users.FirstOrDefaultAsync(x => x.RefreshTokenHash == hash);
        if (user is not null)
        {
            user.RefreshTokenHash = null;
            user.RefreshTokenExpiresUtc = null;
            user.RefreshTokenCreatedAtUtc = null;
            await db.SaveChangesAsync();
        }
    }
    return Results.Ok(new { loggedOut = true });
}).RequireRateLimiting("auth");
app.MapGet("/api/auth/me", async (ClaimsPrincipal p, AppDbContext db) =>
{
    var userId = p.FindFirstValue(ClaimTypes.NameIdentifier);

    if (!Guid.TryParse(userId, out var id))
        return Results.Unauthorized();

    var user = await db.Users
        .AsNoTracking()
        .Where(x => x.Id == id)
        .Select(x => new
        {
            x.Id,
            x.FullName,
            x.NormalizedName,
            x.Email,
            x.Role,
            x.StudentCode,
            x.Stage,
            x.Grade,
            x.StudentPhone,
            x.ParentPhone,
            x.TeacherCount,
            x.RegistrationSource,
            x.CreatedAtUtc,
            x.SessionExpiresAtUtc
        })
        .FirstOrDefaultAsync();

    if (user is null) return Results.NotFound();
    var subscription = user.Role == "Teacher" ? await CurrentSubscription(db, id) : null;
    var linkedTeachers = user.Role == "Student"
        ? await db.TeacherStudentRequests.AsNoTracking()
            .Where(x => x.StudentId == id && x.Status == "Accepted" && x.UnlinkedAtUtc == null)
            .Select(x => new
            {
                teacherId = x.TeacherId,
                fullName = x.Teacher!.FullName,
                email = x.Teacher.Email,
                linkedAtUtc = x.DecidedAtUtc ?? x.CreatedAtUtc,
                suspendedAtUtc = x.SuspendedAtUtc
            }).ToListAsync()
        : [];
    return Results.Ok(new
    {
        user.Id,
        user.FullName,
        user.NormalizedName,
        user.Email,
        user.Role,
        user.StudentCode,
        user.Stage,
        user.Grade,
        user.StudentPhone,
        user.ParentPhone,
        user.TeacherCount,
        user.RegistrationSource,
        user.CreatedAtUtc,
        user.SessionExpiresAtUtc,
        serverUtc = DateTime.UtcNow,
        subscriptionStatus = subscription?.Status,
        subscriptionPlan = subscription?.Plan?.Code,
        subscriptionPlanName = subscription?.Plan?.Name,
        subscriptionFeatures = subscription?.Plan?.FeaturesJson,
        linkedTeachers
    });
})
.RequireAuthorization();
app.MapPut("/api/auth/profile/education", async (EducationProfileRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var studentId)) return Results.Unauthorized();
    var stage = req?.Stage?.Trim();
    var grade = req?.Grade?.Trim();
    if (!EducationCatalog.IsValid(stage, grade))
        return Results.BadRequest(new { message = "اختر المرحلة التعليمية والصف الدراسي المناسب لها." });
    var student = await db.Users.FirstOrDefaultAsync(x => x.Id == studentId && x.Role == "Student");
    if (student is null) return Results.Unauthorized();
    var changed = student.Stage != stage || student.Grade != grade;
    if (changed)
    {
        db.StudentAcademicChanges.Add(new StudentAcademicChange
        {
            StudentId = studentId,
            PreviousStage = student.Stage,
            PreviousGrade = student.Grade,
            NewStage = stage,
            NewGrade = grade,
            ChangedByUserId = studentId,
            ChangedByRole = "Student",
            Source = "Student"
        });
    }
    student.Stage = stage;
    student.Grade = grade;
    student.UpdatedAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync();
    await Audit(db, studentId, "student.education.updated", "AppUser", studentId.ToString());
    return Results.Ok(new
    {
        stage = student.Stage,
        grade = student.Grade,
        changed,
        message = changed
            ? "تم تحديث المرحلة والصف. ستبقى الامتحانات والنتائج السابقة محفوظة في سجلك الأكاديمي السابق."
            : "بيانات المرحلة والصف محدّثة بالفعل."
    });
}).RequireAuthorization();

// --- Student profile, academic history, and teacher-link decisions ---
app.MapGet("/api/student/profile", async (ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var studentId)) return Results.Unauthorized();
    var student = await db.Users.AsNoTracking()
        .Where(x => x.Id == studentId && x.Role == "Student")
        .Select(x => new
        {
            x.Id, x.FullName, x.Email, x.StudentCode, x.Stage, x.Grade,
            x.StudentPhone, x.ParentPhone, x.ParentName, x.TeacherCount, x.RegistrationSource,
            x.CreatedAtUtc, x.UpdatedAtUtc
        })
        .FirstOrDefaultAsync();
    if (student is null) return Results.Unauthorized();

    var changes = await db.StudentAcademicChanges.AsNoTracking()
        .Where(x => x.StudentId == studentId)
        .OrderByDescending(x => x.ChangedAtUtc)
        .Select(x => new
        {
            x.Id, x.PreviousStage, x.PreviousGrade, x.NewStage, x.NewGrade,
            x.ChangedByRole, x.Source, x.ChangedAtUtc
        })
        .ToListAsync();

    var now = DateTime.UtcNow;
    var attempts = await db.Submissions.AsNoTracking()
        .Where(s => s.StudentId == studentId && s.Status != "InProgress" && s.Status != "Abandoned")
        .OrderByDescending(s => s.SubmittedAtUtc ?? s.StartedAtUtc)
        .Select(s => new
        {
            s.Id,
            s.ExamId,
            ExamTitle = s.Exam!.Title,
            TeacherName = db.Users.Where(u => u.Id == s.Exam.TeacherId).Select(u => u.FullName).FirstOrDefault(),
            ExamStage = s.Exam.Stage,
            ExamGrade = s.Exam.Grade,
            ExamAcademicYear = s.Exam.AcademicYear,
            s.StageAtAttempt,
            s.GradeAtAttempt,
            s.AcademicYearAtAttempt,
            s.Status,
            s.Score,
            s.MaxScore,
            s.SubmittedAtUtc,
            s.StartedAtUtc,
            s.AttemptNumber,
            s.Exam.ResultsPublishedAtUtc,
            s.Exam.ResultsReleaseMode,
            ExamStatus = s.Exam.Status,
            s.Exam.AvailableUntilUtc
        })
        .ToListAsync();

    var history = attempts
        .GroupBy(a => new
        {
            Stage = a.StageAtAttempt ?? a.ExamStage ?? "",
            Grade = a.GradeAtAttempt ?? a.ExamGrade ?? "",
            AcademicYear = a.AcademicYearAtAttempt ?? a.ExamAcademicYear ?? ""
        })
        .Select(g => new
        {
            stage = g.Key.Stage,
            grade = g.Key.Grade,
            academicYear = g.Key.AcademicYear,
            attemptsCount = g.Count(),
            items = g.Select(a =>
            {
                var resultsAvailable = ResultsVisibleFor(a.ResultsReleaseMode, a.Status, a.ExamStatus, a.AvailableUntilUtc, a.ResultsPublishedAtUtc, now);
                return new
                {
                    a.Id,
                    a.ExamId,
                    a.ExamTitle,
                    a.TeacherName,
                    a.Status,
                    score = resultsAvailable ? a.Score : (decimal?)null,
                    maxScore = resultsAvailable ? a.MaxScore : (decimal?)null,
                    resultsAvailable,
                    a.SubmittedAtUtc,
                    a.StartedAtUtc,
                    a.AttemptNumber
                };
            }).ToList()
        })
        .OrderByDescending(g => g.academicYear)
        .ThenBy(g => g.stage)
        .ThenBy(g => g.grade)
        .ToList();

    var linkedTeachers = await db.TeacherStudentRequests.AsNoTracking()
        .Where(x => x.StudentId == studentId && x.Status == "Accepted" && x.UnlinkedAtUtc == null)
        .Select(x => new
        {
            teacherId = x.TeacherId,
            fullName = x.Teacher!.FullName,
            email = x.Teacher.Email,
            linkedAtUtc = x.DecidedAtUtc ?? x.CreatedAtUtc,
            suspendedAtUtc = x.SuspendedAtUtc,
            initiatedBy = x.InitiatedBy
        })
        .ToListAsync();

    return Results.Ok(new
    {
        profile = student,
        changes,
        history,
        linkedTeachers,
        currentAcademicYear = AcademicYear.Current(),
        serverUtc = now
    });
}).RequireAuthorization();

app.MapPut("/api/student/profile", async (StudentProfileUpdateRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var studentId)) return Results.Unauthorized();
    var student = await db.Users.FirstOrDefaultAsync(x => x.Id == studentId && x.Role == "Student");
    if (student is null) return Results.Unauthorized();
    var result = await ApplyStudentProfile(db, student, req, studentId, "Student", "Student");
    if (result.Error is not null) return Results.Json(new { message = result.Error }, statusCode: result.Status);
    await db.SaveChangesAsync();
    await Audit(db, studentId, "student.profile.updated", "AppUser", studentId.ToString());
    return Results.Ok(new
    {
        student.FullName,
        student.StudentPhone,
        student.ParentPhone,
        student.ParentName,
        student.Stage,
        student.Grade,
        changed = result.Changed,
        message = result.Changed
            ? "تم تحديث بياناتك. ستبقى الامتحانات والنتائج السابقة محفوظة في سجلك الأكاديمي السابق."
            : "تم تحديث بياناتك بنجاح."
    });
}).RequireAuthorization();

app.MapGet("/api/student/link-requests", async (ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var studentId)) return Results.Unauthorized();
    var rows = await db.TeacherStudentRequests.AsNoTracking()
        .Where(x => x.StudentId == studentId)
        .OrderByDescending(x => x.CreatedAtUtc)
        .Select(x => new
        {
            x.Id,
            x.TeacherId,
            TeacherName = x.Teacher!.FullName,
            TeacherEmail = x.Teacher.Email,
            x.Status,
            x.InitiatedBy,
            x.CreatedAtUtc,
            x.DecidedAtUtc,
            x.UnlinkedAtUtc,
            x.SuspendedAtUtc
        })
        .ToListAsync();
    return Results.Ok(rows);
}).RequireAuthorization();

app.MapPut("/api/student/link-requests/{id:guid}", async (Guid id, StudentLinkDecisionRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var studentId)) return Results.Unauthorized();
    var action = (req?.Action ?? "").Trim().ToLowerInvariant();
    if (action is not ("accept" or "reject")) return Results.BadRequest(new { message = "إجراء غير معروف." });
    await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
    var request = await db.TeacherStudentRequests
        .FirstOrDefaultAsync(x => x.Id == id && x.StudentId == studentId && x.InitiatedBy == "Teacher");
    if (request is null) return Results.NotFound();
    if (request.Status != "Pending") return Results.Conflict(new { message = "تم البت في هذا الطلب مسبقاً." });
    if (action == "accept")
    {
        var student = await db.Users.AsNoTracking().Where(x => x.Id == studentId)
            .Select(x => new { x.TeacherCount }).FirstOrDefaultAsync();
        var accepted = await AcceptedTeacherCount(db, studentId);
        var limit = student?.TeacherCount ?? int.MaxValue;
        if (accepted >= limit)
            return Results.Conflict(new { message = $"لا يمكنك قبول مدرس إضافي. العدد الذي اخترته عند التسجيل هو {limit} مدرس." });
    }
    request.Status = action == "accept" ? "Accepted" : "Rejected";
    request.DecidedAtUtc = DateTime.UtcNow;
    if (action == "accept")
    {
        request.UnlinkedAtUtc = null;
        request.SuspendedAtUtc = null;
    }
    await db.SaveChangesAsync();
    await tx.CommitAsync();
    await Audit(db, studentId, $"student.link-request.{request.Status.ToLowerInvariant()}", "TeacherStudentRequest", id.ToString());
    return Results.Ok(new
    {
        request.Id,
        request.Status,
        message = action == "accept" ? "تم قبول طلب المدرس." : "تم رفض طلب المدرس."
    });
}).RequireAuthorization();


app.MapPost("/api/exams", async (ExamCreateRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
  if (req is null || req.Questions is null || req.Questions.Count == 0 || req.DurationMinutes < 1 || req.DurationMinutes > 300 || req.MaxAttempts < 1 || req.MaxAttempts > 20 || string.IsNullOrWhiteSpace(req.Title))
    return Results.BadRequest(new { message = "بيانات الامتحان غير مكتملة أو المدة خارج النطاق." });
  var resultsReleaseMode = NormalizeResultsReleaseMode(req.ResultsReleaseMode);
  if (resultsReleaseMode == "invalid")
    return Results.BadRequest(new { message = "موعد إظهار النتائج غير صالح." });
  var availableFrom = ToUtc(req.AvailableFromUtc);
  var availableUntil = ToUtc(req.AvailableUntilUtc);
  if (availableFrom.HasValue && availableUntil.HasValue && availableUntil <= availableFrom)
    return Results.BadRequest(new { message = "نهاية فترة الإتاحة يجب أن تكون بعد بدايتها." });
  var validTypes = new[] { "MultipleChoice", "TrueFalse", "ShortAnswer", "Essay", "FillBlank", "Matching" };
  if (req.Questions.Any(q => q is null || string.IsNullOrWhiteSpace(q.Text) || q.Points <= 0 || string.IsNullOrWhiteSpace(q.Type) || !validTypes.Contains(q.Type) ||
    (q.Type == "MultipleChoice" && ((q.Options?.Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0) < 2 || string.IsNullOrWhiteSpace(q.CorrectAnswer) || !(q.Options ?? []).Contains(q.CorrectAnswer, StringComparer.OrdinalIgnoreCase))) ||
    (q.Type == "TrueFalse" && !new[] { "صح", "خطأ" }.Contains(q.CorrectAnswer ?? "")) ||
    ((q.Type is "ShortAnswer" or "FillBlank") && string.IsNullOrWhiteSpace(q.CorrectAnswer)) ||
    (q.Type == "Matching" && ((q.Options?.Length ?? 0) < 2 || (q.Options ?? []).Any(option => !option.Contains("||", StringComparison.Ordinal))))))
    return Results.BadRequest(new { message = "راجع نصوص الأسئلة والدرجات والاختيارات والإجابات الصحيحة." });
  if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
  if (!Guid.TryParse(UserId(p), out var uid)) return Results.Unauthorized();
  var stage = req.Stage?.Trim();
  var grade = req.Grade?.Trim();
  if (!EducationCatalog.IsValid(stage, grade))
    return Results.BadRequest(new { message = "اختر المرحلة التعليمية والصف الدراسي المناسبين للامتحان." });
  var code = RandomNumberGenerator.GetInt32(100000, 1_000_000).ToString(); while (await db.Exams.AnyAsync(x => x.JoinCode == code)) code = RandomNumberGenerator.GetInt32(100000, 1_000_000).ToString();
  var e = new Exam { TeacherId = uid, Title = req.Title.Trim(), Description = req.Description, DurationMinutes = req.DurationMinutes, MaxAttempts = Math.Clamp(req.MaxAttempts, 1, 20), AllowResumeAfterFocusLoss = req.AllowResumeAfterFocusLoss, JoinCode = code, ScheduledAtUtc = ToUtc(req.ScheduledAtUtc), AvailableFromUtc = availableFrom, AvailableUntilUtc = availableUntil, Stage = stage, Grade = grade, AcademicYear = AcademicYear.Current(), ResultsReleaseMode = resultsReleaseMode, Status = "Draft", Questions = req.Questions.Select((q, i) => new Question { Text = q.Text, Type = q.Type, OptionsJson = JsonSerializer.Serialize(q.Options ?? []), CorrectAnswer = q.Type == "Matching" ? string.Join(",", Enumerable.Range(0, q.Options?.Length ?? 0)) : q.CorrectAnswer, Points = q.Points, SortOrder = i }).ToList() };
  db.Exams.Add(e); await db.SaveChangesAsync(); await Audit(db, uid, "exam.created", "Exam", e.Id.ToString()); return Results.Created($"/api/exams/{e.Id}", new { e.Id, e.Title, e.Description, e.DurationMinutes, e.JoinCode, e.IsPublished, e.Status, e.ScheduledAtUtc, e.AvailableFromUtc, e.AvailableUntilUtc, e.Stage, e.Grade, e.AcademicYear, e.ResultsReleaseMode, questionCount = e.Questions.Count });
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
    if (!Guid.TryParse(userId, out var studentId2)) return Results.Unauthorized();
    var studentProfile = await db.Users.AsNoTracking()
        .Where(x => x.Id == studentId2 && x.Role == "Student")
        .Select(x => new { x.Stage, x.Grade })
        .FirstOrDefaultAsync();
    if (studentProfile is null) return Results.Unauthorized();
    var studentStage = studentProfile.Stage;
    var studentGrade = studentProfile.Grade;
    var currentAcademicYear = AcademicYear.Current();
    q = q.Where(x => x.IsPublished &&
        x.AcademicYear == currentAcademicYear &&
        db.TeacherStudentRequests.Any(r => r.TeacherId == x.TeacherId && r.StudentId == studentId2
            && r.Status == "Accepted" && r.UnlinkedAtUtc == null && r.SuspendedAtUtc == null)
        && !db.TeacherStudentExamBans.Any(b => b.ExamId == x.Id && b.StudentId == studentId2)
        && (x.Stage == null || x.Stage == studentStage)
        && (x.Grade == null || x.Grade == studentGrade));
}
  if (!string.IsNullOrWhiteSpace(search)) q = q.Where(x => x.Title.Contains(search));
  if (!string.IsNullOrWhiteSpace(status) && status != "All") q = q.Where(x => x.Status == status);
  var total = await q.CountAsync(); var canSeeJoinCode = p.FindFirstValue(ClaimTypes.Role) == "Teacher";
  var items = await q.OrderByDescending(x => x.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).Select(x => new { x.Id, x.Title, x.Description, x.DurationMinutes, x.MaxAttempts, x.AllowResumeAfterFocusLoss, TeacherName = db.Users.Where(u => u.Id == x.TeacherId).Select(u => u.FullName).FirstOrDefault(), JoinCode = canSeeJoinCode ? x.JoinCode : null, x.IsPublished, x.Status, x.ScheduledAtUtc, x.AvailableFromUtc, x.AvailableUntilUtc, x.Stage, x.Grade, x.ResultsReleaseMode, x.ResultsPublishedAtUtc, x.CreatedAtUtc, QuestionCount = x.Questions.Count, ParticipantCount = x.Participants.Count }).ToListAsync();
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
        var restriction = await ExamAccessRestriction(db, id, userId);
        if (restriction is not null)
            return Results.Json(new { message = restriction }, statusCode: StatusCodes.Status403Forbidden);
    }
    return Results.Ok(new
    {
        exam.Id, exam.Title, exam.Description, exam.DurationMinutes, exam.MaxAttempts,
        exam.AllowResumeAfterFocusLoss, exam.IsPublished, exam.Status, exam.ScheduledAtUtc, exam.AvailableFromUtc, exam.AvailableUntilUtc, exam.Stage, exam.Grade, exam.ResultsPublishedAtUtc, exam.ResultsReleaseMode, exam.CreatedAtUtc,
        ResultsAvailable = exam.ResultsReleaseMode == "AfterExamEnd"
            ? exam.Status == "Finished" || (exam.AvailableUntilUtc.HasValue && DateTime.UtcNow >= exam.AvailableUntilUtc.Value)
            : exam.Status == "Finished" && exam.ResultsPublishedAtUtc.HasValue,
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

app.MapPut("/api/exams/{id:guid}/availability", async (Guid id, ExamAvailabilityRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var exam = await db.Exams.FirstOrDefaultAsync(x => x.Id == id && x.TeacherId == teacherId);
    if (exam is null) return Results.NotFound();
    var resultsReleaseMode = NormalizeResultsReleaseMode(req?.ResultsReleaseMode);
    if (resultsReleaseMode == "invalid")
        return Results.BadRequest(new { message = "موعد إظهار النتائج غير صالح." });
    var from = ToUtc(req?.AvailableFromUtc);
    var until = ToUtc(req?.AvailableUntilUtc);
    if (from.HasValue && until.HasValue && until <= from)
        return Results.BadRequest(new { message = "نهاية فترة الإتاحة يجب أن تكون بعد بدايتها." });
    exam.AvailableFromUtc = from;
    exam.AvailableUntilUtc = until;
    if (resultsReleaseMode is not null) exam.ResultsReleaseMode = resultsReleaseMode;
    await db.SaveChangesAsync();
    await Audit(db, teacherId, "exam.availability.updated", "Exam", id.ToString());
    return Results.Ok(new { exam.Id, exam.AvailableFromUtc, exam.AvailableUntilUtc, exam.ResultsReleaseMode });
}).RequireAuthorization();

// --- Student/teacher enrollment ---
app.MapGet("/api/teachers", async (ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var studentId)) return Results.Unauthorized();
    var teachers = await db.Users.AsNoTracking().Where(t => t.Role == "Teacher" && t.EmailConfirmed)
        .Select(t => new { t.Id, t.FullName, ExamCount = db.Exams.Count(e => e.TeacherId == t.Id && e.IsPublished) })
        .ToListAsync();
    var publicProfiles = await db.TeacherProfiles.AsNoTracking()
        .Where(x => x.Status == "Published")
        .Select(x => new { x.TeacherId, x.PublicSlug, x.ProfessionalName, x.Specialty, x.AvatarUrl })
        .ToDictionaryAsync(x => x.TeacherId);
    var requests = await db.TeacherStudentRequests.AsNoTracking()
        .Where(r => r.StudentId == studentId)
        .Select(r => new { r.TeacherId, r.Status, r.CreatedAtUtc, r.UnlinkedAtUtc })
        .ToListAsync();
    var map = requests.GroupBy(r => r.TeacherId).ToDictionary(g => g.Key, g =>
    {
        var row = g.OrderByDescending(_ => _.Status == "Accepted").ThenByDescending(_ => _.CreatedAtUtc).First();
        return row.Status == "Accepted" && row.UnlinkedAtUtc.HasValue ? "Unlinked" : row.Status;
    });
    return Results.Ok(teachers.Select(t =>
    {
        publicProfiles.TryGetValue(t.Id, out var profile);
        return new
        {
            t.Id,
            t.FullName,
            t.ExamCount,
            RequestStatus = map.TryGetValue(t.Id, out var s) ? s : null,
            hasPublicProfile = profile is not null,
            profileSlug = profile?.PublicSlug,
            professionalName = profile?.ProfessionalName,
            specialty = profile?.Specialty,
            avatarUrl = profile?.AvatarUrl
        };
    }));
}).RequireAuthorization();

app.MapPost("/api/teachers/{teacherId:guid}/request", async (Guid teacherId, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var studentId)) return Results.Unauthorized();
    var teacher = await db.Users.FirstOrDefaultAsync(t => t.Id == teacherId && t.Role == "Teacher" && t.EmailConfirmed);
    if (teacher is null) return Results.NotFound(new { message = "المدرس غير موجود." });
    var studentInfo = await db.Users.AsNoTracking().Where(x => x.Id == studentId)
        .Select(x => new { x.TeacherCount }).FirstOrDefaultAsync();
    var acceptedCount = await AcceptedTeacherCount(db, studentId);
    var teacherLimit = studentInfo?.TeacherCount ?? int.MaxValue;
    if (acceptedCount >= teacherLimit)
        return Results.Conflict(new { message = $"لا يمكنك إضافة مدرس آخر. العدد الذي اخترته عند التسجيل هو {teacherLimit} مدرس." });
    // Rejected and Unlinked rows are immutable history. A new request gets a new Id;
    // only a Pending or an active Accepted link blocks sending another one.
    var existingRows = await db.TeacherStudentRequests
        .Where(r => r.TeacherId == teacherId && r.StudentId == studentId)
        .ToListAsync();
    if (existingRows.Any(r => r.Status == "Accepted"))
        return Results.Conflict(new { message = "لديك ارتباط نشط بالفعل مع هذا المدرس.", status = "Accepted" });
    if (existingRows.Any(r => r.Status == "Pending"))
        return Results.Conflict(new { message = "لديك طلب ارتباط معلّق بالفعل مع هذا المدرس.", status = "Pending" });

    var newRequest = new TeacherStudentRequest
    {
        TeacherId = teacherId,
        StudentId = studentId,
        Status = "Pending",
        InitiatedBy = "Student",
        CreatedAtUtc = DateTime.UtcNow
    };
    db.TeacherStudentRequests.Add(newRequest);
    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException ex) when (IsUniqueViolation(ex.InnerException))
    {
        return Results.Conflict(new { message = "لديك طلب ارتباط معلّق بالفعل مع هذا المدرس.", status = "Pending" });
    }
    await Audit(db, studentId, "teacher.request.created", "TeacherStudentRequest", newRequest.Id.ToString());
    return Results.Ok(new
    {
        requestId = newRequest.Id,
        message = existingRows.Any(r => r.Status is "Rejected" or "Unlinked")
            ? "تم إرسال طلب ارتباط جديد. الطلب السابق محفوظ في السجل، انتظر موافقة المدرس."
            : "تم إرسال طلب التسجيل. انتظر موافقة المدرس.",
        status = "Pending"
    });
}).RequireAuthorization().RequireRateLimiting("api");

app.MapGet("/api/teacher/requests", async (ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var rows = await db.TeacherStudentRequests.AsNoTracking()
        .Where(r => r.TeacherId == teacherId)
        .OrderByDescending(r => r.CreatedAtUtc)
        .Select(r => new
        {
            r.Id,
            r.Status,
            r.InitiatedBy,
            r.CreatedAtUtc,
            r.DecidedAtUtc,
            StudentName = r.Student!.FullName,
            // E-mail is only disclosed for student-initiated requests (needed to decide)
            // or after the student has consented. A teacher-initiated pending request must
            // not expose account data before the student accepts.
            StudentEmail = r.InitiatedBy == "Student" || r.Status == "Accepted" ? r.Student.Email : null
        })
        .ToListAsync();
    return Results.Ok(rows);
}).RequireAuthorization();

app.MapPut("/api/teacher/requests/{id:guid}", async (Guid id, TeacherJoinDecisionRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var action = (req?.Action ?? "").Trim().ToLowerInvariant();
    if (action is not ("accept" or "reject")) return Results.BadRequest(new { message = "إجراء غير معروف." });
    await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
    var request = await db.TeacherStudentRequests.FirstOrDefaultAsync(r => r.Id == id && r.TeacherId == teacherId);
    if (request is null) return Results.NotFound();
    if (request.Status != "Pending") return Results.Conflict(new { message = "تم البت في هذا الطلب مسبقاً." });
    // Student-initiated requests are decided by the teacher. Teacher-initiated requests
    // must always be decided by the student, so consent cannot be bypassed server-side.
    if (request.InitiatedBy != "Student")
        return Results.Conflict(new { message = "هذا الطلب أرسله المدرس وينتظر موافقة الطالب، ولا يمكن للمدرس البت فيه." });
    if (action == "accept")
    {
        var studentInfo = await db.Users.AsNoTracking().Where(x => x.Id == request.StudentId)
            .Select(x => new { x.TeacherCount }).FirstOrDefaultAsync();
        var accepted = await AcceptedTeacherCount(db, request.StudentId);
        var limit = studentInfo?.TeacherCount ?? int.MaxValue;
        if (accepted >= limit)
            return Results.Conflict(new { message = $"لا يمكن قبول الطالب. اختار الطالب عدد {limit} مدرس كحد أقصى واستنفد العدد بالفعل." });
    }
    request.Status = action == "accept" ? "Accepted" : "Rejected";
    request.DecidedAtUtc = DateTime.UtcNow;
    if (action == "accept")
    {
        request.UnlinkedAtUtc = null;
        request.SuspendedAtUtc = null;
    }
    await db.SaveChangesAsync();
    await tx.CommitAsync();
    await Audit(db, teacherId, $"teacher.request.{request.Status.ToLowerInvariant()}", "TeacherStudentRequest", id.ToString());
    return Results.Ok(new { request.Id, request.Status });
}).RequireAuthorization();

app.MapGet("/api/teacher/students", async (ClaimsPrincipal p, AppDbContext db, string? search, string? stage, string? grade) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();

    var rows = await db.TeacherStudentRequests.AsNoTracking()
        .Where(r => r.TeacherId == teacherId && r.Status == "Accepted" && r.UnlinkedAtUtc == null)
        .Select(r => new
        {
            RelationshipId = r.Id,
            r.StudentId,
            FullName = r.Student!.FullName,
            Email = r.Student.Email,
            Stage = r.Student.Stage,
            Grade = r.Student.Grade,
            r.CreatedAtUtc,
            r.DecidedAtUtc,
            r.SuspendedAtUtc
        })
        .ToListAsync();

    var bans = await db.TeacherStudentExamBans.AsNoTracking()
        .Where(b => b.TeacherId == teacherId)
        .Select(b => new
        {
            b.StudentId,
            b.ExamId,
            b.CreatedAtUtc,
            ExamTitle = db.Exams.Where(e => e.Id == b.ExamId).Select(e => e.Title).FirstOrDefault()
        })
        .ToListAsync();
    var bannedByStudent = bans
        .GroupBy(b => b.StudentId)
        .ToDictionary(
            group => group.Key,
            group => group.OrderByDescending(b => b.CreatedAtUtc)
                .Select(b => (b.ExamId, ExamTitle: b.ExamTitle ?? "", b.CreatedAtUtc))
                .ToList());

    var term = (search ?? "").Trim();
    var requestedStage = string.IsNullOrWhiteSpace(stage) ? null : stage.Trim();
    var requestedGrade = string.IsNullOrWhiteSpace(grade) ? null : grade.Trim();
    var filtered = rows
        .Where(r => requestedStage is null || string.Equals(r.Stage, requestedStage, StringComparison.Ordinal))
        .Where(r => requestedGrade is null || string.Equals(r.Grade, requestedGrade, StringComparison.Ordinal))
        .Where(r => term.Length == 0
            || r.FullName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || r.Email.Contains(term, StringComparison.OrdinalIgnoreCase))
        .OrderBy(r => r.FullName, StringComparer.Ordinal)
        .Select(r =>
        {
            var studentBans = bannedByStudent.GetValueOrDefault(r.StudentId);
            studentBans ??= [];
            var isSuspended = r.SuspendedAtUtc.HasValue;
            return new
            {
                relationshipId = r.RelationshipId,
                studentId = r.StudentId,
                fullName = r.FullName,
                email = r.Email,
                stage = r.Stage,
                grade = r.Grade,
                linkedAtUtc = r.DecidedAtUtc ?? r.CreatedAtUtc,
                suspendedAtUtc = r.SuspendedAtUtc,
                status = isSuspended ? "Suspended" : studentBans.Count > 0 ? "Banned" : "Active",
                bannedExamCount = studentBans.Count,
                bannedExams = studentBans.Select(b => new
                {
                    examId = b.ExamId,
                    examTitle = b.ExamTitle,
                    bannedAtUtc = b.CreatedAtUtc
                }).ToList()
            };
        })
        .ToList();

    var suspendedCount = rows.Count(r => r.SuspendedAtUtc.HasValue);
    var bannedCount = rows.Count(r => !r.SuspendedAtUtc.HasValue && bannedByStudent.ContainsKey(r.StudentId));
    return Results.Ok(new
    {
        items = filtered,
        total = filtered.Count,
        summary = new
        {
            total = rows.Count,
            active = rows.Count - suspendedCount - bannedCount,
            banned = bannedCount,
            suspended = suspendedCount
        },
        serverUtc = DateTime.UtcNow
    });
}).RequireAuthorization();

app.MapGet("/api/teacher/students/{studentId:guid}", async (Guid studentId, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();

    var relationship = await db.TeacherStudentRequests.AsNoTracking()
        .Where(r => r.TeacherId == teacherId && r.StudentId == studentId
            && r.Status == "Accepted" && r.UnlinkedAtUtc == null)
        .Select(r => new
        {
            r.Id,
            r.StudentId,
            FullName = r.Student!.FullName,
            Email = r.Student.Email,
            StudentPhone = r.Student.StudentPhone,
            ParentPhone = r.Student.ParentPhone,
            ParentName = r.Student.ParentName,
            StudentStage = r.Student.Stage,
            StudentGrade = r.Student.Grade,
            r.CreatedAtUtc,
            r.DecidedAtUtc,
            r.SuspendedAtUtc
        })
        .FirstOrDefaultAsync();
    if (relationship is null)
        return Results.NotFound(new { message = "هذا الطالب غير مرتبط بك." });

    // Attempt history must include exams that have already finished. The previous
    // implementation filtered finished exams first, so a completed 10/10 attempt
    // disappeared from the summary and the teacher saw Used=0 / Remaining=1.
    var allExams = await db.Exams.AsNoTracking()
        .Where(x => x.TeacherId == teacherId)
        .OrderByDescending(x => x.CreatedAtUtc)
        .Select(x => new
        {
            x.Id,
            x.Title,
            x.Description,
            x.Status,
            x.IsPublished,
            x.MaxAttempts,
            x.Stage,
            x.Grade,
            x.CreatedAtUtc,
            x.AvailableFromUtc,
            x.AvailableUntilUtc
        })
        .ToListAsync();
    var exams = allExams.Where(x => x.Status != "Finished").ToList();
    var allExamIds = allExams.Select(x => x.Id).ToList();
    var bans = await db.TeacherStudentExamBans.AsNoTracking()
        .Where(b => b.TeacherId == teacherId && b.StudentId == studentId)
        .Select(b => new { b.ExamId, b.CreatedAtUtc })
        .ToListAsync();
    var attempts = await db.Submissions.AsNoTracking()
        .Where(s => s.StudentId == studentId && allExamIds.Contains(s.ExamId))
        .ToListAsync();

    var banByExam = bans.ToDictionary(b => b.ExamId);
    var examItems = exams.Select(x =>
    {
        var ownAttempts = attempts.Where(a => a.ExamId == x.Id).ToList();
        var latest = ownAttempts.OrderByDescending(a => a.AttemptNumber).FirstOrDefault();
        var isBanned = banByExam.TryGetValue(x.Id, out var ban);
        var educationMatches = EducationCatalog.Matches(x.Stage, x.Grade, relationship.StudentStage, relationship.StudentGrade);
        var isAvailable = !relationship.SuspendedAtUtc.HasValue
            && !isBanned
            && educationMatches
            && x.IsPublished
            && x.Status != "Draft";
        return new
        {
            examId = x.Id,
            examTitle = x.Title,
            examDescription = x.Description,
            examStatus = x.Status,
            isPublished = x.IsPublished,
            stage = x.Stage,
            grade = x.Grade,
            createdAtUtc = x.CreatedAtUtc,
            availableFromUtc = x.AvailableFromUtc,
            availableUntilUtc = x.AvailableUntilUtc,
            isBanned,
            bannedAtUtc = isBanned ? ban!.CreatedAtUtc : (DateTime?)null,
            educationMatches,
            isAvailable,
            maxAttempts = x.MaxAttempts,
            attemptsCount = ownAttempts.Count,
            attempts = AttemptSummaryFor(x.MaxAttempts, ownAttempts),
            latestAttemptStatus = latest?.Status,
            latestAttemptAtUtc = latest is null ? (DateTime?)null : latest.SubmittedAtUtc ?? latest.StartedAtUtc,
            latestScore = latest?.Score,
            latestMaxScore = latest?.MaxScore
        };
    }).ToList();

    var aggregate = AttemptSummaryFor(
        allExams.Sum(x => Math.Max(1, x.MaxAttempts)),
        attempts);
    var bannedExamCount = bans.Count;
    var status = relationship.SuspendedAtUtc.HasValue ? "Suspended" : bannedExamCount > 0 ? "Banned" : "Active";
    return Results.Ok(new
    {
        relationshipId = relationship.Id,
        studentId = relationship.StudentId,
        fullName = relationship.FullName,
        email = relationship.Email,
        studentPhone = relationship.StudentPhone,
        parentPhone = relationship.ParentPhone,
        parentName = relationship.ParentName,
        stage = relationship.StudentStage,
        grade = relationship.StudentGrade,
        linkedAtUtc = relationship.DecidedAtUtc ?? relationship.CreatedAtUtc,
        suspendedAtUtc = relationship.SuspendedAtUtc,
        status,
        bannedExamCount,
        attemptsCount = attempts.Count,
        attempts = aggregate,
        submittedAttempts = attempts.Count(a => a.Status != "InProgress"),
        availableExamCount = examItems.Count(x => x.isAvailable),
        finishedExamCount = allExams.Count(x => x.Status == "Finished"),
        exams = examItems,
        serverUtc = DateTime.UtcNow
    });
}).RequireAuthorization();

app.MapPost("/api/teacher/students/{studentId:guid}/exam-bans", async (Guid studentId, TeacherStudentExamBanRequest? req, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    if (req is null || req.ExamId == Guid.Empty)
        return Results.BadRequest(new { message = "اختر الامتحان الذي تريد منع الطالب منه." });

    var relationship = await ActiveTeacherStudentLink(db, teacherId, studentId);
    if (relationship is null)
        return Results.NotFound(new { message = "هذا الطالب غير مرتبط بك." });
    var exam = await db.Exams.FirstOrDefaultAsync(x => x.Id == req.ExamId && x.TeacherId == teacherId);
    if (exam is null)
        return Results.NotFound(new { message = "هذا الامتحان ليس ضمن امتحاناتك." });

    var existing = await db.TeacherStudentExamBans
        .FirstOrDefaultAsync(x => x.ExamId == exam.Id && x.StudentId == studentId);
    if (existing is not null)
        return Results.Ok(new
        {
            examId = exam.Id,
            examTitle = exam.Title,
            isBanned = true,
            alreadyBanned = true,
            message = "الطالب ممنوع من هذا الامتحان بالفعل."
        });

    var ban = new TeacherStudentExamBan { TeacherId = teacherId, StudentId = studentId, ExamId = exam.Id };
    db.TeacherStudentExamBans.Add(ban);
    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException ex) when (IsUniqueViolation(ex.InnerException))
    {
        db.Entry(ban).State = EntityState.Detached;
        return Results.Ok(new
        {
            examId = exam.Id,
            examTitle = exam.Title,
            isBanned = true,
            alreadyBanned = true,
            message = "الطالب ممنوع من هذا الامتحان بالفعل."
        });
    }
    await Audit(db, teacherId, "teacher.student.exam-banned", "TeacherStudentExamBan", ban.Id.ToString(),
        $"student={studentId}; exam={exam.Id}");
    return Results.Ok(new
    {
        examId = exam.Id,
        examTitle = exam.Title,
        isBanned = true,
        alreadyBanned = false,
        message = "تم منع الطالب من هذا الامتحان. بقيت المحاولات والنتائج السابقة محفوظة."
    });
}).RequireAuthorization();

app.MapDelete("/api/teacher/students/{studentId:guid}/exam-bans/{examId:guid}", async (Guid studentId, Guid examId, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();

    var relationship = await ActiveTeacherStudentLink(db, teacherId, studentId);
    if (relationship is null)
        return Results.NotFound(new { message = "هذا الطالب غير مرتبط بك." });
    var exam = await db.Exams.FirstOrDefaultAsync(x => x.Id == examId && x.TeacherId == teacherId);
    if (exam is null)
        return Results.NotFound(new { message = "هذا الامتحان ليس ضمن امتحاناتك." });

    var ban = await db.TeacherStudentExamBans
        .FirstOrDefaultAsync(x => x.ExamId == exam.Id && x.StudentId == studentId);
    var removed = ban is not null;
    if (ban is not null)
    {
        db.TeacherStudentExamBans.Remove(ban);
        await db.SaveChangesAsync();
        await Audit(db, teacherId, "teacher.student.exam-unbanned", "TeacherStudentExamBan", ban.Id.ToString(),
            $"student={studentId}; exam={exam.Id}");
    }
    return Results.Ok(new
    {
        examId = exam.Id,
        examTitle = exam.Title,
        isBanned = false,
        removed,
        message = removed ? "تم رفع المنع عن هذا الامتحان." : "لم يكن الطالب ممنوعاً من هذا الامتحان."
    });
}).RequireAuthorization();

app.MapPost("/api/teacher/students/{studentId:guid}/suspend", async (Guid studentId, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();

    var relationship = await ActiveTeacherStudentLink(db, teacherId, studentId);
    if (relationship is null)
        return Results.NotFound(new { message = "هذا الطالب غير مرتبط بك." });
    if (relationship.SuspendedAtUtc.HasValue)
        return Results.Ok(new
        {
            studentId,
            status = "Suspended",
            suspendedAtUtc = relationship.SuspendedAtUtc,
            alreadySuspended = true,
            message = "الطالب موقوف عن جميع امتحاناتك بالفعل."
        });

    relationship.SuspendedAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync();
    await Audit(db, teacherId, "teacher.student.suspended", "TeacherStudentRequest", relationship.Id.ToString(),
        $"student={studentId}");
    return Results.Ok(new
    {
        studentId,
        status = "Suspended",
        suspendedAtUtc = relationship.SuspendedAtUtc,
        alreadySuspended = false,
        message = "تم إيقاف الطالب عن جميع امتحاناتك. لن يبدأ أو يستكمل أي محاولة حتى إعادة تفعيله."
    });
}).RequireAuthorization();

app.MapPost("/api/teacher/students/{studentId:guid}/reactivate", async (Guid studentId, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();

    var relationship = await ActiveTeacherStudentLink(db, teacherId, studentId);
    if (relationship is null)
        return Results.NotFound(new { message = "هذا الطالب غير مرتبط بك." });
    if (!relationship.SuspendedAtUtc.HasValue)
        return Results.Ok(new
        {
            studentId,
            status = "Active",
            alreadyActive = true,
            message = "الطالب غير موقوف بالفعل."
        });

    relationship.SuspendedAtUtc = null;
    await db.SaveChangesAsync();
    await Audit(db, teacherId, "teacher.student.reactivated", "TeacherStudentRequest", relationship.Id.ToString(),
        $"student={studentId}");
    return Results.Ok(new
    {
        studentId,
        status = "Active",
        alreadyActive = false,
        message = "تمت إعادة تفعيل الطالب. عادت امتحاناتك المتاحة له وفق المرحلة والصف."
    });
}).RequireAuthorization();

app.MapDelete("/api/teacher/students/{studentId:guid}", async (Guid studentId, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();

    var relationship = await ActiveTeacherStudentLink(db, teacherId, studentId);
    if (relationship is null)
        return Results.NotFound(new { message = "هذا الطالب غير مرتبط بك." });

    await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
    relationship.Status = "Unlinked";
    relationship.UnlinkedAtUtc = DateTime.UtcNow;
    relationship.SuspendedAtUtc = null;
    var bans = await db.TeacherStudentExamBans
        .Where(x => x.TeacherId == teacherId && x.StudentId == studentId)
        .ToListAsync();
    db.TeacherStudentExamBans.RemoveRange(bans);
    await db.SaveChangesAsync();
    await tx.CommitAsync();
    await Audit(db, teacherId, "teacher.student.unlinked", "TeacherStudentRequest", relationship.Id.ToString(),
        $"student={studentId}; removedExamBans={bans.Count}");
    return Results.Ok(new
    {
        studentId,
        unlinked = true,
        removedExamBans = bans.Count,
        message = "تم فك ارتباط الطالب بك فقط. لم يُحذف حسابه أو بياناته أو نتائجه السابقة، وعلاقته بمدرسين آخرين لم تتأثر."
    });
}).RequireAuthorization();

// --- Teacher lookup, consent-based link requests, invitations, and student editing ---
app.MapGet("/api/teacher/students/lookup", async (ClaimsPrincipal p, AppDbContext db, string? phone) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var normalizedPhone = IdentityNormalizer.NormalizePhone(phone);
    if (normalizedPhone is null)
        return Results.BadRequest(new { message = "أدخل رقم هاتف صحيحاً للبحث.", field = "phone" });
    // Phone lookup is an onboarding surface, not a directory. Until an accepted
    // relationship exists, return only a masked name + existence so a teacher
    // cannot harvest students' names, stages, and grades by enumerating phones.
    var matches = await db.Users.AsNoTracking()
        .Where(x => x.Role == "Student" && (x.StudentPhone == normalizedPhone || x.ParentPhone == normalizedPhone))
        .OrderBy(x => x.FullName)
        .Take(10)
        .Select(x => new
        {
            studentId = x.Id,
            fullName = x.FullName,
            stage = x.Stage,
            grade = x.Grade,
            requestStatus = db.TeacherStudentRequests
                .Where(r => r.TeacherId == teacherId && r.StudentId == x.Id)
                .OrderByDescending(r => r.CreatedAtUtc)
                .Select(r => r.Status == "Accepted" && r.UnlinkedAtUtc != null ? "Unlinked" : r.Status)
                .FirstOrDefault()
        })
        .ToListAsync();
    var items = matches.Select(x =>
    {
        var accepted = string.Equals(x.requestStatus, "Accepted", StringComparison.Ordinal);
        return new
        {
            studentId = x.studentId,
            fullName = accepted ? x.fullName : MaskStudentName(x.fullName),
            masked = !accepted,
            stage = accepted ? x.stage : null,
            grade = accepted ? x.grade : null,
            requestStatus = x.requestStatus
        };
    }).ToList();
    await Audit(db, teacherId, "teacher.student.lookup", "User", null, $"matches={items.Count};masked={items.Count(x => x.masked)}");
    return Results.Ok(new
    {
        found = items.Count > 0,
        canInvite = items.Count == 0,
        items,
        message = items.Count == 0
            ? "لا يوجد حساب طالب بهذا الرقم. يمكنك إضافة طالب جديد عبر دعوة يفعّلها بنفسه."
            : "تم العثور على حساب الطالب. أرسل طلب ارتباط ليقرره الطالب."
    });
}).RequireAuthorization().RequireRateLimiting("lookup");

app.MapPost("/api/teacher/students/request", async (TeacherStudentRequestCreate req, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    AppUser? student = null;
    if (Guid.TryParse(req?.StudentId, out var studentId) && studentId != Guid.Empty)
        student = await db.Users.FirstOrDefaultAsync(x => x.Id == studentId && x.Role == "Student");
    else if (!string.IsNullOrWhiteSpace(req?.Phone))
    {
        var phone = IdentityNormalizer.NormalizePhone(req.Phone);
        if (phone is null) return Results.BadRequest(new { message = "أدخل رقم هاتف صحيحاً." });
        student = await db.Users.FirstOrDefaultAsync(x => x.Role == "Student" && x.StudentPhone == phone);
    }
    if (student is null) return Results.NotFound(new { message = "لم نعثر على حساب طالب بهذه البيانات." });

    var existingRows = await db.TeacherStudentRequests
        .Where(x => x.TeacherId == teacherId && x.StudentId == student.Id)
        .ToListAsync();
    if (existingRows.Any(x => x.Status == "Accepted"))
        return Results.Conflict(new { message = "هذا الطالب مرتبط بك بالفعل.", status = "Accepted" });
    if (existingRows.Any(x => x.Status == "Pending"))
        return Results.Conflict(new { message = "يوجد طلب ارتباط معلّق مع هذا الطالب بالفعل.", status = "Pending" });

    var newRequest = new TeacherStudentRequest
    {
        TeacherId = teacherId,
        StudentId = student.Id,
        Status = "Pending",
        InitiatedBy = "Teacher",
        CreatedAtUtc = DateTime.UtcNow
    };
    db.TeacherStudentRequests.Add(newRequest);
    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException ex) when (IsUniqueViolation(ex.InnerException))
    {
        return Results.Conflict(new { message = "يوجد طلب ارتباط معلّق مع هذا الطالب بالفعل.", status = "Pending" });
    }
    await Audit(db, teacherId, "teacher.link-request.created", "TeacherStudentRequest", newRequest.Id.ToString(),
        $"student={student.Id}");
    return Results.Ok(new
    {
        requestId = newRequest.Id,
        studentId = student.Id,
        studentName = student.FullName,
        status = "Pending",
        message = existingRows.Any(x => x.Status is "Rejected" or "Unlinked")
            ? "تم إرسال طلب ارتباط جديد. الطلب السابق محفوظ في السجل، ولن تظهر بيانات الطالب التفصيلية إلا بعد موافقته."
            : "تم إرسال طلب ارتباط. لن تظهر بيانات الطالب التفصيلية إلا بعد موافقته."
    });
}).RequireAuthorization().RequireRateLimiting("api");

app.MapPost("/api/teacher/students/invite", async (TeacherInvitationRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var fullName = IdentityNormalizer.NormalizeDisplayName(req?.FullName);
    var stage = req?.Stage?.Trim();
    var grade = req?.Grade?.Trim();
    if (fullName.Length is < 3 or > 120)
        return Results.BadRequest(new { message = "أدخل اسم الطالب بشكل صحيح.", field = "fullName" });
    var invitationNameError = IdentityNormalizer.StudentFullNameError(fullName);
    if (invitationNameError is not null)
        return Results.BadRequest(new { message = invitationNameError, field = "fullName" });
    var parentName = IdentityNormalizer.NormalizeDisplayName(req?.ParentName);
    var parentNameError = IdentityNormalizer.ParentNameError(parentName);
    if (parentNameError is not null)
        return Results.BadRequest(new { message = parentNameError, field = "parentName" });
    if (!EducationCatalog.IsValid(stage, grade))
        return Results.BadRequest(new { message = "اختر المرحلة التعليمية والصف الدراسي المناسب لها.", field = "stage" });
    var studentPhone = IdentityNormalizer.NormalizePhone(req?.StudentPhone);
    var parentPhone = IdentityNormalizer.NormalizePhone(req?.ParentPhone);
    if (studentPhone is null)
        return Results.BadRequest(new { message = IdentityNormalizer.PhoneConflictMessage("student"), field = "studentPhone" });
    if (parentPhone is null)
        return Results.BadRequest(new { message = IdentityNormalizer.PhoneConflictMessage("parent"), field = "parentPhone" });
    if (studentPhone == parentPhone)
        return Results.BadRequest(new { message = "رقم هاتف الطالب يجب أن يختلف عن رقم هاتف ولي الأمر.", field = "parentPhone" });

    var existingAccount = await db.Users.AsNoTracking()
        .Where(x => x.Role == "Student" && x.StudentPhone == studentPhone)
        .Select(x => new { x.Id, x.FullName })
        .FirstOrDefaultAsync();
    if (existingAccount is not null)
        return Results.Conflict(new
        {
            message = "يوجد حساب طالب بهذا الرقم بالفعل. استخدم طلب الارتباط بدلاً من إنشاء حساب جديد.",
            studentId = existingAccount.Id,
            existingStudentName = existingAccount.FullName
        });
    var normalizedName = IdentityNormalizer.NormalizeName(fullName);
    if (normalizedName.Length == 0)
        return Results.BadRequest(new { message = "أدخل اسم طالب صحيحاً يحتوي على حروف.", field = "fullName" });
    var nameMatch = await db.Users.AsNoTracking()
        .Where(x => x.Role == "Student" && x.NormalizedName == normalizedName)
        .Select(x => new { x.Id, x.FullName })
        .FirstOrDefaultAsync();
    if (nameMatch is not null)
        return Results.Conflict(new
        {
            message = "يوجد حساب طالب بنفس الاسم بالفعل. استخدم طلب الارتباط بدلاً من إنشاء حساب مكرر.",
            studentId = nameMatch.Id,
            existingStudentName = nameMatch.FullName
        });

    var pending = await db.TeacherStudentInvitations
        .Where(x => x.TeacherId == teacherId && x.StudentPhone == studentPhone && x.Status == "Pending" && x.ExpiresAtUtc > DateTime.UtcNow)
        .OrderByDescending(x => x.CreatedAtUtc)
        .FirstOrDefaultAsync();
    if (pending is not null)
        return Results.Ok(new
        {
            invitationId = pending.Id,
            token = pending.Token,
            inviteUrl = $"{allowedOrigins[0]}/?invite={Uri.EscapeDataString(pending.Token)}",
            status = pending.Status,
            expiresAtUtc = pending.ExpiresAtUtc,
            message = "توجد دعوة معلّقة لهذا الرقم بالفعل. شارك الرابط نفسه مع الطالب."
        });

    var invitation = new TeacherStudentInvitation
    {
        TeacherId = teacherId,
        StudentName = fullName,
        StudentPhone = studentPhone,
        ParentPhone = parentPhone,
        ParentName = parentName.Length > 0 ? parentName : null,
        Stage = stage,
        Grade = grade,
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
        Status = "Pending",
        ExpiresAtUtc = DateTime.UtcNow.AddDays(14)
    };
    db.TeacherStudentInvitations.Add(invitation);
    await db.SaveChangesAsync();
    await Audit(db, teacherId, "teacher.student.invited", "TeacherStudentInvitation", invitation.Id.ToString(),
        $"studentPhone={studentPhone}");
    return Results.Ok(new
    {
        invitationId = invitation.Id,
        token = invitation.Token,
        inviteUrl = $"{allowedOrigins[0]}/?invite={Uri.EscapeDataString(invitation.Token)}",
        status = invitation.Status,
        expiresAtUtc = invitation.ExpiresAtUtc,
        message = "تم إنشاء رابط دعوة. لا توجد خدمة رسائل تلقائية؛ أرسل الرابط للطالب ليُنشئ حسابه بنفسه ويوافق على الارتباط."
    });
}).RequireAuthorization().RequireRateLimiting("api");

app.MapGet("/api/teacher/students/invitations", async (ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var now = DateTime.UtcNow;
    var rows = await db.TeacherStudentInvitations.AsNoTracking()
        .Where(x => x.TeacherId == teacherId)
        .OrderByDescending(x => x.CreatedAtUtc)
        .Select(x => new
        {
            invitationId = x.Id,
            x.StudentName,
            x.StudentPhone,
            x.ParentPhone,
            x.ParentName,
            x.Stage,
            x.Grade,
            x.Token,
            x.Status,
            x.CreatedAtUtc,
            x.ExpiresAtUtc,
            x.AcceptedAtUtc,
            effectiveStatus = x.Status == "Pending" && x.ExpiresAtUtc <= now ? "Expired" : x.Status
        })
        .ToListAsync();
    return Results.Ok(rows.Select(x => new
    {
        x.invitationId, x.StudentName, x.StudentPhone, x.ParentPhone, x.Stage, x.Grade, x.Token,
        x.Status, x.effectiveStatus, x.CreatedAtUtc, x.ExpiresAtUtc, x.AcceptedAtUtc,
        inviteUrl = $"{allowedOrigins[0]}/?invite={Uri.EscapeDataString(x.Token)}"
    }).ToList());
}).RequireAuthorization();

app.MapPut("/api/teacher/students/{studentId:guid}/profile", async (Guid studentId, TeacherStudentProfileUpdateRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var relationship = await ActiveTeacherStudentLink(db, teacherId, studentId);
    if (relationship is null) return Results.NotFound(new { message = "هذا الطالب غير مرتبط بك." });
    var student = await db.Users.FirstOrDefaultAsync(x => x.Id == studentId && x.Role == "Student");
    if (student is null) return Results.NotFound();
    var payload = new StudentProfileUpdateRequest(req.FullName, req.StudentPhone, req.ParentPhone, req.Stage, req.Grade, req.ParentName);
    var result = await ApplyStudentProfile(db, student, payload, teacherId, "Teacher", "Teacher");
    if (result.Error is not null) return Results.Json(new { message = result.Error }, statusCode: result.Status);
    await db.SaveChangesAsync();
    await Audit(db, teacherId, "teacher.student.profile.updated", "AppUser", studentId.ToString());
    return Results.Ok(new
    {
        studentId,
        student.FullName,
        student.StudentPhone,
        student.ParentPhone,
        student.ParentName,
        student.Stage,
        student.Grade,
        changed = result.Changed,
        message = result.Changed
            ? "تم تحديث بيانات الطالب. ستبقى محاولاته ونتائجه السابقة محفوظة بلقطات مرحلته وصفه وقت المحاولة."
            : "تم تحديث بيانات الطالب بنجاح."
    });
}).RequireAuthorization();

app.MapGet("/api/teacher/students/{studentId:guid}/academic-history", async (Guid studentId, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var relationship = await ActiveTeacherStudentLink(db, teacherId, studentId);
    if (relationship is null) return Results.NotFound(new { message = "هذا الطالب غير مرتبط بك." });
    var student = await db.Users.AsNoTracking()
        .Where(x => x.Id == studentId && x.Role == "Student")
        .Select(x => new { x.Id, x.FullName, x.Email, x.StudentCode, x.Stage, x.Grade, x.StudentPhone, x.ParentPhone })
        .FirstOrDefaultAsync();
    if (student is null) return Results.NotFound();
    var changes = await db.StudentAcademicChanges.AsNoTracking()
        .Where(x => x.StudentId == studentId)
        .OrderByDescending(x => x.ChangedAtUtc)
        .Select(x => new { x.Id, x.PreviousStage, x.PreviousGrade, x.NewStage, x.NewGrade, x.ChangedByRole, x.Source, x.ChangedAtUtc })
        .ToListAsync();
    var attempts = await db.Submissions.AsNoTracking()
        .Where(s => s.StudentId == studentId && s.Status != "InProgress" && s.Status != "Abandoned")
        .OrderByDescending(s => s.SubmittedAtUtc ?? s.StartedAtUtc)
        .Select(s => new
        {
            s.Id,
            s.ExamId,
            ExamTitle = s.Exam!.Title,
            ExamStage = s.Exam.Stage,
            ExamGrade = s.Exam.Grade,
            ExamAcademicYear = s.Exam.AcademicYear,
            s.StageAtAttempt,
            s.GradeAtAttempt,
            s.AcademicYearAtAttempt,
            s.Status,
            s.Score,
            s.MaxScore,
            s.SubmittedAtUtc,
            s.StartedAtUtc,
            s.AttemptNumber
        })
        .ToListAsync();
    var history = attempts
        .GroupBy(a => new
        {
            Stage = a.StageAtAttempt ?? a.ExamStage ?? "",
            Grade = a.GradeAtAttempt ?? a.ExamGrade ?? "",
            AcademicYear = a.AcademicYearAtAttempt ?? a.ExamAcademicYear ?? ""
        })
        .Select(g => new
        {
            stage = g.Key.Stage,
            grade = g.Key.Grade,
            academicYear = g.Key.AcademicYear,
            attemptsCount = g.Count(),
            items = g.ToList()
        })
        .OrderByDescending(g => g.academicYear)
        .ToList();
    return Results.Ok(new { student, changes, history, serverUtc = DateTime.UtcNow });
}).RequireAuthorization();

// --- Teacher public portfolio (Draft stays private; Published is readable by students) ---
app.MapGet("/api/teacher/profile", async (ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var teacher = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == teacherId && x.Role == "Teacher");
    if (teacher is null) return Results.Unauthorized();
    var profile = await db.TeacherProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.TeacherId == teacherId);
    var suggestedSlug = $"{IdentityNormalizer.Slugify(teacher.FullName, "teacher")}-{teacher.Id.ToString("N")[..4]}";
    return Results.Ok(new
    {
        profile = profile is null ? null : new
        {
            profile.Id,
            profile.PublicSlug,
            profile.ProfessionalName,
            profile.Specialty,
            profile.Bio,
            profile.Qualifications,
            profile.Experience,
            profile.YearsOfExperience,
            profile.TeachingStyle,
            profile.ExtraInfo,
            profile.AvatarUrl,
            profile.CoverUrl,
            profile.ContactPhone,
            stages = SafeJsonArray(profile.StagesJson),
            grades = SafeJsonArray(profile.GradesJson),
            socialLinks = SafeSocialLinks(profile.SocialLinksJson),
            profile.ShowEmail,
            profile.ShowPhone,
            profile.Status,
            profile.PublishedAtUtc,
            profile.UpdatedAtUtc
        },
        teacher = new { teacher.Id, teacher.FullName, teacher.Email, teacher.StudentPhone },
        suggestedSlug,
        publicUrl = profile is null ? null : $"/t/{profile.PublicSlug}"
    });
}).RequireAuthorization();

app.MapPut("/api/teacher/profile", async (TeacherProfileUpdateRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var teacher = await db.Users.FirstOrDefaultAsync(x => x.Id == teacherId && x.Role == "Teacher");
    if (teacher is null) return Results.Unauthorized();

    var professionalName = IdentityNormalizer.NormalizeDisplayName(req?.ProfessionalName);
    if (professionalName.Length is < 2 or > 160)
        return Results.BadRequest(new { message = "أدخل الاسم المهني بين حرفين و160 حرفاً.", field = "professionalName" });
    if (req?.YearsOfExperience is < 0 or > 70)
        return Results.BadRequest(new { message = "سنوات الخبرة يجب أن تكون بين 0 و70.", field = "yearsOfExperience" });

    var stages = (req?.Stages ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray();
    if (stages.Any(x => !EducationCatalog.IsValidStage(x)))
        return Results.BadRequest(new { message = "إحدى المراحل التعليمية غير صحيحة.", field = "stages" });
    var grades = (req?.Grades ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray();
    if (grades.Any(grade => !stages.Any(stage => EducationCatalog.GradesFor(stage).Contains(grade, StringComparer.Ordinal))))
        return Results.BadRequest(new { message = "إحدى الصفوف لا تتبع المراحل التي اخترتها.", field = "grades" });

    var avatar = CleanProfileUrl(req?.AvatarUrl);
    var cover = CleanProfileUrl(req?.CoverUrl);
    if (avatar == "invalid" || cover == "invalid")
        return Results.BadRequest(new { message = "روابط الصورة يجب أن تكون روابط HTTPS صحيحة.", field = "avatarUrl" });
    var contactPhone = string.IsNullOrWhiteSpace(req?.ContactPhone) ? null : IdentityNormalizer.NormalizePhone(req.ContactPhone);
    if (!string.IsNullOrWhiteSpace(req?.ContactPhone) && contactPhone is null)
        return Results.BadRequest(new { message = "أدخل رقم هاتف تواصل صحيحاً (8 إلى 15 رقماً).", field = "contactPhone" });
    var links = (req?.SocialLinks ?? []).Where(x => x is not null)
        .Select(x => new SocialLinkInput(
            IdentityNormalizer.NormalizeDisplayName(x.Label),
            CleanProfileUrl(x.Url) ?? ""))
        .ToArray();
    if (links.Length > 8)
        return Results.BadRequest(new { message = "يمكن إضافة 8 روابط تواصل كحد أقصى.", field = "socialLinks" });
    if (links.Any(x => x.Label.Length is < 1 or > 40 || string.IsNullOrWhiteSpace(x.Url) || x.Url == "invalid"))
        return Results.BadRequest(new { message = "راجع اسم ورابط كل وسيلة تواصل.", field = "socialLinks" });

    var profile = await db.TeacherProfiles.FirstOrDefaultAsync(x => x.TeacherId == teacherId);
    if (profile is null)
    {
        profile = new TeacherProfile { TeacherId = teacherId, Status = "Draft" };
        db.TeacherProfiles.Add(profile);
    }

    var requestedSlug = (req?.PublicSlug ?? "").Trim().ToLowerInvariant();
    if (requestedSlug.Length > 0)
    {
        if (!IdentityNormalizer.IsValidPublicSlug(requestedSlug))
            return Results.BadRequest(new { message = "الرابط العام يقبل الحروف الإنجليزية الصغيرة والأرقام والشرطة فقط.", field = "publicSlug" });
        if (await db.TeacherProfiles.AnyAsync(x => x.PublicSlug == requestedSlug && x.Id != profile.Id))
            return Results.Conflict(new { message = "هذا الرابط العام مستخدم بالفعل. اختر رابطاً آخر.", field = "publicSlug" });
        profile.PublicSlug = requestedSlug;
    }
    else if (string.IsNullOrWhiteSpace(profile.PublicSlug))
    {
        var baseSlug = IdentityNormalizer.Slugify(professionalName, "teacher");
        if (baseSlug.Length < 3) baseSlug = "teacher";
        var candidate = baseSlug;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!await db.TeacherProfiles.AnyAsync(x => x.PublicSlug == candidate)) break;
            candidate = $"{baseSlug}-{RandomNumberGenerator.GetInt32(1000, 9999)}";
        }
        profile.PublicSlug = candidate;
    }

    profile.ProfessionalName = professionalName;
    profile.Specialty = IdentityNormalizer.NormalizeDisplayName(req?.Specialty);
    profile.Bio = req?.Bio?.Trim();
    profile.Qualifications = req?.Qualifications?.Trim();
    profile.Experience = req?.Experience?.Trim();
    profile.YearsOfExperience = req?.YearsOfExperience;
    profile.TeachingStyle = req?.TeachingStyle?.Trim();
    profile.ExtraInfo = req?.ExtraInfo?.Trim();
    profile.AvatarUrl = avatar;
    profile.CoverUrl = cover;
    profile.ContactPhone = contactPhone;
    profile.StagesJson = JsonSerializer.Serialize(stages);
    profile.GradesJson = JsonSerializer.Serialize(grades);
    profile.SocialLinksJson = JsonSerializer.Serialize(links);
    profile.ShowEmail = req?.ShowEmail == true;
    profile.ShowPhone = req?.ShowPhone == true;
    profile.UpdatedAtUtc = DateTime.UtcNow;
    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException ex) when (IsUniqueViolation(ex.InnerException))
    {
        return Results.Conflict(new { message = "هذا الرابط العام مستخدم بالفعل. اختر رابطاً آخر.", field = "publicSlug" });
    }
    await Audit(db, teacherId, "teacher.profile.updated", "TeacherProfile", profile.Id.ToString());
    return Results.Ok(new
    {
        profile.Id,
        profile.PublicSlug,
        profile.Status,
        profile.UpdatedAtUtc,
        publicUrl = $"/t/{profile.PublicSlug}",
        message = "تم حفظ الصفحة التعريفية. معاينتها متاحة قبل النشر، ولن تظهر للطلاب قبل الضغط على نشر."
    });
}).RequireAuthorization().RequireRateLimiting("api");

app.MapPost("/api/teacher/profile/publish", async (ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var profile = await db.TeacherProfiles.FirstOrDefaultAsync(x => x.TeacherId == teacherId);
    if (profile is null || string.IsNullOrWhiteSpace(profile.ProfessionalName))
        return Results.BadRequest(new { message = "أكمل بيانات صفحتك التعريفية قبل النشر." });
    profile.Status = "Published";
    profile.PublishedAtUtc = profile.PublishedAtUtc ?? DateTime.UtcNow;
    profile.UpdatedAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync();
    await Audit(db, teacherId, "teacher.profile.published", "TeacherProfile", profile.Id.ToString());
    return Results.Ok(new { profile.Id, profile.Status, profile.PublishedAtUtc, publicUrl = $"/t/{profile.PublicSlug}", message = "تم نشر صفحتك التعريفية. يستطيع الطلاب الآن عرض البيانات التي اخترت نشرها." });
}).RequireAuthorization();

app.MapPost("/api/teacher/profile/unpublish", async (ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var profile = await db.TeacherProfiles.FirstOrDefaultAsync(x => x.TeacherId == teacherId);
    if (profile is null) return Results.NotFound();
    profile.Status = "Draft";
    profile.UpdatedAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync();
    await Audit(db, teacherId, "teacher.profile.unpublished", "TeacherProfile", profile.Id.ToString());
    return Results.Ok(new { profile.Id, profile.Status, message = "تم إخفاء الصفحة التعريفية عن الطلاب. بياناتها محفوظة كما هي." });
}).RequireAuthorization();

app.MapGet("/api/teachers/{teacherId:guid}/profile", async (Guid teacherId, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
    var profile = await db.TeacherProfiles.AsNoTracking().Include(x => x.Teacher)
        .FirstOrDefaultAsync(x => x.TeacherId == teacherId && x.Status == "Published");
    if (profile?.Teacher is null)
        return Results.NotFound(new { message = "هذا المدرس لم ينشر صفحة تعريفية بعد." });
    return Results.Ok(PublicTeacherProfile(profile, profile.Teacher));
}).RequireAuthorization();

app.MapGet("/api/public/teachers/{slug}", async (string slug, AppDbContext db) =>
{
    var profile = await db.TeacherProfiles.AsNoTracking().Include(x => x.Teacher)
        .FirstOrDefaultAsync(x => x.PublicSlug == slug && x.Status == "Published");
    if (profile?.Teacher is null)
        return Results.NotFound(new { message = "الصفحة التعريفية غير موجودة أو غير منشورة." });
    return Results.Ok(PublicTeacherProfile(profile, profile.Teacher));
}).RequireRateLimiting("api");

app.MapGet("/api/teacher/overview", async (ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var examIds = db.Exams.Where(x => x.TeacherId == teacherId).Select(x => x.Id);
    var totalExams = await examIds.CountAsync();
    var publishedExams = await db.Exams.CountAsync(x => x.TeacherId == teacherId && x.IsPublished);
    var activeExams = await db.Exams.CountAsync(x => x.TeacherId == teacherId && (x.Status == "Active" || x.Status == "Paused"));
    var openableExams = await db.Exams.CountAsync(x => x.TeacherId == teacherId && x.IsPublished
        && x.Status != "Finished" && x.Status != "Active" && x.Status != "Paused");
    var participantCount = await db.ExamParticipants.CountAsync(x => examIds.Contains(x.ExamId));
    var studentsReached = await db.ExamParticipants.Where(x => examIds.Contains(x.ExamId)).Select(x => x.StudentId).Distinct().CountAsync();
    var pendingReview = await db.Submissions.CountAsync(x => examIds.Contains(x.ExamId) && x.Status == "NeedsReview");
    var submitted = await db.Submissions.CountAsync(x => examIds.Contains(x.ExamId) && x.Status != "InProgress" && x.Status != "Abandoned");
    var recent = await db.Exams.Where(x => x.TeacherId == teacherId).OrderByDescending(x => x.CreatedAtUtc).Take(5)
        .Select(x => new { x.Id, x.Title, x.Description, x.Status, x.IsPublished, x.CreatedAtUtc, x.DurationMinutes, ParticipantCount = x.Participants.Count, QuestionCount = x.Questions.Count }).ToListAsync();
    var live = await db.Exams.Where(x => x.TeacherId == teacherId && (x.Status == "Active" || x.Status == "Paused")).OrderByDescending(x => x.ScheduledAtUtc).Take(6)
        .Select(x => new { x.Id, x.Title, x.Status, x.ScheduledAtUtc, ParticipantCount = x.Participants.Count }).ToListAsync();
    return Results.Ok(new { totalExams, publishedExams, activeExams, openableExams, participantCount, studentsReached, pendingReview, submitted, recent, live, serverUtc = DateTime.UtcNow });
}).RequireAuthorization();

app.MapPost("/api/exams/{id:guid}/participants/generate", async (Guid id, ExamParticipantGenerateRequest? req, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var exam = await db.Exams.FirstOrDefaultAsync(x => x.Id == id && x.TeacherId == teacherId);
    if (exam is null) return Results.NotFound();
    if (exam.Status is "Active" or "Paused") return Results.Conflict(new { message = "لا يمكن إنشاء أكواد طلاب بعد بدء الجلسة." });
    var studentsQuery = db.Users.Where(x => x.Role == "Student"
        && db.TeacherStudentRequests.Any(r => r.TeacherId == teacherId && r.StudentId == x.Id
            && r.Status == "Accepted" && r.UnlinkedAtUtc == null && r.SuspendedAtUtc == null)
        && !db.TeacherStudentExamBans.Any(b => b.ExamId == id && b.StudentId == x.Id));
    if (!string.IsNullOrWhiteSpace(exam.Stage))
    {
        var examStage = exam.Stage;
        studentsQuery = studentsQuery.Where(x => x.Stage == examStage);
    }
    if (!string.IsNullOrWhiteSpace(exam.Grade))
    {
        var examGrade = exam.Grade;
        studentsQuery = studentsQuery.Where(x => x.Grade == examGrade);
    }
    var requestedStudentIds = req?.StudentIds;
    if (requestedStudentIds is { Length: > 0 }) studentsQuery = studentsQuery.Where(x => requestedStudentIds.Contains(x.Id));
    var students = await studentsQuery.Select(x => new { x.Id, x.FullName }).ToListAsync();
    if (students.Count == 0) return Results.Conflict(new { message = "لا يوجد طلاب مسجلون لديك ومطابقون لمرحلة الامتحان وصفه." });
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
}).RequireAuthorization().RequireRateLimiting("api");

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

app.MapPost("/api/exams/{id:guid}/results/publish", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    await FinalizeExpired(db);
    var exam = await db.Exams.FirstOrDefaultAsync(x => x.Id == id && x.TeacherId == teacherId);
    if (exam is null) return Results.NotFound();
    if (exam.Status != "Finished")
        return Results.Conflict(new { message = "أنهِ جلسة الامتحان أولاً قبل نشر النتائج." });
    if (exam.ResultsPublishedAtUtc.HasValue)
        return Results.Ok(new { exam.Id, exam.Status, exam.ResultsPublishedAtUtc, alreadyPublished = true });
    if (await db.Submissions.AnyAsync(x => x.ExamId == id && x.Status == "NeedsReview"))
        return Results.Conflict(new { message = "لا يمكن نشر النتائج قبل إكمال تصحيح جميع الأسئلة المقالية." });
    if (await db.Submissions.AnyAsync(x => x.ExamId == id && (x.Status == "Processing" || x.Status == "InProgress")))
        return Results.Conflict(new { message = "توجد محاولات قيد المعالجة. أعد المحاولة بعد لحظات." });
    exam.ResultsPublishedAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync();
    await Audit(db, teacherId, "results.published", "Exam", id.ToString());
    return Results.Ok(new { exam.Id, exam.Status, exam.ResultsPublishedAtUtc, alreadyPublished = false });
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
        var studentStage = student.Stage;
        var studentGrade = student.Grade;
        var publishedExam = await db.Exams
            .Where(x => x.IsPublished && x.Status != "Draft" && x.Status != "Finished"
                && x.AcademicYear == AcademicYear.Current()
                && db.TeacherStudentRequests.Any(r => r.TeacherId == x.TeacherId && r.StudentId == studentId
                    && r.Status == "Accepted" && r.UnlinkedAtUtc == null && r.SuspendedAtUtc == null)
                && !db.TeacherStudentExamBans.Any(b => b.ExamId == x.Id && b.StudentId == studentId)
                && (x.Stage == null || x.Stage == studentStage)
                && (x.Grade == null || x.Grade == studentGrade))
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
    if (exam.AcademicYear != AcademicYear.Current())
        return Results.Conflict(new { message = "هذا الامتحان من عام دراسي سابق ولا يمكن الانضمام إليه من قائمة الامتحانات الحالية." });
    if (exam.AvailableUntilUtc.HasValue && exam.AvailableUntilUtc.Value <= DateTime.UtcNow)
        return Results.Conflict(new { message = "انتهت فترة إتاحة هذا الامتحان. تواصل مع المدرس إذا كنت تحتاج إلى استثناء." });
    var restriction = await ExamAccessRestriction(db, exam.Id, studentId);
    if (restriction is not null)
        return Results.Json(new { message = restriction }, statusCode: StatusCodes.Status403Forbidden);
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

app.MapGet("/api/exams/available", async (ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var studentId)) return Results.Unauthorized();
    await FinalizeExpired(db);
    var now = DateTime.UtcNow;
    var studentProfile = await db.Users.AsNoTracking()
        .Where(x => x.Id == studentId && x.Role == "Student")
        .Select(x => new { x.Stage, x.Grade })
        .FirstOrDefaultAsync();
    if (studentProfile is null) return Results.Unauthorized();
    var studentStage = studentProfile.Stage;
    var studentGrade = studentProfile.Grade;

    // Only published, non-draft, non-finished exams are discoverable. Draft/unpublished exams
    // must never appear here, and no join codes or answer keys are included in the payload.
    // Students only see exams for teachers they are enrolled with and whose stage/grade match.
    var exams = await db.Exams.AsNoTracking()
        .Where(x => x.IsPublished && x.Status != "Draft" && x.Status != "Finished"
            && x.AcademicYear == AcademicYear.Current()
            && (!x.AvailableUntilUtc.HasValue || x.AvailableUntilUtc > now)
            && db.TeacherStudentRequests.Any(r => r.TeacherId == x.TeacherId && r.StudentId == studentId
                && r.Status == "Accepted" && r.UnlinkedAtUtc == null && r.SuspendedAtUtc == null)
            && !db.TeacherStudentExamBans.Any(b => b.ExamId == x.Id && b.StudentId == studentId)
            && (x.Stage == null || x.Stage == studentStage)
            && (x.Grade == null || x.Grade == studentGrade))
        .OrderByDescending(x => x.Status == "Active" || x.Status == "Paused")
        .ThenBy(x => x.ScheduledAtUtc ?? x.CreatedAtUtc)
        .Select(x => new
        {
            x.Id,
            x.Title,
            x.Description,
            x.DurationMinutes,
            x.MaxAttempts,
            x.Status,
            x.ScheduledAtUtc,
            x.AvailableFromUtc,
            x.AvailableUntilUtc,
            x.PausedAtUtc,
            x.Stage,
            x.Grade,
            x.CreatedAtUtc,
            TeacherName = db.Users.Where(u => u.Id == x.TeacherId).Select(u => u.FullName).FirstOrDefault(),
            QuestionCount = x.Questions.Count,
            TotalPoints = x.Questions.Sum(q => (decimal?)q.Points) ?? 0
        })
        .ToListAsync();

    if (exams.Count == 0)
        return Results.Ok(new { serverUtc = now, items = Array.Empty<object>() });

    var examIds = exams.Select(x => x.Id).ToList();
    var submissions = await db.Submissions.AsNoTracking()
        .Where(x => x.StudentId == studentId && examIds.Contains(x.ExamId))
        .Select(x => new { x.Id, x.ExamId, x.Status, x.StartedAtUtc, x.IsExceptionAttempt, x.ExtraTimeSeconds, x.AttemptNumber })
        .ToListAsync();
    var participants = await db.ExamParticipants.AsNoTracking()
        .Where(x => x.StudentId == studentId && examIds.Contains(x.ExamId))
        .Select(x => new { x.ExamId, x.Status, x.JoinedAtUtc })
        .ToListAsync();

    var items = new List<object>();
    foreach (var exam in exams)
    {
        var ownAttempts = submissions.Where(x => x.ExamId == exam.Id).ToList();
        var active = ownAttempts.Where(x => x.Status == "InProgress").OrderByDescending(x => x.AttemptNumber).FirstOrDefault();
        var attemptsUsed = ownAttempts.Count(x => x.Status != "InProgress" && x.Status != "Abandoned" && !x.IsExceptionAttempt);

        // A student with no remaining attempts must not see the exam again, unless an
        // in-progress attempt exists that they are allowed to resume.
        if (active is null && attemptsUsed >= exam.MaxAttempts) continue;

        var participant = participants.FirstOrDefault(x => x.ExamId == exam.Id);
        int? remainingSeconds = null;
        DateTime? deadlineUtc = null;
        if (active is not null)
        {
            var deadline = active.StartedAtUtc.AddMinutes(exam.DurationMinutes).AddSeconds(active.ExtraTimeSeconds);
            if (exam.AvailableUntilUtc.HasValue && exam.AvailableUntilUtc.Value < deadline) deadline = exam.AvailableUntilUtc.Value;
            if (exam.Status == "Paused" && exam.PausedAtUtc.HasValue) deadline = deadline.Add(now - exam.PausedAtUtc.Value);
            deadlineUtc = deadline;
            remainingSeconds = Math.Max(0, (int)Math.Ceiling((deadline - now).TotalSeconds));
        }

        items.Add(new
        {
            examId = exam.Id,
            examTitle = exam.Title,
            examDescription = exam.Description,
            teacherName = exam.TeacherName,
            durationMinutes = exam.DurationMinutes,
            examStatus = exam.Status,
            scheduledAtUtc = exam.ScheduledAtUtc,
            stage = exam.Stage,
            grade = exam.Grade,
            questionCount = exam.QuestionCount,
            totalPoints = exam.TotalPoints,
            maxAttempts = exam.MaxAttempts,
            attemptsUsed,
            attemptsRemaining = Math.Max(0, exam.MaxAttempts - attemptsUsed),
            hasActiveAttempt = active is not null,
            remainingSeconds,
            deadlineUtc,
            participantStatus = participant?.Status,
            joinedAtUtc = participant?.JoinedAtUtc,
            isNew = participant is null
        });
    }

    return Results.Ok(new { serverUtc = now, items });
}).RequireAuthorization();

app.MapPost("/api/exams/{id:guid}/enter", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var studentId)) return Results.Unauthorized();
    await FinalizeExpired(db);
    var now = DateTime.UtcNow;
    var exam = await db.Exams.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    if (exam is null || !exam.IsPublished || exam.Status == "Draft")
        return Results.NotFound(new { message = "الامتحان غير متاح أو لم يُنشر بعد." });
    if (exam.Status == "Finished")
        return Results.Conflict(new { message = "انتهت جلسة هذا الامتحان. تواصل مع المدرس." });
    if (exam.AcademicYear != AcademicYear.Current())
        return Results.Conflict(new { message = "هذا الامتحان من عام دراسي سابق. الامتحانات السابقة محفوظة في سجلك الأكاديمي ولا تظهر ضمن الامتحانات الحالية." });
    if (exam.AvailableUntilUtc.HasValue && exam.AvailableUntilUtc.Value <= now)
        return Results.Conflict(new { message = "انتهت فترة إتاحة هذا الامتحان. تواصل مع المدرس إذا كنت تحتاج إلى استثناء." });

    var restriction = await ExamAccessRestriction(db, exam.Id, studentId);
    if (restriction is not null)
        return Results.Json(new { message = restriction }, statusCode: StatusCodes.Status403Forbidden);

    var submissions = await db.Submissions.AsNoTracking()
        .Where(x => x.ExamId == id && x.StudentId == studentId)
        .OrderByDescending(x => x.AttemptNumber)
        .ToListAsync();
    var active = submissions.FirstOrDefault(x => x.Status == "InProgress");
    var attemptsUsed = submissions.Count(x => x.Status != "InProgress" && x.Status != "Abandoned" && !x.IsExceptionAttempt);
    if (active is null && attemptsUsed >= exam.MaxAttempts)
        return Results.Conflict(new { message = "لقد استنفدت عدد المحاولات المتاحة لهذا الامتحان." });

    var participant = await db.ExamParticipants.FirstOrDefaultAsync(x => x.ExamId == id && x.StudentId == studentId);
    if (participant is null)
    {
        // ExamParticipants.Code is unique across all exams, so each auto-enrollment gets
        // its own participant code. A concurrent click may win the race; use that row.
        for (var attempt = 0; attempt < 3 && participant is null; attempt++)
        {
            var candidate = new ExamParticipant
            {
                ExamId = id,
                StudentId = studentId,
                Code = await NewStudentCode(db),
                Status = "Waiting",
                JoinedAtUtc = now,
                LastSeenAtUtc = now
            };
            db.ExamParticipants.Add(candidate);
            try
            {
                await db.SaveChangesAsync();
                participant = candidate;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex.InnerException))
            {
                db.Entry(candidate).State = EntityState.Detached;
                participant = await db.ExamParticipants.FirstOrDefaultAsync(x => x.ExamId == id && x.StudentId == studentId);
                if (participant is null) db.ChangeTracker.Clear();
            }
        }
        if (participant is null)
            return Results.Conflict(new { message = "تعذر الانضمام للامتحان بسبب طلب متزامن. أعد المحاولة بعد لحظة." });
    }

    participant.JoinedAtUtc ??= now;
    participant.LastSeenAtUtc = now;
    if (participant.Status is "Invited" or "Absent") participant.Status = "Waiting";
    // Entering a fresh attempt after a previous submission is legitimate when attempts
    // remain. Keeping the participant waiting lets the lobby start the next attempt.
    if (active is null && participant.Status == "Submitted") participant.Status = "Waiting";
    await db.SaveChangesAsync();

    var teacherName = await db.Users.Where(x => x.Id == exam.TeacherId).Select(x => x.FullName).FirstOrDefaultAsync();
    var questionCount = await db.Questions.CountAsync(x => x.ExamId == id);
    int? remainingSeconds = null;
    DateTime? deadlineUtc = null;
    if (active is not null)
    {
        var deadline = ExamGrader.Deadline(active, exam.DurationMinutes, exam.AvailableUntilUtc);
        if (exam.Status == "Paused" && exam.PausedAtUtc.HasValue) deadline = deadline.Add(now - exam.PausedAtUtc.Value);
        deadlineUtc = deadline;
        remainingSeconds = Math.Max(0, (int)Math.Ceiling((deadline - now).TotalSeconds));
    }

    return Results.Ok(new
    {
        examId = exam.Id,
        examTitle = exam.Title,
        examDescription = exam.Description,
        teacherName,
        durationMinutes = exam.DurationMinutes,
        scheduledAtUtc = exam.ScheduledAtUtc,
        examStatus = exam.Status,
        participantStatus = participant.Status,
        joinedAtUtc = participant.JoinedAtUtc,
        submissionId = active?.Id,
        remainingSeconds,
        deadlineUtc,
        serverUtc = now,
        maxAttempts = exam.MaxAttempts,
        attemptsUsed,
        attemptsRemaining = Math.Max(0, exam.MaxAttempts - attemptsUsed),
        questionCount
    });
}).RequireAuthorization().RequireRateLimiting("join");

app.MapGet("/api/exams/{id:guid}/session", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var studentId)) return Results.Unauthorized();
    var participant = await db.ExamParticipants.Include(x => x.Exam).FirstOrDefaultAsync(x => x.ExamId == id && x.StudentId == studentId);
    if (participant?.Exam is null) return Results.Forbid();
    var exam = participant.Exam;
    var restriction = await ExamAccessRestriction(db, exam.Id, studentId);
    if (restriction is not null) return Results.Json(new { message = restriction }, statusCode: StatusCodes.Status403Forbidden);
    var now = DateTime.UtcNow;
    participant.JoinedAtUtc ??= now;
    if (participant.Status == "Invited") participant.Status = "Waiting";
    participant.LastSeenAtUtc = now;
    await db.SaveChangesAsync();
    var submission = await db.Submissions.Where(x => x.ExamId == exam.Id && x.StudentId == studentId).OrderByDescending(x => x.AttemptNumber).FirstOrDefaultAsync();
    var teacherName = await db.Users.Where(x => x.Id == exam.TeacherId).Select(x => x.FullName).FirstOrDefaultAsync();
    var effectiveDeadline = submission is not null && submission.Status == "InProgress" ? EffectiveDeadline(submission, exam, now) : (DateTime?)null;
    return Results.Ok(new
    {
        examId = exam.Id, examTitle = exam.Title, examDescription = exam.Description, teacherName,
        durationMinutes = exam.DurationMinutes, scheduledAtUtc = exam.ScheduledAtUtc, examStatus = exam.Status,
        participantStatus = participant.Status, joinedAtUtc = participant.JoinedAtUtc, submissionId = submission?.Id,
        remainingSeconds = RemainingSeconds(submission, exam, now), deadlineUtc = effectiveDeadline, serverUtc = now, maxAttempts = exam.MaxAttempts,
        questionCount = await db.Questions.CountAsync(x => x.ExamId == exam.Id)
    });
}).RequireAuthorization();

app.MapGet("/api/exams/{id:guid}/paper", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var studentId)) return Results.Unauthorized();
    await FinalizeExpired(db);
    var exam = await db.Exams.Include(x => x.Questions).FirstOrDefaultAsync(x => x.Id == id && x.IsPublished);
    if (exam is null) return Results.NotFound();
    if (exam.Status is "Draft" or "Scheduled" or "Waiting") return Results.Conflict(new { message = "لم يبدأ الامتحان بعد. انتظر تعليمات المدرس." });
    if (exam.Status == "Finished") return Results.Conflict(new { message = "انتهت جلسة الامتحان." });
    var participant = await db.ExamParticipants.FirstOrDefaultAsync(x => x.ExamId == id && x.StudentId == studentId);
    if (participant is null || participant.Status == "Invited") return Results.Forbid();
    var restriction = await ExamAccessRestriction(db, exam.Id, studentId);
    if (restriction is not null) return Results.Json(new { message = restriction }, statusCode: StatusCodes.Status403Forbidden);
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
        remainingSeconds = RemainingSeconds(submission, exam, now), deadlineUtc = EffectiveDeadline(submission, exam, now), serverUtc = now,
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
        examId = exam.Id, examTitle = exam.Title, examStatus = exam.Status, resultsPublishedAtUtc = exam.ResultsPublishedAtUtc, serverUtc = now,
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
        if (!await IsEligibleForExam(db, e.Id, userId)) return Results.NotFound();
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
  var restriction = await ExamAccessRestriction(db, e.Id, uid);
  if (restriction is not null) return Results.Json(new { message = restriction }, statusCode: StatusCodes.Status403Forbidden);
  var now = DateTime.UtcNow;
  if (e.AvailableFromUtc.HasValue && now < e.AvailableFromUtc.Value)
    return Results.Conflict(new { message = "لم يحن وقت إتاحة هذا الامتحان بعد. حاول في الموعد المحدد." });
  if (e.AvailableUntilUtc.HasValue && now >= e.AvailableUntilUtc.Value)
    return Results.Conflict(new { message = "انتهت فترة إتاحة هذا الامتحان ولا يمكن بدء محاولة جديدة." });
  var existing = await db.Submissions.FirstOrDefaultAsync(s => s.ExamId == id && s.StudentId == uid && s.Status == "InProgress");
  if (existing is not null)
  {
    if (!e.AllowResumeAfterFocusLoss && existing.FocusLossDetected) return Results.Conflict(new { message = "تم إيقاف استكمال المحاولة بعد مغادرة الامتحان وفقاً لإعدادات المدرس. تواصل مع المدرس إذا كان الخروج بسبب ظرف طارئ." });
    participant.Status = "Active"; participant.LastSeenAtUtc = now; await db.SaveChangesAsync();
    return Results.Ok(new { existing.Id, existing.StartedAtUtc, e.DurationMinutes, existing.ExtraTimeSeconds, remainingSeconds = RemainingSeconds(existing, e, now), deadlineUtc = EffectiveDeadline(existing, e, now), serverUtc = now, examStatus = e.Status });
  }
  var attemptsUsed = await db.Submissions.CountAsync(s => s.ExamId == id && s.StudentId == uid && s.Status != "InProgress" && s.Status != "Abandoned" && !s.IsExceptionAttempt);
  var attemptNumber = await db.Submissions.CountAsync(s => s.ExamId == id && s.StudentId == uid) + 1;
  if (attemptsUsed >= e.MaxAttempts) return Results.Conflict(new { message = "لقد استنفدت عدد المحاولات المتاحة لهذا الامتحان." });
  var studentSnapshot = await db.Users.AsNoTracking().Where(x => x.Id == uid)
    .Select(x => new { x.Stage, x.Grade }).FirstOrDefaultAsync();
  var sub = new Submission
  {
    ExamId = id,
    StudentId = uid,
    AttemptNumber = attemptNumber,
    MaxScore = e.Questions.Sum(q => q.Points),
    StartedAtUtc = now,
    StageAtAttempt = studentSnapshot?.Stage,
    GradeAtAttempt = studentSnapshot?.Grade,
    AcademicYearAtAttempt = e.AcademicYear
  };
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
      : Results.Ok(new { raced.Id, raced.StartedAtUtc, e.DurationMinutes, raced.ExtraTimeSeconds, remainingSeconds = RemainingSeconds(raced, e, now), deadlineUtc = EffectiveDeadline(raced, e, now), serverUtc = now, examStatus = e.Status });
  }
  await Audit(db, uid, "submission.started", "Submission", sub.Id.ToString());
  return Results.Ok(new { sub.Id, sub.StartedAtUtc, e.DurationMinutes, sub.ExtraTimeSeconds, remainingSeconds = RemainingSeconds(sub, e, now), deadlineUtc = EffectiveDeadline(sub, e, now), serverUtc = now, examStatus = e.Status });
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
  var examInfo = await db.Exams.Where(e => e.Id == sub.ExamId).Select(e => new { e.DurationMinutes, e.Status, e.AvailableUntilUtc }).FirstOrDefaultAsync();
  if (examInfo is null) return Results.NotFound();
  var restriction = await ExamAccessRestriction(db, sub.ExamId, uid);
  if (restriction is not null) return Results.Json(new { message = restriction }, statusCode: StatusCodes.Status403Forbidden);
  if (examInfo.Status is "Paused" or "Finished") return Results.Conflict(new { message = examInfo.Status == "Paused" ? "الامتحان متوقف مؤقتاً. ستُستأنف الجلسة عند سماح المدرس." : "انتهت الجلسة؛ لم تعد الإجابات قابلة للتعديل." });
  if (DateTime.UtcNow >= ExamGrader.Deadline(sub, examInfo.DurationMinutes, examInfo.AvailableUntilUtc)) return Results.BadRequest(new { message = "انتهى وقت الامتحان؛ تم إيقاف حفظ الإجابات الجديدة." });
  if (req.FocusLossDetected && !sub.FocusLossDetected) { sub.FocusLossCount++; sub.FocusLossDetected = true; }
  // Autosave is a heartbeat too, so a long essay is not mistaken for a dead session.
  var autosaveParticipant = await db.ExamParticipants.FirstOrDefaultAsync(x => x.ExamId == sub.ExamId && x.StudentId == uid);
  if (autosaveParticipant is not null) autosaveParticipant.LastSeenAtUtc = DateTime.UtcNow;
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
  await db.SaveChangesAsync(); await tx.CommitAsync(); var savedAt = DateTime.UtcNow; return Results.Ok(new { saved = true, answerCount = sub.Answers.Count, sub.FocusLossDetected, savedAtUtc = savedAt, serverUtc = savedAt, deadlineUtc = ExamGrader.Deadline(sub, examInfo.DurationMinutes, examInfo.AvailableUntilUtc) });
}).RequireAuthorization();
app.MapPost("/api/submissions/{id:guid}/submit", async (Guid id, SubmitRequest? req, ClaimsPrincipal p, AppDbContext db) =>
{
  if (p.FindFirstValue(ClaimTypes.Role) != "Student") return Results.Forbid();
  if (!Guid.TryParse(UserId(p), out var uid)) return Results.Unauthorized();
  await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
  await AcquireSubmissionLock(db, id);
  var access = await db.Submissions.AsNoTracking()
      .Where(s => s.Id == id && s.StudentId == uid)
      .Select(s => new { s.ExamId, s.Status })
      .FirstOrDefaultAsync();
  if (access is null) return Results.NotFound();
  // Checks restrictions before claiming the attempt, otherwise a banned student would
  // leave the submission stuck in Processing after a rejected submit request.
  if (access.Status == "InProgress")
  {
    var restriction = await ExamAccessRestriction(db, access.ExamId, uid);
    if (restriction is not null) return Results.Json(new { message = restriction }, statusCode: StatusCodes.Status403Forbidden);
  }
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
    var alreadyExam = await db.Exams.Where(e => e.Id == alreadySubmitted.ExamId)
        .Select(e => new { e.Status, e.ResultsPublishedAtUtc, e.ResultsReleaseMode, e.AvailableUntilUtc }).FirstOrDefaultAsync();
    var alreadyResultsAvailable = alreadyExam is not null && ResultsVisibleFor(alreadyExam.ResultsReleaseMode, alreadySubmitted.Status, alreadyExam.Status, alreadyExam.AvailableUntilUtc, alreadyExam.ResultsPublishedAtUtc, DateTime.UtcNow);
    return Results.Ok(new { alreadySubmitted.Id, Score = alreadyResultsAvailable ? alreadySubmitted.Score : (decimal?)null, MaxScore = alreadyResultsAvailable ? alreadySubmitted.MaxScore : (decimal?)null, alreadySubmitted.Status, ResultsAvailable = alreadyResultsAvailable });
  }
  var sub = await db.Submissions.Include(s => s.Answers).FirstOrDefaultAsync(s => s.Id == id && s.StudentId == uid);
  if (sub is null) return Results.NotFound();
  var timing = await db.Exams.Where(e => e.Id == sub.ExamId).Select(e => new { e.DurationMinutes, e.AvailableUntilUtc, e.Status, e.ResultsPublishedAtUtc, e.ResultsReleaseMode }).FirstOrDefaultAsync();
  if (timing is null) return Results.NotFound();
  var deadline = ExamGrader.Deadline(sub, timing.DurationMinutes, timing.AvailableUntilUtc);
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
  var resultsAvailable = ResultsVisibleFor(timing.ResultsReleaseMode, sub.Status, timing.Status, timing.AvailableUntilUtc, timing.ResultsPublishedAtUtc, DateTime.UtcNow);
  return Results.Ok(new { sub.Id, Score = resultsAvailable ? sub.Score : (decimal?)null, MaxScore = resultsAvailable ? sub.MaxScore : (decimal?)null, sub.Status, ResultsAvailable = resultsAvailable, late = isLate });
}).RequireAuthorization();
app.MapGet("/api/results/mine", async (ClaimsPrincipal p, AppDbContext db) =>
{
  await FinalizeExpired(db);
  if (!Guid.TryParse(UserId(p), out var studentId)) return Results.Unauthorized();
  var now = DateTime.UtcNow;
  var submissions = await db.Submissions.AsNoTracking()
    .Where(s => s.StudentId == studentId && s.Status != "InProgress" && s.Status != "Abandoned")
    .OrderByDescending(s => s.SubmittedAtUtc)
    .Select(s => new
    {
      s.Id,
      s.ExamId,
      ExamTitle = s.Exam!.Title,
      TeacherName = db.Users.Where(u => u.Id == s.Exam.TeacherId).Select(u => u.FullName).FirstOrDefault(),
      Subject = s.Exam.Description,
      ExamStatus = s.Exam.Status,
      s.Exam.AvailableUntilUtc,
      s.Exam.ResultsPublishedAtUtc,
      s.Exam.ResultsReleaseMode,
      s.Score,
      s.MaxScore,
      s.Status,
      s.SubmittedAtUtc,
      s.StartedAtUtc,
      s.AttemptNumber,
      s.FocusLossDetected,
      s.FocusLossCount,
      s.CopyAttemptCount,
      s.ScreenshotAttemptCount
    })
    .ToListAsync();
  return Results.Ok(submissions.Select(s =>
  {
    var resultsAvailable = ResultsVisibleFor(s.ResultsReleaseMode, s.Status, s.ExamStatus, s.AvailableUntilUtc, s.ResultsPublishedAtUtc, now);
    return new
    {
      s.Id,
      s.ExamId,
      s.ExamTitle,
      s.TeacherName,
      s.Subject,
      ResultsAvailable = resultsAvailable,
      Score = resultsAvailable ? s.Score : (decimal?)null,
      MaxScore = resultsAvailable ? s.MaxScore : (decimal?)null,
      s.Status,
      s.SubmittedAtUtc,
      s.StartedAtUtc,
      DurationSeconds = s.SubmittedAtUtc.HasValue ? (int)(s.SubmittedAtUtc.Value - s.StartedAtUtc).TotalSeconds : (int?)null,
      s.AttemptNumber,
      s.FocusLossDetected,
      s.FocusLossCount,
      s.CopyAttemptCount,
      s.ScreenshotAttemptCount
    };
  }).ToList());
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
  var q = db.Submissions.Where(s => s.ExamId == id && s.Status != "InProgress" && s.Status != "Abandoned"); var total = await q.CountAsync(); var items = await q.OrderByDescending(s => s.SubmittedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).Select(s => new { s.Id, Student = s.Student!.FullName, Email = s.Student.Email, s.Score, s.MaxScore, s.Status, s.SubmittedAtUtc, s.FocusLossDetected, s.IsExceptionAttempt }).ToListAsync(); return Results.Ok(new { items, page, pageSize, total });
}).RequireAuthorization().RequireRateLimiting("api");
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
  if (submission.Exam.Status == "Finished") return Results.Conflict(new { message = "لا يمكن منح محاولة إضافية لامتحان منتهٍ. أعد فتح الجلسة أولاً إن كان ذلك مقصوداً." });
  if (submission.Status == "Processing") return Results.Conflict(new { message = "التسليم قيد المعالجة. انتظر لحظة ثم أعد المحاولة." });
  if (submission.Status == "InProgress" && !submission.FocusLossDetected)
  {
    // A technical outage can leave an InProgress row with no focus-loss event.
    // Only allow recovery after the student's heartbeat has been stale for five
    // minutes, so a teacher cannot reset a genuinely active attempt by accident.
    var participant = await db.ExamParticipants.AsNoTracking()
      .FirstOrDefaultAsync(x => x.ExamId == submission.ExamId && x.StudentId == submission.StudentId);
    var lastSeen = participant?.LastSeenAtUtc;
    var stale = !lastSeen.HasValue || DateTime.UtcNow - lastSeen.Value > TimeSpan.FromMinutes(5);
    if (!stale)
      return Results.Conflict(new { message = "المحاولة لا تزال نشطة (آخر نشاط حديث). انتظر انتهاء المهلة أو اطلب من الطالب تحديث الصفحة قبل منح استثناء تقني." });
  }
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
  if (a is null)
  {
    // Repair legacy submissions whose essay answer row was never materialised.
    var questionList = await db.Questions.Where(x => x.ExamId == sub.ExamId).ToListAsync();
    ExamGrader.EnsureAnswers(sub, questionList);
    await db.SaveChangesAsync();
    a = sub.Answers.FirstOrDefault(x => x.Id == answerId && x.SubmissionId == id);
  }
  if (a is null) return Results.NotFound(new { message = "لم نعثر على إجابة هذا السؤال في المحاولة." });
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
  var gradedIds = sub.Answers.Where(x => x.AwardedPoints != null).Select(x => x.QuestionId).ToHashSet();
  sub.Status = essayQuestionIds.Any(qid => !gradedIds.Contains(qid)) ? "NeedsReview" : "Reviewed";
  await db.SaveChangesAsync();
  if (exam.Status == "Finished" && exam.ResultsReleaseMode == "AfterExamEnd" && !exam.ResultsPublishedAtUtc.HasValue)
  {
    var stillPending = await db.Submissions.AnyAsync(x => x.ExamId == sub.ExamId && (x.Status == "NeedsReview" || x.Status == "Processing" || x.Status == "InProgress"));
    if (!stillPending) { exam.ResultsPublishedAtUtc = DateTime.UtcNow; await db.SaveChangesAsync(); }
  }
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
  var rows = await db.Submissions.Where(s => s.ExamId == id && s.Status != "InProgress" && s.Status != "Abandoned").Select(s => new { s.Student!.FullName, s.Student.Email, s.Score, s.MaxScore, s.Status, s.SubmittedAtUtc }).ToListAsync();
  static string Esc(string? x)
  {
    var value = (x ?? "").Replace("\r", " ").Replace("\n", " ");
    if (value.Length > 0 && "=+-@".Contains(value.TrimStart()[0])) value = "'" + value;
    return "\"" + value.Replace("\"", "\"\"") + "\"";
  }
  var csv = "Student,Email,Score,MaxScore,Status,SubmittedAtUtc\r\n" + string.Join("\r\n", rows.Select(x => string.Join(",", new[] { Esc(x.FullName), Esc(x.Email), x.Score.ToString(System.Globalization.CultureInfo.InvariantCulture), x.MaxScore.ToString(System.Globalization.CultureInfo.InvariantCulture), Esc(x.Status), Esc(x.SubmittedAtUtc?.ToString("O")) })));
  await Audit(db, teacherId, "results.exported", "Exam", id.ToString()); return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8", $"exam-{id}-results.csv");
}).RequireAuthorization().RequireRateLimiting("api");

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

// --- Subscription plans and teacher package selection ---
app.MapGet("/api/subscriptions/plans", async (AppDbContext db, ClaimsPrincipal p) =>
{
    if (p.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var plans = await db.SubscriptionPlans.AsNoTracking()
        .Where(x => x.IsActive)
        .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
        .Select(x => new
        {
            x.Id, x.Code, x.Name, x.Description, x.IsDefault, x.SortOrder, x.FeaturesJson
        })
        .ToListAsync();
    return Results.Ok(plans.Select(x => new
    {
        x.Id,
        x.Code,
        x.Name,
        x.Description,
        x.IsDefault,
        features = SafeJsonArray(x.FeaturesJson)
    }).ToList());
}).RequireAuthorization();

app.MapGet("/api/teacher/subscription", async (ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var subscription = await CurrentSubscription(db, teacherId);
    var plans = await db.SubscriptionPlans.AsNoTracking()
        .Where(x => x.IsActive)
        .OrderBy(x => x.SortOrder)
        .Select(x => new { x.Id, x.Code, x.Name, x.Description, x.IsDefault, x.FeaturesJson })
        .ToListAsync();
    return Results.Ok(new
    {
        subscription = subscription is null ? null : new
        {
            subscription.Id,
            subscription.PlanId,
            planCode = subscription.Plan?.Code,
            planName = subscription.Plan?.Name,
            features = SafeJsonArray(subscription.Plan?.FeaturesJson),
            subscription.Status,
            subscription.StartedAtUtc,
            subscription.EndsAtUtc,
            subscription.ConfirmedAtUtc,
            subscription.CreatedAtUtc
        },
        plans = plans.Select(x => new
        {
            x.Id, x.Code, x.Name, x.Description, x.IsDefault, features = SafeJsonArray(x.FeaturesJson)
        }).ToList(),
        requiresSelection = subscription is null || subscription.Status != "Confirmed",
        serverUtc = DateTime.UtcNow
    });
}).RequireAuthorization();

app.MapPost("/api/teacher/subscription", async (SubscriptionSelectRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    var code = (req?.PlanCode ?? "").Trim().ToLowerInvariant();
    if (code.Length == 0) return Results.BadRequest(new { message = "اختر الخطة أولاً." });
    var plan = await db.SubscriptionPlans.FirstOrDefaultAsync(x => x.Code == code && x.IsActive);
    if (plan is null) return Results.NotFound(new { message = "الخطة غير موجودة أو غير متاحة حالياً." });
    var now = DateTime.UtcNow;
    var subscription = await db.TeacherSubscriptions
        .Where(x => x.TeacherId == teacherId)
        .OrderByDescending(x => x.Status == "Confirmed")
        .ThenByDescending(x => x.UpdatedAtUtc)
        .FirstOrDefaultAsync();
    if (subscription is null)
    {
        subscription = new TeacherSubscription
        {
            TeacherId = teacherId,
            PlanId = plan.Id,
            Status = "Confirmed",
            StartedAtUtc = now,
            ConfirmedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.TeacherSubscriptions.Add(subscription);
    }
    else
    {
        subscription.PlanId = plan.Id;
        subscription.Status = "Confirmed";
        subscription.StartedAtUtc ??= now;
        subscription.EndsAtUtc = null;
        subscription.ConfirmedAtUtc = now;
        subscription.UpdatedAtUtc = now;
    }
    await db.SaveChangesAsync();
    await Audit(db, teacherId, "teacher.subscription.confirmed", "TeacherSubscription", subscription.Id.ToString(),
        $"plan={plan.Code}");
    return Results.Ok(new
    {
        subscription.Id,
        planCode = plan.Code,
        planName = plan.Name,
        status = subscription.Status,
        startedAtUtc = subscription.StartedAtUtc,
        endsAtUtc = subscription.EndsAtUtc,
        confirmedAtUtc = subscription.ConfirmedAtUtc,
        message = "تم تأكيد اشتراكك في الخطة المجانية. لا توجد رسوم أو مدفوعات."
    });
}).RequireAuthorization();

// --- Admin dashboard (server-side role enforcement) ---
app.MapGet("/api/admin/overview", async (ClaimsPrincipal p, AppDbContext db, string? search, string? planStatus, string? sort, int page = 1, int pageSize = 25) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Admin") return Results.Forbid();
    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 100);
    var teachers = await db.Users.AsNoTracking()
        .Where(x => x.Role == "Teacher")
        .Select(x => new
        {
            x.Id,
            x.FullName,
            x.Email,
            x.RegistrationSource,
            x.CreatedAtUtc,
            x.EmailConfirmed,
            Subscription = db.TeacherSubscriptions.AsNoTracking()
                .Where(s => s.TeacherId == x.Id)
                .OrderByDescending(s => s.Status == "Confirmed")
                .ThenByDescending(s => s.UpdatedAtUtc)
                .Select(s => new
                {
                    s.Id,
                    PlanCode = s.Plan != null ? s.Plan.Code : null,
                    PlanName = s.Plan != null ? s.Plan.Name : null,
                    s.Status,
                    s.StartedAtUtc,
                    s.EndsAtUtc,
                    s.ConfirmedAtUtc,
                    s.CreatedAtUtc
                })
                .FirstOrDefault(),
            AcceptedStudentCount = db.TeacherStudentRequests.Count(r => r.TeacherId == x.Id
                && r.Status == "Accepted" && r.UnlinkedAtUtc == null),
            PendingRequestCount = db.TeacherStudentRequests.Count(r => r.TeacherId == x.Id && r.Status == "Pending"),
            ExamCount = db.Exams.Count(e => e.TeacherId == x.Id)
        })
        .ToListAsync();

    var term = (search ?? "").Trim();
    var filter = (planStatus ?? "").Trim();
    var filtered = teachers
        .Where(t => term.Length == 0
            || t.FullName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || t.Email.Contains(term, StringComparison.OrdinalIgnoreCase))
        .Where(t => filter.Length == 0 || filter == "All"
            || (filter == "Free" && t.Subscription?.PlanCode == PlatformDataUpgrade.FreePlanCode)
            || (filter == "Pending" && t.Subscription?.Status == "Pending")
            || (filter == "Confirmed" && t.Subscription?.Status == "Confirmed")
            || (filter == "None" && t.Subscription is null))
        .ToList();
    filtered = (sort ?? "newest").ToLowerInvariant() switch
    {
        "name" => filtered.OrderBy(t => t.FullName, StringComparer.Ordinal).ToList(),
        "students" => filtered.OrderByDescending(t => t.AcceptedStudentCount).ThenBy(t => t.FullName).ToList(),
        "oldest" => filtered.OrderBy(t => t.CreatedAtUtc).ToList(),
        _ => filtered.OrderByDescending(t => t.CreatedAtUtc).ToList()
    };

    var totalTeachers = teachers.Count;
    var freePlanTeachers = teachers.Count(t => t.Subscription?.PlanCode == PlatformDataUpgrade.FreePlanCode);
    var confirmedSubscriptions = teachers.Count(t => t.Subscription?.Status == "Confirmed");
    var pendingSubscriptions = teachers.Count(t => t.Subscription?.Status == "Pending");
    var withoutSubscription = teachers.Count(t => t.Subscription is null);
    var acceptedLinkedStudents = await db.TeacherStudentRequests.AsNoTracking()
        .CountAsync(r => r.Status == "Accepted" && r.UnlinkedAtUtc == null);
    var pendingRequests = await db.TeacherStudentRequests.AsNoTracking()
        .CountAsync(r => r.Status == "Pending");
    var pendingInvitations = await db.TeacherStudentInvitations.AsNoTracking()
        .CountAsync(x => x.Status == "Pending" && x.ExpiresAtUtc > DateTime.UtcNow);
    var totalStudents = await db.Users.AsNoTracking().CountAsync(x => x.Role == "Student");

    return Results.Ok(new
    {
        summary = new
        {
            totalTeachers,
            freePlanTeachers,
            confirmedSubscriptions,
            pendingSubscriptions,
            withoutSubscription,
            acceptedLinkedStudents,
            pendingRequests,
            pendingInvitations,
            totalStudents
        },
        page,
        pageSize,
        total = filtered.Count,
        items = filtered.Skip((page - 1) * pageSize).Take(pageSize).Select(t => new
        {
            teacherId = t.Id,
            t.FullName,
            t.Email,
            t.RegistrationSource,
            t.CreatedAtUtc,
            t.EmailConfirmed,
            subscription = t.Subscription,
            t.AcceptedStudentCount,
            t.PendingRequestCount,
            t.ExamCount
        }).ToList(),
        serverUtc = DateTime.UtcNow
    });
}).RequireAuthorization();

app.MapGet("/api/admin/subscription-settings", async (ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Admin") return Results.Forbid();
    var freePlan = await db.SubscriptionPlans.AsNoTracking()
        .FirstOrDefaultAsync(x => x.Code == PlatformDataUpgrade.FreePlanCode);
    var setting = await db.SystemSettings.AsNoTracking()
        .FirstOrDefaultAsync(x => x.Key == PlatformDataUpgrade.FreeFeaturesSettingKey);
    return Results.Ok(new
    {
        planCode = PlatformDataUpgrade.FreePlanCode,
        planName = freePlan?.Name,
        freePlanFeatures = SafeJsonArray(setting?.Value ?? freePlan?.FeaturesJson),
        settingKey = PlatformDataUpgrade.FreeFeaturesSettingKey,
        editableFeatures = SafeJsonArray(setting?.Value ?? freePlan?.FeaturesJson),
        note = "الميزات قابلة للتوسعة من إعدادات النظام. لا توجد حدود رقمية افتراضية للطلاب أو الامتحانات."
    });
}).RequireAuthorization();

app.MapPut("/api/admin/subscription-settings", async (AdminSubscriptionSettingsRequest req, ClaimsPrincipal p, AppDbContext db) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Admin") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var adminId)) return Results.Unauthorized();
    var features = (req?.Features ?? []).Select(x => x.Trim().ToLowerInvariant())
        .Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    if (features.Length is < 1 or > 50 || features.Any(x => x.Length > 64 || !x.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or ':')))
        return Results.BadRequest(new { message = "أدخل مفاتيح ميزات صحيحة (حروف إنجليزية وأرقام و _ - : بحد أقصى 64 حرفاً)." });
    var json = JsonSerializer.Serialize(features);
    var setting = await db.SystemSettings.FirstOrDefaultAsync(x => x.Key == PlatformDataUpgrade.FreeFeaturesSettingKey);
    if (setting is null)
    {
        setting = new SystemSetting { Key = PlatformDataUpgrade.FreeFeaturesSettingKey, Value = json, UpdatedAtUtc = DateTime.UtcNow };
        db.SystemSettings.Add(setting);
    }
    else
    {
        setting.Value = json;
        setting.UpdatedAtUtc = DateTime.UtcNow;
    }
    var freePlan = await db.SubscriptionPlans.FirstOrDefaultAsync(x => x.Code == PlatformDataUpgrade.FreePlanCode);
    if (freePlan is not null)
    {
        freePlan.FeaturesJson = json;
        freePlan.UpdatedAtUtc = DateTime.UtcNow;
    }
    await db.SaveChangesAsync();
    await Audit(db, adminId, "admin.subscription-settings.updated", "SystemSetting", PlatformDataUpgrade.FreeFeaturesSettingKey);
    return Results.Ok(new { features, message = "تم تحديث ميزات الخطة المجانية." });
}).RequireAuthorization();

app.MapGet("/api/audit", async (ClaimsPrincipal p, AppDbContext db, int page = 1, int pageSize = 50) =>
{
    if (p.FindFirstValue(ClaimTypes.Role) != "Teacher") return Results.Forbid();
    if (!Guid.TryParse(UserId(p), out var teacherId)) return Results.Unauthorized();
    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 100);
    var submissionIds = await db.Submissions.AsNoTracking()
        .Where(s => s.Exam!.TeacherId == teacherId)
        .Select(s => s.Id)
        .ToListAsync();
    var submissionIdStrings = submissionIds.Select(x => x.ToString()).ToList();
    // The teacher's own actions plus student-originated submission events for
    // this teacher's exams. Student submission events are written with the
    // student as ActorId, so an actor-only filter hid them from the teacher.
    var q = db.AuditEvents.AsNoTracking()
        .Where(x => x.ActorId == teacherId
            || (x.EntityType == "Submission" && x.EntityId != null && submissionIdStrings.Contains(x.EntityId)))
        .OrderByDescending(x => x.AtUtc);
    var total = await q.CountAsync();
    return Results.Ok(new { items = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(), page, pageSize, total });
}).RequireAuthorization().RequireRateLimiting("api");

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
  var restriction = await ExamAccessRestriction(db, sub.ExamId, uid);
  if (restriction is not null) return Results.Json(new { message = restriction }, statusCode: StatusCodes.Status403Forbidden);
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
  if (role == "Teacher") { ExamGrader.EnsureAnswers(sub, qs); await db.SaveChangesAsync(); }
  var elapsed = (int)((sub.SubmittedAtUtc ?? DateTime.UtcNow) - sub.StartedAtUtc).TotalSeconds;
  var resultsAvailable = ResultsVisible(sub.Exam!, sub, DateTime.UtcNow);
  // Results are released according to the exam mode and only after automatic grading
  // (and any required manual essay grading) is complete.
  var canSeeResults = role == "Teacher" || resultsAvailable;
  return Results.Ok(new { sub.Id, sub.ExamId, ExamTitle = sub.Exam!.Title, TeacherName = await db.Users.Where(u => u.Id == sub.Exam.TeacherId).Select(u => u.FullName).FirstOrDefaultAsync(), StudentName = sub.Student!.FullName, sub.Status, ResultsAvailable = resultsAvailable, Score = canSeeResults ? sub.Score : (decimal?)null, MaxScore = canSeeResults ? sub.MaxScore : (decimal?)null, sub.StartedAtUtc, sub.SubmittedAtUtc, DurationSeconds = elapsed, sub.AttemptNumber, sub.FocusLossCount, sub.CopyAttemptCount, sub.ScreenshotAttemptCount, Answers = qs.Select(q => { var a = sub.Answers.FirstOrDefault(x => x.QuestionId == q.Id); return new { Id = a?.Id, QuestionId = q.Id, q.Text, q.Type, Options = JsonSerializer.Deserialize<string[]>(q.OptionsJson), CorrectAnswer = canSeeResults ? q.CorrectAnswer : null, q.Points, StudentAnswer = a?.AnswerText ?? "", AwardedPoints = canSeeResults ? a?.AwardedPoints : null, TeacherFeedback = canSeeResults ? a?.TeacherFeedback : null }; }) });
}).RequireAuthorization();

app.Run();

public sealed record AttemptSummaryDto(
    int Allowed,
    int Used,
    int Completed,
    int Remaining,
    int InProgress,
    int Abandoned,
    int ExceptionUsed,
    int Total);

/// <summary>
/// The API contract uses UTC for every date field. SQL Server returns datetime2 values
/// with DateTimeKind.Unspecified, which would otherwise be serialized without a trailing
/// "Z" and re-interpreted as local time by browsers (causing an Egypt UTC+2/+3 shift).
/// </summary>
public sealed class UtcDateTimeJsonConverter : System.Text.Json.Serialization.JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetDateTime();
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        writer.WriteStringValue(utc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }
}
