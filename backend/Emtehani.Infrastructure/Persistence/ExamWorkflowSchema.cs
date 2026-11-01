using Microsoft.EntityFrameworkCore;

namespace Emtehani.Infrastructure.Persistence;

public static class ExamWorkflowSchema
{
    public static async Task EnsureAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        if (db.Database.IsSqlServer())
        {
            await db.Database.ExecuteSqlRawAsync("""
                IF COL_LENGTH('dbo.Exams', 'Status') IS NULL
                    ALTER TABLE [Exams] ADD [Status] nvarchar(24) NOT NULL CONSTRAINT [DF_Exams_Status] DEFAULT N'Draft';
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF COL_LENGTH('dbo.Exams', 'ScheduledAtUtc') IS NULL
                    ALTER TABLE [Exams] ADD [ScheduledAtUtc] datetime2 NULL;
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF COL_LENGTH('dbo.Exams', 'PausedAtUtc') IS NULL
                    ALTER TABLE [Exams] ADD [PausedAtUtc] datetime2 NULL;
                IF COL_LENGTH('dbo.Exams', 'AvailableFromUtc') IS NULL
                    ALTER TABLE [Exams] ADD [AvailableFromUtc] datetime2 NULL;
                IF COL_LENGTH('dbo.Exams', 'AvailableUntilUtc') IS NULL
                    ALTER TABLE [Exams] ADD [AvailableUntilUtc] datetime2 NULL;
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF OBJECT_ID(N'dbo.TeacherStudentRequests', N'U') IS NULL
                BEGIN
                    CREATE TABLE [TeacherStudentRequests] (
                        [Id] uniqueidentifier NOT NULL,
                        [TeacherId] uniqueidentifier NOT NULL,
                        [StudentId] uniqueidentifier NOT NULL,
                        [Status] nvarchar(24) NOT NULL CONSTRAINT [DF_TeacherStudentRequests_Status] DEFAULT N'Pending',
                        [CreatedAtUtc] datetime2 NOT NULL,
                        [DecidedAtUtc] datetime2 NULL,
                        CONSTRAINT [PK_TeacherStudentRequests] PRIMARY KEY ([Id]),
                        CONSTRAINT [FK_TeacherStudentRequests_Users_TeacherId] FOREIGN KEY ([TeacherId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE,
                        CONSTRAINT [FK_TeacherStudentRequests_Users_StudentId] FOREIGN KEY ([StudentId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
                    );
                    CREATE UNIQUE INDEX [IX_TeacherStudentRequests_TeacherId_StudentId] ON [TeacherStudentRequests] ([TeacherId], [StudentId]);
                    CREATE INDEX [IX_TeacherStudentRequests_StudentId] ON [TeacherStudentRequests] ([StudentId]);
                END;
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF COL_LENGTH('dbo.TeacherStudentRequests', 'UnlinkedAtUtc') IS NULL
                    ALTER TABLE [TeacherStudentRequests] ADD [UnlinkedAtUtc] datetime2 NULL;
                IF COL_LENGTH('dbo.TeacherStudentRequests', 'SuspendedAtUtc') IS NULL
                    ALTER TABLE [TeacherStudentRequests] ADD [SuspendedAtUtc] datetime2 NULL;
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF OBJECT_ID(N'dbo.TeacherStudentExamBans', N'U') IS NULL
                BEGIN
                    CREATE TABLE [TeacherStudentExamBans] (
                        [Id] uniqueidentifier NOT NULL,
                        [TeacherId] uniqueidentifier NOT NULL,
                        [StudentId] uniqueidentifier NOT NULL,
                        [ExamId] uniqueidentifier NOT NULL,
                        [CreatedAtUtc] datetime2 NOT NULL,
                        CONSTRAINT [PK_TeacherStudentExamBans] PRIMARY KEY ([Id]),
                        CONSTRAINT [FK_TeacherStudentExamBans_Users_TeacherId] FOREIGN KEY ([TeacherId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE,
                        CONSTRAINT [FK_TeacherStudentExamBans_Users_StudentId] FOREIGN KEY ([StudentId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION,
                        CONSTRAINT [FK_TeacherStudentExamBans_Exams_ExamId] FOREIGN KEY ([ExamId]) REFERENCES [Exams] ([Id]) ON DELETE CASCADE
                    );
                END;
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TeacherStudentExamBans_ExamId_StudentId' AND [object_id] = OBJECT_ID(N'dbo.TeacherStudentExamBans'))
                    CREATE UNIQUE INDEX [IX_TeacherStudentExamBans_ExamId_StudentId] ON [TeacherStudentExamBans] ([ExamId], [StudentId]);
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_TeacherStudentExamBans_TeacherId_StudentId' AND [object_id] = OBJECT_ID(N'dbo.TeacherStudentExamBans'))
                    CREATE INDEX [IX_TeacherStudentExamBans_TeacherId_StudentId] ON [TeacherStudentExamBans] ([TeacherId], [StudentId]);
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO [TeacherStudentRequests] ([Id], [TeacherId], [StudentId], [Status], [CreatedAtUtc], [DecidedAtUtc])
                SELECT NEWID(), x.[TeacherId], x.[StudentId], N'Accepted', GETUTCDATE(), GETUTCDATE()
                FROM (SELECT DISTINCT e.[TeacherId], p.[StudentId] FROM [ExamParticipants] p INNER JOIN [Exams] e ON e.[Id] = p.[ExamId]) x
                WHERE NOT EXISTS (SELECT 1 FROM [TeacherStudentRequests] r WHERE r.[TeacherId] = x.[TeacherId] AND r.[StudentId] = x.[StudentId]);
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF COL_LENGTH('dbo.Exams', 'JoinCode') IS NULL
                    ALTER TABLE [Exams] ADD [JoinCode] nvarchar(450) NOT NULL CONSTRAINT [DF_Exams_JoinCode] DEFAULT N'';
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF COL_LENGTH('dbo.Submissions', 'ExtraTimeSeconds') IS NULL
                    ALTER TABLE [Submissions] ADD [ExtraTimeSeconds] int NOT NULL CONSTRAINT [DF_Submissions_ExtraTimeSeconds] DEFAULT 0;
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF COL_LENGTH('dbo.Users', 'StudentCode') IS NULL
                    ALTER TABLE [Users] ADD [StudentCode] nvarchar(12) NOT NULL CONSTRAINT [DF_Users_StudentCode] DEFAULT N'';
                IF COL_LENGTH('dbo.Users', 'PasswordResetToken') IS NULL
                    ALTER TABLE [Users] ADD [PasswordResetToken] nvarchar(128) NULL;
                IF COL_LENGTH('dbo.Users', 'PasswordResetExpiresUtc') IS NULL
                    ALTER TABLE [Users] ADD [PasswordResetExpiresUtc] datetime2 NULL;
                IF COL_LENGTH('dbo.Users', 'Stage') IS NULL
                    ALTER TABLE [Users] ADD [Stage] nvarchar(32) NULL;
                IF COL_LENGTH('dbo.Users', 'Grade') IS NULL
                    ALTER TABLE [Users] ADD [Grade] nvarchar(64) NULL;
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF COL_LENGTH('dbo.Exams', 'Stage') IS NULL
                    ALTER TABLE [Exams] ADD [Stage] nvarchar(32) NULL;
                IF COL_LENGTH('dbo.Exams', 'Grade') IS NULL
                    ALTER TABLE [Exams] ADD [Grade] nvarchar(64) NULL;
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF COL_LENGTH('dbo.Exams', 'ResultsPublishedAtUtc') IS NULL
                BEGIN
                    ALTER TABLE [Exams] ADD [ResultsPublishedAtUtc] datetime2 NULL;
                    -- Exams finished before this column existed were already visible under the
                    -- old rules. Publish them once during the upgrade so legacy results do not
                    -- disappear. Newly finished exams stay unpublished until the teacher
                    -- explicitly publishes them.
                    EXEC(N'UPDATE [Exams] SET [ResultsPublishedAtUtc] = GETUTCDATE() WHERE [Status] = N''Finished'';');
                END;
                IF COL_LENGTH('dbo.Exams', 'ResultsReleaseMode') IS NULL
                    ALTER TABLE [Exams] ADD [ResultsReleaseMode] nvarchar(32) NULL;
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF COL_LENGTH('dbo.Users', 'RefreshTokenHash') IS NULL
                    ALTER TABLE [Users] ADD [RefreshTokenHash] nvarchar(64) NULL;
                IF COL_LENGTH('dbo.Users', 'RefreshTokenExpiresUtc') IS NULL
                    ALTER TABLE [Users] ADD [RefreshTokenExpiresUtc] datetime2 NULL;
                IF COL_LENGTH('dbo.Users', 'RefreshTokenCreatedAtUtc') IS NULL
                    ALTER TABLE [Users] ADD [RefreshTokenCreatedAtUtc] datetime2 NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_Users_RefreshTokenHash' AND [object_id] = OBJECT_ID(N'dbo.Users'))
                    CREATE INDEX [IX_Users_RefreshTokenHash] ON [Users] ([RefreshTokenHash]);
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_Users_StudentCode' AND [object_id] = OBJECT_ID(N'dbo.Users'))
                    CREATE UNIQUE INDEX [IX_Users_StudentCode] ON [Users] ([StudentCode]) WHERE [StudentCode] <> N'';
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF OBJECT_ID(N'dbo.ExamParticipants', N'U') IS NULL
                BEGIN
                    CREATE TABLE [ExamParticipants] (
                        [Id] uniqueidentifier NOT NULL,
                        [ExamId] uniqueidentifier NOT NULL,
                        [StudentId] uniqueidentifier NOT NULL,
                        [Code] nvarchar(12) NOT NULL CONSTRAINT [DF_ExamParticipants_Code] DEFAULT N'',
                        [Status] nvarchar(24) NOT NULL,
                        [JoinedAtUtc] datetime2 NULL,
                        [LastSeenAtUtc] datetime2 NULL,
                        [CreatedAtUtc] datetime2 NOT NULL,
                        CONSTRAINT [PK_ExamParticipants] PRIMARY KEY ([Id]),
                        CONSTRAINT [FK_ExamParticipants_Exams_ExamId] FOREIGN KEY ([ExamId]) REFERENCES [Exams] ([Id]) ON DELETE CASCADE,
                        CONSTRAINT [FK_ExamParticipants_Users_StudentId] FOREIGN KEY ([StudentId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
                    );
                    CREATE UNIQUE INDEX [IX_ExamParticipants_ExamId_StudentId] ON [ExamParticipants] ([ExamId], [StudentId]);
                    CREATE INDEX [IX_ExamParticipants_StudentId] ON [ExamParticipants] ([StudentId]);
                END;
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF COL_LENGTH('dbo.ExamParticipants', 'Code') IS NULL
                    ALTER TABLE [ExamParticipants] ADD [Code] nvarchar(12) NOT NULL CONSTRAINT [DF_ExamParticipants_Code] DEFAULT N'';
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_ExamParticipants_Code' AND [object_id] = OBJECT_ID(N'dbo.ExamParticipants'))
                    CREATE UNIQUE INDEX [IX_ExamParticipants_Code] ON [ExamParticipants] ([Code]) WHERE [Code] <> N'';
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_Exams_JoinCode' AND [object_id] = OBJECT_ID(N'dbo.Exams'))
                    CREATE UNIQUE INDEX [IX_Exams_JoinCode] ON [Exams] ([JoinCode]);
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE [Exams] SET [Status] = N'Waiting' WHERE [IsPublished] = 1 AND [Status] = N'Draft';
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                ;WITH duplicates AS (
                    SELECT [Id], ROW_NUMBER() OVER (PARTITION BY [ExamId], [StudentId] ORDER BY [StartedAtUtc] DESC, [AttemptNumber] DESC) AS [RowNumber]
                    FROM [Submissions] WHERE [Status] = N'InProgress'
                )
                UPDATE s SET [Status] = N'Abandoned', [SubmittedAtUtc] = COALESCE(s.[SubmittedAtUtc], GETUTCDATE())
                FROM [Submissions] s INNER JOIN duplicates d ON s.[Id] = d.[Id]
                WHERE d.[RowNumber] > 1;
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_Submissions_Exam_Student_InProgress' AND [object_id] = OBJECT_ID(N'dbo.Submissions'))
                    CREATE UNIQUE INDEX [IX_Submissions_Exam_Student_InProgress] ON [Submissions] ([ExamId], [StudentId]) WHERE [Status] = N'InProgress';
                """, cancellationToken);

            // ---- Platform upgrade: identity/phones, academic history, subscriptions, invitations ----
            await db.Database.ExecuteSqlRawAsync("""
                IF COL_LENGTH('dbo.Users', 'NormalizedName') IS NULL
                    ALTER TABLE [Users] ADD [NormalizedName] nvarchar(256) NOT NULL CONSTRAINT [DF_Users_NormalizedName] DEFAULT N'';
                IF COL_LENGTH('dbo.Users', 'StudentPhone') IS NULL
                    ALTER TABLE [Users] ADD [StudentPhone] nvarchar(20) NULL;
                IF COL_LENGTH('dbo.Users', 'ParentPhone') IS NULL
                    ALTER TABLE [Users] ADD [ParentPhone] nvarchar(20) NULL;
                IF COL_LENGTH('dbo.Users', 'TeacherCount') IS NULL
                    ALTER TABLE [Users] ADD [TeacherCount] int NULL;
                IF COL_LENGTH('dbo.Users', 'RegistrationSource') IS NULL
                    ALTER TABLE [Users] ADD [RegistrationSource] nvarchar(24) NOT NULL CONSTRAINT [DF_Users_RegistrationSource] DEFAULT N'Self';
                IF COL_LENGTH('dbo.Users', 'SessionExpiresAtUtc') IS NULL
                    ALTER TABLE [Users] ADD [SessionExpiresAtUtc] datetime2 NULL;
                IF COL_LENGTH('dbo.Users', 'UpdatedAtUtc') IS NULL
                    ALTER TABLE [Users] ADD [UpdatedAtUtc] datetime2 NOT NULL CONSTRAINT [DF_Users_UpdatedAtUtc] DEFAULT GETUTCDATE();
                IF COL_LENGTH('dbo.Exams', 'AcademicYear') IS NULL
                    ALTER TABLE [Exams] ADD [AcademicYear] nvarchar(16) NOT NULL CONSTRAINT [DF_Exams_AcademicYear] DEFAULT N'';
                IF COL_LENGTH('dbo.Submissions', 'StageAtAttempt') IS NULL
                    ALTER TABLE [Submissions] ADD [StageAtAttempt] nvarchar(32) NULL;
                IF COL_LENGTH('dbo.Submissions', 'GradeAtAttempt') IS NULL
                    ALTER TABLE [Submissions] ADD [GradeAtAttempt] nvarchar(64) NULL;
                IF COL_LENGTH('dbo.Submissions', 'AcademicYearAtAttempt') IS NULL
                    ALTER TABLE [Submissions] ADD [AcademicYearAtAttempt] nvarchar(16) NULL;
                IF COL_LENGTH('dbo.TeacherStudentRequests', 'InitiatedBy') IS NULL
                    ALTER TABLE [TeacherStudentRequests] ADD [InitiatedBy] nvarchar(24) NOT NULL CONSTRAINT [DF_TeacherStudentRequests_InitiatedBy] DEFAULT N'Student';
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_Users_StudentPhone' AND [object_id] = OBJECT_ID(N'dbo.Users'))
                    CREATE UNIQUE INDEX [IX_Users_StudentPhone] ON [Users] ([StudentPhone]) WHERE [StudentPhone] IS NOT NULL AND [StudentPhone] <> N'';
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_Users_NormalizedName_Student' AND [object_id] = OBJECT_ID(N'dbo.Users'))
                    CREATE UNIQUE INDEX [IX_Users_NormalizedName_Student] ON [Users] ([NormalizedName]) WHERE [Role] = N'Student' AND [NormalizedName] <> N'';
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_Users_ParentPhone' AND [object_id] = OBJECT_ID(N'dbo.Users'))
                    CREATE INDEX [IX_Users_ParentPhone] ON [Users] ([ParentPhone]);
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_Exams_AcademicYear' AND [object_id] = OBJECT_ID(N'dbo.Exams'))
                    CREATE INDEX [IX_Exams_AcademicYear] ON [Exams] ([AcademicYear]);
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF OBJECT_ID(N'dbo.StudentAcademicChanges', N'U') IS NULL
                BEGIN
                    CREATE TABLE [StudentAcademicChanges] (
                        [Id] uniqueidentifier NOT NULL,
                        [StudentId] uniqueidentifier NOT NULL,
                        [PreviousStage] nvarchar(32) NULL,
                        [PreviousGrade] nvarchar(64) NULL,
                        [NewStage] nvarchar(32) NULL,
                        [NewGrade] nvarchar(64) NULL,
                        [ChangedByUserId] uniqueidentifier NULL,
                        [ChangedByRole] nvarchar(24) NOT NULL,
                        [Source] nvarchar(24) NOT NULL,
                        [ChangedAtUtc] datetime2 NOT NULL,
                        CONSTRAINT [PK_StudentAcademicChanges] PRIMARY KEY ([Id]),
                        CONSTRAINT [FK_StudentAcademicChanges_Users_StudentId] FOREIGN KEY ([StudentId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
                    );
                    CREATE INDEX [IX_StudentAcademicChanges_StudentId_ChangedAtUtc] ON [StudentAcademicChanges] ([StudentId], [ChangedAtUtc]);
                END;
                IF OBJECT_ID(N'dbo.SubscriptionPlans', N'U') IS NULL
                BEGIN
                    CREATE TABLE [SubscriptionPlans] (
                        [Id] uniqueidentifier NOT NULL,
                        [Code] nvarchar(48) NOT NULL,
                        [Name] nvarchar(120) NOT NULL,
                        [Description] nvarchar(500) NULL,
                        [IsActive] bit NOT NULL,
                        [IsDefault] bit NOT NULL,
                        [SortOrder] int NOT NULL,
                        [FeaturesJson] nvarchar(max) NOT NULL,
                        [CreatedAtUtc] datetime2 NOT NULL,
                        [UpdatedAtUtc] datetime2 NOT NULL,
                        CONSTRAINT [PK_SubscriptionPlans] PRIMARY KEY ([Id])
                    );
                    CREATE UNIQUE INDEX [IX_SubscriptionPlans_Code] ON [SubscriptionPlans] ([Code]);
                END;
                IF OBJECT_ID(N'dbo.TeacherSubscriptions', N'U') IS NULL
                BEGIN
                    CREATE TABLE [TeacherSubscriptions] (
                        [Id] uniqueidentifier NOT NULL,
                        [TeacherId] uniqueidentifier NOT NULL,
                        [PlanId] uniqueidentifier NOT NULL,
                        [Status] nvarchar(24) NOT NULL,
                        [StartedAtUtc] datetime2 NULL,
                        [EndsAtUtc] datetime2 NULL,
                        [ConfirmedAtUtc] datetime2 NULL,
                        [CreatedAtUtc] datetime2 NOT NULL,
                        [UpdatedAtUtc] datetime2 NOT NULL,
                        CONSTRAINT [PK_TeacherSubscriptions] PRIMARY KEY ([Id]),
                        CONSTRAINT [FK_TeacherSubscriptions_Users_TeacherId] FOREIGN KEY ([TeacherId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE,
                        CONSTRAINT [FK_TeacherSubscriptions_SubscriptionPlans_PlanId] FOREIGN KEY ([PlanId]) REFERENCES [SubscriptionPlans] ([Id]) ON DELETE NO ACTION
                    );
                    CREATE INDEX [IX_TeacherSubscriptions_TeacherId] ON [TeacherSubscriptions] ([TeacherId]);
                END;
                IF OBJECT_ID(N'dbo.SystemSettings', N'U') IS NULL
                BEGIN
                    CREATE TABLE [SystemSettings] (
                        [Key] nvarchar(120) NOT NULL,
                        [Value] nvarchar(max) NOT NULL,
                        [UpdatedAtUtc] datetime2 NOT NULL,
                        CONSTRAINT [PK_SystemSettings] PRIMARY KEY ([Key])
                    );
                END;
                IF OBJECT_ID(N'dbo.TeacherStudentInvitations', N'U') IS NULL
                BEGIN
                    CREATE TABLE [TeacherStudentInvitations] (
                        [Id] uniqueidentifier NOT NULL,
                        [TeacherId] uniqueidentifier NOT NULL,
                        [StudentName] nvarchar(200) NOT NULL,
                        [StudentPhone] nvarchar(20) NOT NULL,
                        [ParentPhone] nvarchar(20) NULL,
                        [Stage] nvarchar(32) NOT NULL,
                        [Grade] nvarchar(64) NOT NULL,
                        [Token] nvarchar(64) NOT NULL,
                        [Status] nvarchar(24) NOT NULL,
                        [CreatedAtUtc] datetime2 NOT NULL,
                        [ExpiresAtUtc] datetime2 NOT NULL,
                        [AcceptedAtUtc] datetime2 NULL,
                        [AcceptedStudentId] uniqueidentifier NULL,
                        CONSTRAINT [PK_TeacherStudentInvitations] PRIMARY KEY ([Id]),
                        CONSTRAINT [FK_TeacherStudentInvitations_Users_TeacherId] FOREIGN KEY ([TeacherId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
                    );
                    CREATE UNIQUE INDEX [IX_TeacherStudentInvitations_Token] ON [TeacherStudentInvitations] ([Token]);
                    CREATE INDEX [IX_TeacherStudentInvitations_TeacherId_StudentPhone] ON [TeacherStudentInvitations] ([TeacherId], [StudentPhone]);
                END;
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE [Exams] SET [AcademicYear] =
                    CASE WHEN MONTH([CreatedAtUtc]) >= 9
                        THEN CAST(YEAR([CreatedAtUtc]) AS nvarchar(4)) + N'/' + CAST(YEAR([CreatedAtUtc]) + 1 AS nvarchar(4))
                        ELSE CAST(YEAR([CreatedAtUtc]) - 1 AS nvarchar(4)) + N'/' + CAST(YEAR([CreatedAtUtc]) AS nvarchar(4)) END
                WHERE [AcademicYear] = N'';
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE s SET
                    [StageAtAttempt] = COALESCE(s.[StageAtAttempt], u.[Stage]),
                    [GradeAtAttempt] = COALESCE(s.[GradeAtAttempt], u.[Grade]),
                    [AcademicYearAtAttempt] = COALESCE(s.[AcademicYearAtAttempt], NULLIF(e.[AcademicYear], N''), N'')
                FROM [Submissions] s
                INNER JOIN [Users] u ON u.[Id] = s.[StudentId]
                INNER JOIN [Exams] e ON e.[Id] = s.[ExamId]
                WHERE s.[StageAtAttempt] IS NULL AND s.[GradeAtAttempt] IS NULL AND s.[AcademicYearAtAttempt] IS NULL;
                """, cancellationToken);
            return;
        }

        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "StudentCode" character varying(12) NOT NULL DEFAULT '';
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "PasswordResetToken" character varying(128) NULL;
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "PasswordResetExpiresUtc" timestamp with time zone NULL;
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "Stage" character varying(32) NULL;
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "Grade" character varying(64) NULL;
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_StudentCode" ON "Users" ("StudentCode") WHERE "StudentCode" <> '';
                CREATE TABLE IF NOT EXISTS "ExamParticipants" (
                    "Id" uuid NOT NULL,
                    "ExamId" uuid NOT NULL,
                    "StudentId" uuid NOT NULL,
                    "Code" character varying(12) NOT NULL DEFAULT '',
                    "Status" character varying(24) NOT NULL,
                    "JoinedAtUtc" timestamp with time zone NULL,
                    "LastSeenAtUtc" timestamp with time zone NULL,
                    "CreatedAtUtc" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_ExamParticipants" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_ExamParticipants_Exams_ExamId" FOREIGN KEY ("ExamId") REFERENCES "Exams" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_ExamParticipants_Users_StudentId" FOREIGN KEY ("StudentId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                );
                ALTER TABLE "ExamParticipants" ADD COLUMN IF NOT EXISTS "Code" character varying(12) NOT NULL DEFAULT '';
                ALTER TABLE "Exams" ADD COLUMN IF NOT EXISTS "JoinCode" character varying(450) NOT NULL DEFAULT '';
                DROP INDEX IF EXISTS "IX_ExamParticipants_Code";
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExamParticipants_Code" ON "ExamParticipants" ("Code") WHERE "Code" <> '';
                DROP INDEX IF EXISTS "IX_Exams_JoinCode";
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Exams_JoinCode" ON "Exams" ("JoinCode");
                ALTER TABLE "Exams" ADD COLUMN IF NOT EXISTS "Status" character varying(24) NOT NULL DEFAULT 'Draft';
                ALTER TABLE "Exams" ADD COLUMN IF NOT EXISTS "ScheduledAtUtc" timestamp with time zone NULL;
                ALTER TABLE "Exams" ADD COLUMN IF NOT EXISTS "PausedAtUtc" timestamp with time zone NULL;
                ALTER TABLE "Exams" ADD COLUMN IF NOT EXISTS "AvailableFromUtc" timestamp with time zone NULL;
                ALTER TABLE "Exams" ADD COLUMN IF NOT EXISTS "AvailableUntilUtc" timestamp with time zone NULL;
                ALTER TABLE "Exams" ADD COLUMN IF NOT EXISTS "Stage" character varying(32) NULL;
                ALTER TABLE "Exams" ADD COLUMN IF NOT EXISTS "Grade" character varying(64) NULL;
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name = 'Exams'
                          AND column_name = 'ResultsPublishedAtUtc'
                    ) THEN
                        ALTER TABLE "Exams" ADD COLUMN "ResultsPublishedAtUtc" timestamp with time zone NULL;
                        -- Preserve visibility of exams that were already finished before this
                        -- column existed. Newly finished exams require explicit publication.
                        UPDATE "Exams" SET "ResultsPublishedAtUtc" = NOW() WHERE "Status" = 'Finished';
                    END IF;
                END $$;
                ALTER TABLE "Exams" ADD COLUMN IF NOT EXISTS "ResultsReleaseMode" character varying(32) NULL;
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "RefreshTokenHash" character varying(64) NULL;
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "RefreshTokenExpiresUtc" timestamp with time zone NULL;
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "RefreshTokenCreatedAtUtc" timestamp with time zone NULL;
                CREATE INDEX IF NOT EXISTS "IX_Users_RefreshTokenHash" ON "Users" ("RefreshTokenHash");
                CREATE TABLE IF NOT EXISTS "TeacherStudentRequests" (
                    "Id" uuid NOT NULL,
                    "TeacherId" uuid NOT NULL,
                    "StudentId" uuid NOT NULL,
                    "Status" character varying(24) NOT NULL DEFAULT 'Pending',
                    "CreatedAtUtc" timestamp with time zone NOT NULL,
                    "DecidedAtUtc" timestamp with time zone NULL,
                    CONSTRAINT "PK_TeacherStudentRequests" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_TeacherStudentRequests_Users_TeacherId" FOREIGN KEY ("TeacherId") REFERENCES "Users" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_TeacherStudentRequests_Users_StudentId" FOREIGN KEY ("StudentId") REFERENCES "Users" ("Id") ON DELETE NO ACTION
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_TeacherStudentRequests_TeacherId_StudentId" ON "TeacherStudentRequests" ("TeacherId", "StudentId");
                CREATE INDEX IF NOT EXISTS "IX_TeacherStudentRequests_StudentId" ON "TeacherStudentRequests" ("StudentId");
                ALTER TABLE "TeacherStudentRequests" ADD COLUMN IF NOT EXISTS "UnlinkedAtUtc" timestamp with time zone NULL;
                ALTER TABLE "TeacherStudentRequests" ADD COLUMN IF NOT EXISTS "SuspendedAtUtc" timestamp with time zone NULL;
                CREATE TABLE IF NOT EXISTS "TeacherStudentExamBans" (
                    "Id" uuid NOT NULL,
                    "TeacherId" uuid NOT NULL,
                    "StudentId" uuid NOT NULL,
                    "ExamId" uuid NOT NULL,
                    "CreatedAtUtc" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_TeacherStudentExamBans" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_TeacherStudentExamBans_Users_TeacherId" FOREIGN KEY ("TeacherId") REFERENCES "Users" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_TeacherStudentExamBans_Users_StudentId" FOREIGN KEY ("StudentId") REFERENCES "Users" ("Id") ON DELETE NO ACTION,
                    CONSTRAINT "FK_TeacherStudentExamBans_Exams_ExamId" FOREIGN KEY ("ExamId") REFERENCES "Exams" ("Id") ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_TeacherStudentExamBans_ExamId_StudentId" ON "TeacherStudentExamBans" ("ExamId", "StudentId");
                CREATE INDEX IF NOT EXISTS "IX_TeacherStudentExamBans_TeacherId_StudentId" ON "TeacherStudentExamBans" ("TeacherId", "StudentId");
                INSERT INTO "TeacherStudentRequests" ("Id", "TeacherId", "StudentId", "Status", "CreatedAtUtc", "DecidedAtUtc")
                SELECT gen_random_uuid(), x."TeacherId", x."StudentId", 'Accepted', NOW(), NOW()
                FROM (SELECT DISTINCT e."TeacherId", p."StudentId" FROM "ExamParticipants" p INNER JOIN "Exams" e ON e."Id" = p."ExamId") x
                ON CONFLICT ("TeacherId", "StudentId") DO NOTHING;
                ALTER TABLE "Submissions" ADD COLUMN IF NOT EXISTS "ExtraTimeSeconds" integer NOT NULL DEFAULT 0;
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExamParticipants_ExamId_StudentId" ON "ExamParticipants" ("ExamId", "StudentId");
                CREATE INDEX IF NOT EXISTS "IX_ExamParticipants_StudentId" ON "ExamParticipants" ("StudentId");
                UPDATE "Exams" SET "Status" = 'Waiting' WHERE "IsPublished" = TRUE AND "Status" = 'Draft';
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                WITH duplicates AS (
                    SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "ExamId", "StudentId" ORDER BY "StartedAtUtc" DESC, "AttemptNumber" DESC) AS rn
                    FROM "Submissions" WHERE "Status" = 'InProgress'
                )
                UPDATE "Submissions" s SET "Status" = 'Abandoned', "SubmittedAtUtc" = COALESCE(s."SubmittedAtUtc", NOW())
                FROM duplicates d WHERE s."Id" = d."Id" AND d.rn > 1;
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Submissions_Exam_Student_InProgress" ON "Submissions" ("ExamId", "StudentId") WHERE "Status" = 'InProgress';
                """, cancellationToken);

            // ---- Platform upgrade: identity/phones, academic history, subscriptions, invitations ----
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "NormalizedName" character varying(256) NOT NULL DEFAULT '';
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "StudentPhone" character varying(20) NULL;
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "ParentPhone" character varying(20) NULL;
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "TeacherCount" integer NULL;
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "RegistrationSource" character varying(24) NOT NULL DEFAULT 'Self';
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "SessionExpiresAtUtc" timestamp with time zone NULL;
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "UpdatedAtUtc" timestamp with time zone NOT NULL DEFAULT NOW();
                ALTER TABLE "Exams" ADD COLUMN IF NOT EXISTS "AcademicYear" character varying(16) NOT NULL DEFAULT '';
                ALTER TABLE "Submissions" ADD COLUMN IF NOT EXISTS "StageAtAttempt" character varying(32) NULL;
                ALTER TABLE "Submissions" ADD COLUMN IF NOT EXISTS "GradeAtAttempt" character varying(64) NULL;
                ALTER TABLE "Submissions" ADD COLUMN IF NOT EXISTS "AcademicYearAtAttempt" character varying(16) NULL;
                ALTER TABLE "TeacherStudentRequests" ADD COLUMN IF NOT EXISTS "InitiatedBy" character varying(24) NOT NULL DEFAULT 'Student';
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_StudentPhone" ON "Users" ("StudentPhone") WHERE "StudentPhone" IS NOT NULL AND "StudentPhone" <> '';
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_NormalizedName_Student" ON "Users" ("NormalizedName") WHERE "Role" = 'Student' AND "NormalizedName" <> '';
                CREATE INDEX IF NOT EXISTS "IX_Users_ParentPhone" ON "Users" ("ParentPhone");
                CREATE INDEX IF NOT EXISTS "IX_Exams_AcademicYear" ON "Exams" ("AcademicYear");
                CREATE TABLE IF NOT EXISTS "StudentAcademicChanges" (
                    "Id" uuid NOT NULL,
                    "StudentId" uuid NOT NULL,
                    "PreviousStage" character varying(32) NULL,
                    "PreviousGrade" character varying(64) NULL,
                    "NewStage" character varying(32) NULL,
                    "NewGrade" character varying(64) NULL,
                    "ChangedByUserId" uuid NULL,
                    "ChangedByRole" character varying(24) NOT NULL,
                    "Source" character varying(24) NOT NULL,
                    "ChangedAtUtc" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_StudentAcademicChanges" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_StudentAcademicChanges_Users_StudentId" FOREIGN KEY ("StudentId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS "IX_StudentAcademicChanges_StudentId_ChangedAtUtc" ON "StudentAcademicChanges" ("StudentId", "ChangedAtUtc");
                CREATE TABLE IF NOT EXISTS "SubscriptionPlans" (
                    "Id" uuid NOT NULL,
                    "Code" character varying(48) NOT NULL,
                    "Name" character varying(120) NOT NULL,
                    "Description" character varying(500) NULL,
                    "IsActive" boolean NOT NULL,
                    "IsDefault" boolean NOT NULL,
                    "SortOrder" integer NOT NULL,
                    "FeaturesJson" text NOT NULL,
                    "CreatedAtUtc" timestamp with time zone NOT NULL,
                    "UpdatedAtUtc" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_SubscriptionPlans" PRIMARY KEY ("Id")
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_SubscriptionPlans_Code" ON "SubscriptionPlans" ("Code");
                CREATE TABLE IF NOT EXISTS "TeacherSubscriptions" (
                    "Id" uuid NOT NULL,
                    "TeacherId" uuid NOT NULL,
                    "PlanId" uuid NOT NULL,
                    "Status" character varying(24) NOT NULL,
                    "StartedAtUtc" timestamp with time zone NULL,
                    "EndsAtUtc" timestamp with time zone NULL,
                    "ConfirmedAtUtc" timestamp with time zone NULL,
                    "CreatedAtUtc" timestamp with time zone NOT NULL,
                    "UpdatedAtUtc" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_TeacherSubscriptions" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_TeacherSubscriptions_Users_TeacherId" FOREIGN KEY ("TeacherId") REFERENCES "Users" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_TeacherSubscriptions_SubscriptionPlans_PlanId" FOREIGN KEY ("PlanId") REFERENCES "SubscriptionPlans" ("Id") ON DELETE NO ACTION
                );
                CREATE INDEX IF NOT EXISTS "IX_TeacherSubscriptions_TeacherId" ON "TeacherSubscriptions" ("TeacherId");
                CREATE TABLE IF NOT EXISTS "SystemSettings" (
                    "Key" character varying(120) NOT NULL,
                    "Value" text NOT NULL,
                    "UpdatedAtUtc" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_SystemSettings" PRIMARY KEY ("Key")
                );
                CREATE TABLE IF NOT EXISTS "TeacherStudentInvitations" (
                    "Id" uuid NOT NULL,
                    "TeacherId" uuid NOT NULL,
                    "StudentName" character varying(200) NOT NULL,
                    "StudentPhone" character varying(20) NOT NULL,
                    "ParentPhone" character varying(20) NULL,
                    "Stage" character varying(32) NOT NULL,
                    "Grade" character varying(64) NOT NULL,
                    "Token" character varying(64) NOT NULL,
                    "Status" character varying(24) NOT NULL,
                    "CreatedAtUtc" timestamp with time zone NOT NULL,
                    "ExpiresAtUtc" timestamp with time zone NOT NULL,
                    "AcceptedAtUtc" timestamp with time zone NULL,
                    "AcceptedStudentId" uuid NULL,
                    CONSTRAINT "PK_TeacherStudentInvitations" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_TeacherStudentInvitations_Users_TeacherId" FOREIGN KEY ("TeacherId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_TeacherStudentInvitations_Token" ON "TeacherStudentInvitations" ("Token");
                CREATE INDEX IF NOT EXISTS "IX_TeacherStudentInvitations_TeacherId_StudentPhone" ON "TeacherStudentInvitations" ("TeacherId", "StudentPhone");
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE "Exams" SET "AcademicYear" =
                    CASE WHEN EXTRACT(MONTH FROM "CreatedAtUtc") >= 9
                        THEN EXTRACT(YEAR FROM "CreatedAtUtc")::int::text || '/' || (EXTRACT(YEAR FROM "CreatedAtUtc")::int + 1)::text
                        ELSE (EXTRACT(YEAR FROM "CreatedAtUtc")::int - 1)::text || '/' || EXTRACT(YEAR FROM "CreatedAtUtc")::int::text END
                WHERE "AcademicYear" = '';
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE "Submissions" s SET
                    "StageAtAttempt" = COALESCE(s."StageAtAttempt", u."Stage"),
                    "GradeAtAttempt" = COALESCE(s."GradeAtAttempt", u."Grade"),
                    "AcademicYearAtAttempt" = COALESCE(s."AcademicYearAtAttempt", NULLIF(e."AcademicYear", ''), '')
                FROM "Users" u, "Exams" e
                WHERE u."Id" = s."StudentId" AND e."Id" = s."ExamId"
                  AND s."StageAtAttempt" IS NULL AND s."GradeAtAttempt" IS NULL AND s."AcademicYearAtAttempt" IS NULL;
                """, cancellationToken);
        }
    }
}
