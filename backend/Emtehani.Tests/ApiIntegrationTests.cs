using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Emtehani.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Emtehani.Tests;

public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"emtehani-tests-{Guid.NewGuid():N}.db");
    internal const string TestJwtKey = "emtehani-test-jwt-key-for-local-development-only-32ch";

    protected virtual string EnvironmentName => "Development";

    protected virtual void ConfigureTestSettings(IWebHostBuilder builder)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        ConfigureTestSettings(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FrontendOrigin"] = "http://localhost:5173",
                ["PublicApiUrl"] = "http://localhost:5000",
                ["Auth:AutoConfirmWithoutSmtp"] = "true",
                ["Email:SmtpHost"] = "",
                ["Email:Username"] = "",
                ["Email:Password"] = "",
                ["Jwt:Key"] = TestJwtKey,
                ["RateLimiting:ApiPermitLimit"] = "10000",
                ["RateLimiting:AuthPermitLimit"] = "10000"
            });
            ConfigureTestConfiguration(config);
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_databasePath};Default Timeout=30"));
            services.Replace(ServiceDescriptor.Singleton<Emtehani.Application.Abstractions.ITokenService>(
                new FixedTokenService(TestJwtKey)));
            // Override JWT validation to use the test key
            var jwtKeyBytes = Encoding.UTF8.GetBytes(TestJwtKey);
            services.Configure<JwtBearerOptions>(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = "Emtehani",
                    ValidateAudience = true,
                    ValidAudience = "Emtehani.Client",
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(jwtKeyBytes),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });
        });
    }

    protected virtual void ConfigureTestConfiguration(IConfigurationBuilder config)
    {
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        SqliteConnection.ClearAllPools();
        try { File.Delete(_databasePath); } catch (IOException) { }
    }
}

public sealed class FixedTokenService(string jwtKey) : Emtehani.Application.Abstractions.ITokenService
{
    public (string Token, DateTime ExpiresAtUtc) Create(Emtehani.Domain.Entities.AppUser user)
    {
        var expiry = DateTime.UtcNow.AddHours(8);
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.FullName),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role)
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = "Emtehani",
            Audience = "Emtehani.Client",
            Expires = expiry,
            SigningCredentials = creds
        });
        return (handler.WriteToken(token), expiry);
    }
}

public sealed class ProductionStartupFactory : ApiFactory
{
    protected override string EnvironmentName => "Production";

    protected override void ConfigureTestSettings(IWebHostBuilder builder)
    {
        builder.UseSetting("FrontendOrigin", "https://emtehani.example.test");
        builder.UseSetting("PublicApiUrl", "https://api.emtehani.example.test");
        builder.UseSetting("Jwt:Key", "cHJvZHVjdGlvbi1zdGFydHVwLXRlc3Qta2V5LTAxMjM0NTY3ODlhYmNkZWY=");
        builder.UseSetting("Auth:AutoConfirmWithoutSmtp", "false");
    }
}

public sealed class InviteCodeFactory : ApiFactory
{
    internal const string InviteCode = "TEST-INVITE-2026";

    protected override void ConfigureTestConfiguration(IConfigurationBuilder config)
    {
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:TeacherInviteCode"] = InviteCode
        });
    }
}

public sealed class RegistrationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task TeacherRegistration_InDevelopmentWithoutConfiguredInviteCode_IsAllowed()
    {
        var client = factory.CreateClient();
        var email = $"teacher-open-{Guid.NewGuid():N}@example.test";
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            fullName = "مدرس تسجيل مفتوح",
            email,
            password = "Passw0rd!",
            role = "Teacher"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Passw0rd!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var auth = JsonNode.Parse(await login.Content.ReadAsStringAsync());
        Assert.Equal("Teacher", auth!["role"]!.GetValue<string>());
        Assert.True(string.IsNullOrWhiteSpace(auth["studentCode"]?.GetValue<string>()));
    }

    [Fact]
    public async Task TeacherRegistration_WithConfiguredInviteCode_RequiresAnExactValidCode()
    {
        using var inviteFactory = new InviteCodeFactory();
        var client = inviteFactory.CreateClient();
        var emailMissing = $"teacher-missing-{Guid.NewGuid():N}@example.test";
        var missing = await client.PostAsJsonAsync("/api/auth/register", new
        {
            fullName = "مدرس بلا رمز",
            email = emailMissing,
            password = "Passw0rd!",
            role = "Teacher"
        });
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);

        var emailInvalid = $"teacher-invalid-{Guid.NewGuid():N}@example.test";
        var invalid = await client.PostAsJsonAsync("/api/auth/register", new
        {
            fullName = "مدرس برمز خاطئ",
            email = emailInvalid,
            password = "Passw0rd!",
            role = "Teacher",
            inviteCode = "WRONG-CODE"
        });
        Assert.Equal(HttpStatusCode.Forbidden, invalid.StatusCode);

        var emailValid = $"teacher-valid-{Guid.NewGuid():N}@example.test";
        var valid = await client.PostAsJsonAsync("/api/auth/register", new
        {
            fullName = "مدرس برمز صحيح",
            email = emailValid,
            password = "Passw0rd!",
            role = "Teacher",
            inviteCode = InviteCodeFactory.InviteCode
        });
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = emailValid, password = "Passw0rd!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var auth = JsonNode.Parse(await login.Content.ReadAsStringAsync());
        Assert.Equal("Teacher", auth!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProductionTeacherRegistration_WithoutConfiguredInviteCode_IsForbidden()
    {
        using var productionFactory = new ProductionStartupFactory();
        var client = productionFactory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            fullName = "مدرس إنتاج",
            email = $"teacher-production-{Guid.NewGuid():N}@example.test",
            password = "Passw0rd!",
            role = "Teacher"
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ProductionStartup_AllowsHealthCheck()
    {
        using var productionFactory = new ProductionStartupFactory();
        var client = productionFactory.CreateClient();
        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}

public sealed class ApiIntegrationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string Email(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.test";

    private static async Task<JsonNode> ReadJson(HttpResponseMessage response)
        => JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? throw new InvalidOperationException("Empty response.");

    private static Guid GuidValue(JsonNode? node) => Guid.Parse(node!.GetValue<string>());

    private async Task<(HttpClient Client, JsonNode Auth)> RegisterAndLogin(string email, string role, string name)
    {
        var client = factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/auth/register", new { fullName = name, email, password = "Passw0rd!", role });
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Passw0rd!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var auth = await ReadJson(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth["token"]!.GetValue<string>());
        return (client, auth);
    }

    private async Task<Guid> CreatePublishedExam(HttpClient teacher)
    {
        var create = await teacher.PostAsJsonAsync("/api/exams", new
        {
            title = "اختبار تكاملي",
            description = "وصف",
            durationMinutes = 30,
            maxAttempts = 1,
            allowResumeAfterFocusLoss = true,
            questions = new object[]
            {
                new { text = "٢ + ٢", type = "MultipleChoice", options = new[] { "3", "4" }, correctAnswer = "4", points = 2 },
                new { text = "السماء زرقاء", type = "TrueFalse", options = Array.Empty<string>(), correctAnswer = "صح", points = 1 }
            }
        });
        Assert.True(create.StatusCode == HttpStatusCode.Created,
            $"{(int)create.StatusCode} {create.StatusCode}: {await create.Content.ReadAsStringAsync()} | {string.Join(",", create.Headers.WwwAuthenticate)}");
        var exam = await ReadJson(create);
        Assert.NotNull(exam["joinCode"]);
        var examId = GuidValue(exam["id"]);
        var publish = await teacher.PutAsync($"/api/exams/{examId}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        return examId;
    }

    private static async Task OpenExam(HttpClient teacher, Guid examId)
    {
        var open = await teacher.PutAsync($"/api/exams/{examId}/state", JsonContent.Create(new { action = "open" }));
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
    }

    private static string StudentCode(JsonNode auth) => auth["studentCode"]!.GetValue<string>();

    [Fact]
    public async Task Registration_AssignsUniqueSixDigitStudentCodeToEachStudent()
    {
        var (_, firstAuth) = await RegisterAndLogin(Email("code-one"), "Student", "طالب كود أول");
        var (_, secondAuth) = await RegisterAndLogin(Email("code-two"), "Student", "طالب كود ثان");
        var (_, teacherAuth) = await RegisterAndLogin(Email("code-teacher"), "Teacher", "مدرس بلا كود");

        var first = StudentCode(firstAuth);
        var second = StudentCode(secondAuth);
        Assert.Matches("^[0-9]{6}$", first);
        Assert.Matches("^[0-9]{6}$", second);
        Assert.NotEqual(first, second);
        Assert.True(string.IsNullOrWhiteSpace(teacherAuth["studentCode"]?.GetValue<string>()));
    }

    [Fact]
    public async Task Student_CanJoinStartSubmitAndResubmitIdempotently()
    {
        var teacherEmail = Email("teacher");
        var studentEmail = Email("student");
        var (teacher, _) = await RegisterAndLogin(teacherEmail, "Teacher", "مدرس تكاملي");
        var (student, studentAuth) = await RegisterAndLogin(studentEmail, "Student", "طالب تكاملي");
        var examId = await CreatePublishedExam(teacher);
        var studentCode = StudentCode(studentAuth);

        var joined = await student.PostAsJsonAsync("/api/exams/enter-code", new { code = studentCode });
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        var joinedResult = await ReadJson(joined);
        Assert.Equal(examId, GuidValue(joinedResult["examId"]));
        Assert.Equal("Waiting", joinedResult["participantStatus"]!.GetValue<string>());

        await OpenExam(teacher, examId);
        var start = await student.PostAsync($"/api/exams/{examId}/start", null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var started = await ReadJson(start);
        var submissionId = GuidValue(started["id"]);

        var paper = await student.GetFromJsonAsync<JsonNode>($"/api/exams/{examId}/paper");
        var questions = paper!["questions"]!.AsArray();
        Assert.Equal(2, questions.Count);
        var answers = questions.Select(question => new
        {
            questionId = GuidValue(question!["id"]),
            answerText = question["type"]!.GetValue<string>() == "MultipleChoice" ? "4" : "صح"
        }).ToArray();

        var save = await student.PutAsJsonAsync($"/api/submissions/{submissionId}/answers", new { answers, focusLossDetected = false });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var submit = await student.PostAsJsonAsync($"/api/submissions/{submissionId}/submit", new { answers, focusLossDetected = false });
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        var submitted = await ReadJson(submit);
        Assert.Equal(3, submitted["score"]!.GetValue<double>());
        Assert.Equal("Reviewed", submitted["status"]!.GetValue<string>());

        var again = await student.PostAsJsonAsync($"/api/submissions/{submissionId}/submit", new { answers, focusLossDetected = false });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(submissionId, GuidValue((await ReadJson(again))["id"]));

        var review = await student.GetFromJsonAsync<JsonNode>($"/api/submissions/{submissionId}/review");
        Assert.Equal(3, review!["score"]!.GetValue<double>());
        Assert.Equal("Reviewed", review["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task AutosaveAfterSubmit_IsRejectedAndDoesNotChangePersistedAnswers()
    {
        var (teacher, _) = await RegisterAndLogin(Email("late-save-teacher"), "Teacher", "مدرس حفظ متأخر");
        var (student, studentAuth) = await RegisterAndLogin(Email("late-save-student"), "Student", "طالب حفظ متأخر");
        var examId = await CreatePublishedExam(teacher);
        var studentCode = StudentCode(studentAuth);

        var joined = await student.PostAsJsonAsync("/api/exams/enter-code", new { code = studentCode });
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        await OpenExam(teacher, examId);

        var start = await student.PostAsync($"/api/exams/{examId}/start", null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var submissionId = GuidValue((await ReadJson(start))["id"]);

        var paper = await student.GetFromJsonAsync<JsonNode>($"/api/exams/{examId}/paper");
        var questions = paper!["questions"]!.AsArray();
        var answers = questions.Select(question => new
        {
            questionId = GuidValue(question!["id"]),
            answerText = question["type"]!.GetValue<string>() == "MultipleChoice" ? "4" : "صح"
        }).ToArray();

        var submit = await student.PostAsJsonAsync($"/api/submissions/{submissionId}/submit", new { answers, focusLossDetected = false });
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        var lateAnswers = questions.Select(question => new
        {
            questionId = GuidValue(question!["id"]),
            answerText = question["type"]!.GetValue<string>() == "MultipleChoice" ? "3" : "خطأ"
        }).ToArray();
        var lateSave = await student.PutAsJsonAsync($"/api/submissions/{submissionId}/answers", new { answers = lateAnswers, focusLossDetected = false });
        Assert.Equal(HttpStatusCode.Conflict, lateSave.StatusCode);

        var review = await student.GetFromJsonAsync<JsonNode>($"/api/submissions/{submissionId}/review");
        Assert.Equal(3, review!["score"]!.GetValue<double>());
        Assert.Equal("4", review["answers"]![0]!["studentAnswer"]!.GetValue<string>());
        Assert.Equal("صح", review["answers"]![1]!["studentAnswer"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnauthorizedUsers_CannotReviewGradeOrExportAnotherUsersSubmission()
    {
        var (teacher, _) = await RegisterAndLogin(Email("access-teacher"), "Teacher", "مدرس الصلاحيات");
        var (student, studentAuth) = await RegisterAndLogin(Email("access-student"), "Student", "طالب الصلاحيات");
        var (otherTeacher, _) = await RegisterAndLogin(Email("access-other-teacher"), "Teacher", "مدرس آخر");
        var (otherStudent, _) = await RegisterAndLogin(Email("access-other-student"), "Student", "طالب آخر");
        var examId = await CreatePublishedExam(teacher);
        var studentCode = StudentCode(studentAuth);

        var joined = await student.PostAsJsonAsync("/api/exams/enter-code", new { code = studentCode });
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        await OpenExam(teacher, examId);

        var start = await student.PostAsync($"/api/exams/{examId}/start", null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var submissionId = GuidValue((await ReadJson(start))["id"]);

        var paper = await student.GetFromJsonAsync<JsonNode>($"/api/exams/{examId}/paper");
        var answers = paper!["questions"]!.AsArray().Select(question => new
        {
            questionId = GuidValue(question!["id"]),
            answerText = question["type"]!.GetValue<string>() == "MultipleChoice" ? "4" : "صح"
        }).ToArray();
        var submit = await student.PostAsJsonAsync($"/api/submissions/{submissionId}/submit", new { answers, focusLossDetected = false });
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        var studentReview = await student.GetFromJsonAsync<JsonNode>($"/api/submissions/{submissionId}/review");
        var answerId = GuidValue(studentReview!["answers"]![0]!["id"]);

        Assert.Equal(HttpStatusCode.Forbidden, (await otherTeacher.GetAsync($"/api/submissions/{submissionId}/review")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherStudent.GetAsync($"/api/submissions/{submissionId}/review")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/submissions/{submissionId}/review")).StatusCode);

        var otherTeacherGrade = await otherTeacher.PutAsJsonAsync($"/api/submissions/{submissionId}/answers/{answerId}/grade",
            new { awardedPoints = 1, feedback = "محاولة غير مصرح بها" });
        Assert.Equal(HttpStatusCode.Forbidden, otherTeacherGrade.StatusCode);
        var otherStudentGrade = await otherStudent.PutAsJsonAsync($"/api/submissions/{submissionId}/answers/{answerId}/grade",
            new { awardedPoints = 1, feedback = "محاولة غير مصرح بها" });
        Assert.Equal(HttpStatusCode.Forbidden, otherStudentGrade.StatusCode);

        Assert.True((await otherTeacher.GetAsync($"/api/exams/{examId}/export")).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
        Assert.True((await otherTeacher.GetAsync($"/api/exams/{examId}/participants/export")).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync($"/api/exams/{examId}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync($"/api/exams/{examId}/participants/export")).StatusCode);
    }

    [Fact]
    public async Task EssayReview_ReturnsSubmissionAnswerIdThatCanBeGraded()
    {
        var teacherEmail = Email("essay-teacher");
        var studentEmail = Email("essay-student");
        var (teacher, _) = await RegisterAndLogin(teacherEmail, "Teacher", "مدرس مقالي");
        var (student, studentAuth) = await RegisterAndLogin(studentEmail, "Student", "طالب مقالي");

        var create = await teacher.PostAsJsonAsync("/api/exams", new
        {
            title = "اختبار مقالي",
            description = "وصف",
            durationMinutes = 30,
            maxAttempts = 1,
            allowResumeAfterFocusLoss = true,
            questions = new object[]
            {
                new { text = "اشرح الفكرة", type = "Essay", options = Array.Empty<string>(), correctAnswer = (string?)null, points = 5 }
            }
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var exam = await ReadJson(create);
        var examId = GuidValue(exam["id"]);
        var publish = await teacher.PutAsync($"/api/exams/{examId}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        var studentCode = StudentCode(studentAuth);
        var joined = await student.PostAsJsonAsync("/api/exams/enter-code", new { code = studentCode });
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        await OpenExam(teacher, examId);

        var start = await student.PostAsync($"/api/exams/{examId}/start", null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var submissionId = GuidValue((await ReadJson(start))["id"]);

        var paper = await student.GetFromJsonAsync<JsonNode>($"/api/exams/{examId}/paper");
        var question = paper!["questions"]!.AsArray().Single()!;
        var questionId = GuidValue(question["id"]);
        var answers = new[] { new { questionId, answerText = "إجابة الطالب" } };

        var submit = await student.PostAsJsonAsync($"/api/submissions/{submissionId}/submit", new { answers, focusLossDetected = false });
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        var submitted = await ReadJson(submit);
        Assert.Equal("NeedsReview", submitted["status"]!.GetValue<string>());

        var reviewRows = await teacher.GetFromJsonAsync<JsonNode>($"/api/exams/{examId}/review");
        var answer = reviewRows!.AsArray().Single()!["answers"]!.AsArray().Single()!;
        var answerId = GuidValue(answer["id"]);
        Assert.NotEqual(Guid.Empty, answerId);
        Assert.NotEqual(questionId, answerId);
        Assert.Equal(questionId, GuidValue(answer["questionId"]));

        var grade = await teacher.PutAsJsonAsync($"/api/submissions/{submissionId}/answers/{answerId}/grade",
            new { awardedPoints = 4, feedback = "جيد" });
        Assert.Equal(HttpStatusCode.OK, grade.StatusCode);
        var graded = await ReadJson(grade);
        Assert.Equal("Reviewed", graded["status"]!.GetValue<string>());
        Assert.Equal(4, graded["score"]!.GetValue<double>());

        var gradeByQuestionId = await teacher.PutAsJsonAsync($"/api/submissions/{submissionId}/answers/{questionId}/grade",
            new { awardedPoints = 4, feedback = "خطأ" });
        Assert.Equal(HttpStatusCode.NotFound, gradeByQuestionId.StatusCode);

        var studentReview = await student.GetFromJsonAsync<JsonNode>($"/api/submissions/{submissionId}/review");
        Assert.Equal(4, studentReview!["score"]!.GetValue<double>());
        Assert.Equal("Reviewed", studentReview["status"]!.GetValue<string>());
        Assert.Equal(answerId, GuidValue(studentReview["answers"]![0]!["id"]));
    }

    [Fact]
    public async Task Teacher_CanCloseExamAndFinalizeInProgressSubmission()
    {
        var teacherEmail = Email("close-teacher");
        var studentEmail = Email("close-student");
        var (teacher, _) = await RegisterAndLogin(teacherEmail, "Teacher", "مدرس إغلاق");
        var (student, studentAuth) = await RegisterAndLogin(studentEmail, "Student", "طالب إغلاق");
        var examId = await CreatePublishedExam(teacher);
        var studentCode = StudentCode(studentAuth);
        var joined = await student.PostAsJsonAsync("/api/exams/enter-code", new { code = studentCode });
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        await OpenExam(teacher, examId);

        var start = await student.PostAsync($"/api/exams/{examId}/start", null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var submissionId = GuidValue((await ReadJson(start))["id"]);
        var paper = await student.GetFromJsonAsync<JsonNode>($"/api/exams/{examId}/paper");
        var answers = paper!["questions"]!.AsArray().Select(question => new
        {
            questionId = GuidValue(question!["id"]),
            answerText = question["type"]!.GetValue<string>() == "MultipleChoice" ? "4" : "صح"
        }).ToArray();
        var save = await student.PutAsJsonAsync($"/api/submissions/{submissionId}/answers", new { answers, focusLossDetected = false });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var close = await teacher.PutAsJsonAsync($"/api/exams/{examId}/state", new { action = "close" });
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        Assert.Equal("Finished", (await ReadJson(close))["status"]!.GetValue<string>());

        var results = await student.GetFromJsonAsync<JsonNode>("/api/results/mine");
        var result = results!.AsArray().Single(x => GuidValue(x!["examId"]) == examId);
        Assert.Equal("Reviewed", result!["status"]!.GetValue<string>());
        Assert.Equal(3, result["score"]!.GetValue<double>());
    }

    [Fact]
    public async Task Student_CannotStartWithoutEnteringPersonalCode()
    {
        var teacherEmail = Email("teacher");
        var studentEmail = Email("student");
        var (teacher, _) = await RegisterAndLogin(teacherEmail, "Teacher", "مدرس");
        var (student, _) = await RegisterAndLogin(studentEmail, "Student", "طالب");
        var examId = await CreatePublishedExam(teacher);
        await OpenExam(teacher, examId);

        var start = await student.PostAsync($"/api/exams/{examId}/start", null);
        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);

        var close = await teacher.PutAsJsonAsync($"/api/exams/{examId}/state", new { action = "close" });
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        Assert.Equal("Finished", (await ReadJson(close))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Student_OwnCode_JoinsCurrentPublishedExam_AndTeacherSeesCodeWithName()
    {
        var (teacher, teacherAuth) = await RegisterAndLogin(Email("roster-teacher"), "Teacher", "مدرس الحضور");
        var (student, studentAuth) = await RegisterAndLogin(Email("roster-student"), "Student", "سارة أحمد رستم");
        var examId = await CreatePublishedExam(teacher);
        var studentCode = StudentCode(studentAuth);

        var beforeExam = await student.PostAsJsonAsync("/api/exams/enter-code", new { code = "999999" });
        Assert.Equal(HttpStatusCode.NotFound, beforeExam.StatusCode);

        var joined = await student.PostAsJsonAsync("/api/exams/enter-code", new { code = studentCode });
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        var joinResult = await ReadJson(joined);
        Assert.Equal(examId, GuidValue(joinResult["examId"]));
        Assert.Equal("Waiting", joinResult["participantStatus"]!.GetValue<string>());
        Assert.Equal(studentCode, joinResult["studentCode"]!.GetValue<string>());

        var live = await teacher.GetFromJsonAsync<JsonNode>($"/api/exams/{examId}/live");
        var item = live!["items"]!.AsArray().Single();
        Assert.Equal("سارة أحمد رستم", item!["studentName"]!.GetValue<string>());
        Assert.Equal(studentCode, item["code"]!.GetValue<string>());
        Assert.Equal("Waiting", item["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Student_UsingPermanentCode_JoinsExistingTeacherGeneratedParticipantWithoutDuplication()
    {
        var (teacher, _) = await RegisterAndLogin(Email("generated-teacher"), "Teacher", "مدرس الأكواد المولدة");
        var (student, studentAuth) = await RegisterAndLogin(Email("generated-student"), "Student", "طالب الكود المولد");
        var examId = await CreatePublishedExam(teacher);
        var me = await student.GetFromJsonAsync<JsonNode>("/api/auth/me");
        var studentId = GuidValue(me!["id"]);
        var permanentCode = StudentCode(studentAuth);

        var generate = await teacher.PostAsJsonAsync($"/api/exams/{examId}/participants/generate",
            new { studentIds = new[] { studentId } });
        Assert.Equal(HttpStatusCode.OK, generate.StatusCode);
        var generated = await ReadJson(generate);
        Assert.Equal(1, generated["generated"]!.GetValue<int>());

        var participants = await teacher.GetFromJsonAsync<JsonNode>($"/api/exams/{examId}/participants");
        var participant = participants!["items"]!.AsArray().Single()!;
        var teacherGeneratedCode = participant["code"]!.GetValue<string>();
        Assert.NotEqual(permanentCode, teacherGeneratedCode);

        var joined = await student.PostAsJsonAsync("/api/exams/enter-code", new { code = permanentCode });
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        var joinedResult = await ReadJson(joined);
        Assert.Equal(examId, GuidValue(joinedResult["examId"]));
        Assert.Equal("Waiting", joinedResult["participantStatus"]!.GetValue<string>());

        var afterJoin = await teacher.GetFromJsonAsync<JsonNode>($"/api/exams/{examId}/participants");
        Assert.Equal(1, afterJoin!["total"]!.GetValue<int>());
        Assert.Equal(teacherGeneratedCode, afterJoin["items"]!.AsArray().Single()!["code"]!.GetValue<string>());

        var joinedWithAssignedCode = await student.PostAsJsonAsync("/api/exams/enter-code", new { code = teacherGeneratedCode });
        Assert.Equal(HttpStatusCode.OK, joinedWithAssignedCode.StatusCode);
        Assert.Equal(examId, GuidValue((await ReadJson(joinedWithAssignedCode))["examId"]));
    }

    [Fact]
    public async Task Student_CannotEnterWithAnotherStudentsCode()
    {
        var (teacher, _) = await RegisterAndLogin(Email("owned-teacher"), "Teacher", "مدرس");
        var (first, firstAuth) = await RegisterAndLogin(Email("owned-first"), "Student", "الطالب الأول");
        var (_, secondAuth) = await RegisterAndLogin(Email("owned-second"), "Student", "الطالب الثاني");
        await CreatePublishedExam(teacher);

        var otherCode = StudentCode(secondAuth);
        var firstCode = StudentCode(firstAuth);
        Assert.NotEqual(firstCode, otherCode);

        var rejected = await first.PostAsJsonAsync("/api/exams/enter-code", new { code = otherCode });
        Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);

        var own = await first.PostAsJsonAsync("/api/exams/enter-code", new { code = firstCode });
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var joinedExamId = GuidValue((await ReadJson(own))["examId"]);
        Assert.NotEqual(Guid.Empty, joinedExamId);
    }

    [Fact]
    public async Task EnterCode_BeforeAnyPublishedExam_ReturnsConflict()
    {
        using var freshFactory = new ApiFactory();
        var client = freshFactory.CreateClient();
        var email = Email("idle-student");
        var register = await client.PostAsJsonAsync("/api/auth/register", new { fullName = "طالب بدون امتحان", email, password = "Passw0rd!", role = "Student" });
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Passw0rd!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var auth = await ReadJson(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth["token"]!.GetValue<string>());

        var response = await client.PostAsJsonAsync("/api/exams/enter-code", new { code = StudentCode(auth) });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Contains("امتحان", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task OtherTeacher_CannotManageTheExam()
    {
        var (owner, _) = await RegisterAndLogin(Email("owner"), "Teacher", "المدرس المالك");
        var (other, _) = await RegisterAndLogin(Email("other"), "Teacher", "مدرس آخر");
        var examId = await CreatePublishedExam(owner);

        var detail = await other.GetAsync($"/api/exams/{examId}");
        Assert.True(detail.StatusCode == HttpStatusCode.NotFound || detail.StatusCode == HttpStatusCode.Forbidden,
            $"Expected NotFound or Forbidden, got {detail.StatusCode}");
        var publish = await other.PutAsync($"/api/exams/{examId}/publish", null);
        Assert.True(publish.StatusCode == HttpStatusCode.NotFound || publish.StatusCode == HttpStatusCode.Forbidden,
            $"Expected NotFound or Forbidden, got {publish.StatusCode}");
    }

    [Fact]
    public async Task ExamCodeAttempts_AreRateLimitedPerAccount()
    {
        var (student, _) = await RegisterAndLogin(Email("ratelimit"), "Student", "Rate Limit Student");
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 16; attempt++)
            response = await student.PostAsJsonAsync("/api/exams/enter-code", new { code = "000000" });

        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.TooManyRequests, response!.StatusCode);
        Assert.Equal("60", response.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task AdminReset_DisabledInProductionByDefault()
    {
        using var productionFactory = new ProductionStartupFactory();
        var client = productionFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "dummy-token");
        var response = await client.PostAsync("/api/admin/reset", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdminReset_RequiresAdminRole()
    {
        // In Development, admin reset is enabled but requires proper auth.
        // This test verifies that a non-admin role gets forbidden.
        var (teacher, teacherAuth) = await RegisterAndLogin(Email("admin-reset-teacher"), "Teacher", "مدرس اختبار");
        var client = teacher;
        var response = await client.PostAsync("/api/admin/reset", null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task StudentCodeGeneration_HandlesCollisions()
    {
        // Register multiple students and verify all have unique 6-digit codes.
        var client = factory.CreateClient();
        var codes = new HashSet<string>();
        for (var i = 0; i < 10; i++)
        {
            var email = Email($"collision-{i}");
            var register = await client.PostAsJsonAsync("/api/auth/register", new
            {
                fullName = $"طالب ت撞{i}",
                email,
                password = "Passw0rd!",
                role = "Student"
            });
            Assert.Equal(HttpStatusCode.OK, register.StatusCode);
            var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Passw0rd!" });
            var auth = await ReadJson(login);
            var code = auth["studentCode"]!.GetValue<string>();
            Assert.Matches("^[0-9]{6}$", code);
            Assert.True(codes.Add(code), $"Duplicate code detected: {code}");
        }
    }

    [Fact]
    public async Task ParticipantViews_Pagination_Works()
    {
        var (teacher, _) = await RegisterAndLogin(Email("pag-teacher"), "Teacher", "مدرس تجزئة");
        var examId = await CreatePublishedExam(teacher);

        // Register multiple students and have them join.
        for (var i = 0; i < 5; i++)
        {
            var (student, studentAuth) = await RegisterAndLogin(Email($"pag-student-{i}"), "Student", $"طالب تجزئة {i}");
            var code = StudentCode(studentAuth);
            var joined = await student.PostAsJsonAsync("/api/exams/enter-code", new { code });
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        }

        // Test pagination.
        var participantsPage1 = await teacher.GetFromJsonAsync<JsonNode>($"/api/exams/{examId}/participants?page=1&pageSize=2");
        Assert.NotNull(participantsPage1);
        var items = participantsPage1["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Equal(5, participantsPage1["total"]!.GetValue<int>());
        Assert.Equal(1, participantsPage1["page"]!.GetValue<int>());
        Assert.Equal(2, participantsPage1["pageSize"]!.GetValue<int>());

        var participantsPage2 = await teacher.GetFromJsonAsync<JsonNode>($"/api/exams/{examId}/participants?page=2&pageSize=2");
        items = participantsPage2!["items"]!.AsArray();
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task ConcurrentAutosaveVsSubmit_CoherenceMaintained()
    {
        var (teacher, _) = await RegisterAndLogin(Email("concurrent"), "Teacher", "مدرس تزامن");
        var (student, studentAuth) = await RegisterAndLogin(Email("concurrent-student"), "Student", "طالب تزامن");
        var examId = await CreatePublishedExam(teacher);
        var code = StudentCode(studentAuth);

        await student.PostAsJsonAsync("/api/exams/enter-code", new { code });
        await OpenExam(teacher, examId);

        // Start exam.
        var start = await student.PostAsync($"/api/exams/{examId}/start", null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var started = await ReadJson(start);
        var submissionId = GuidValue(started["id"]);

        // Simulate concurrent autosave and submit.
        var answers = new[] { new { questionId = Guid.NewGuid(), answerText = "4" } };
        var saveTask = student.PutAsJsonAsync($"/api/submissions/{submissionId}/answers", new { answers, focusLossDetected = false });
        var submitTask = student.PostAsJsonAsync($"/api/submissions/{submissionId}/submit", new { answers, focusLossDetected = false });
        await Task.WhenAll(saveTask, submitTask);
        var saveResult = await saveTask;
        var submitResult = await submitTask;

        // Either save succeeds (200) or conflicts (409), submit must return 200.
        Assert.Contains(saveResult.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        Assert.Equal(HttpStatusCode.OK, submitResult.StatusCode);

        // Verify final state is consistent.
        var review = await student.GetFromJsonAsync<JsonNode>($"/api/submissions/{submissionId}/review");
        Assert.NotNull(review);
        Assert.Contains(review["status"]!.GetValue<string>(), new[] { "Reviewed", "NeedsReview" });
    }

    [Fact]
    public async Task FinalizeExpired_IdempotentOnRepeatedCalls()
    {
        var (teacher, _) = await RegisterAndLogin(Email("finalize"), "Teacher", "مدرس إنهاء");
        var (student, studentAuth) = await RegisterAndLogin(Email("finalize-student"), "Student", "طالب إنهاء");
        var examId = await CreatePublishedExam(teacher);
        var code = StudentCode(studentAuth);

        var entered = await student.PostAsJsonAsync("/api/exams/enter-code", new { code });
        Assert.Equal(HttpStatusCode.OK, entered.StatusCode);
        await OpenExam(teacher, examId);

        // Start and immediately close (bypassing time).
        var start = await student.PostAsync($"/api/exams/{examId}/start", null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var close = await teacher.PutAsJsonAsync($"/api/exams/{examId}/state", new { action = "close" });
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);

        // Call finalization multiple times - should not error.
        await teacher.GetFromJsonAsync<JsonNode>($"/api/exams/{examId}/live");
        await teacher.GetFromJsonAsync<JsonNode>($"/api/exams/{examId}/live");

        // Verify submission is still in correct state.
        var results = await teacher.GetFromJsonAsync<JsonNode>($"/api/exams/{examId}/results");
        Assert.NotNull(results);
        Assert.Equal(1, results["total"]!.GetValue<int>());
    }
}
