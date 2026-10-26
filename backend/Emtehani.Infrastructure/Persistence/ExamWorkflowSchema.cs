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
            return;
        }

        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "StudentCode" character varying(12) NOT NULL DEFAULT '';
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
        }
    }
}
