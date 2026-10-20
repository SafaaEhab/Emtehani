namespace Emtehani.Application.Contracts;

public record RegisterRequest(string FullName, string Email, string Password, string Role = "Student");
public record LoginRequest(string Email, string Password);
public record AuthResponse(string Token, string FullName, string Email, string Role, DateTime ExpiresAtUtc);
public record QuestionInput(string Text, string Type, string[]? Options, string? CorrectAnswer, decimal Points = 1);
public record ExamCreateRequest(string Title, string? Description, int DurationMinutes, List<QuestionInput> Questions, int MaxAttempts = 1, bool AllowResumeAfterFocusLoss = true);
public record AnswerInput(Guid QuestionId, string AnswerText);
public record SubmitRequest(List<AnswerInput> Answers, bool FocusLossDetected = false);
public record EssayGradeRequest(decimal AwardedPoints, string? Feedback);
public record ResendVerificationRequest(string Email);
public record SubmissionEventRequest(string Type);