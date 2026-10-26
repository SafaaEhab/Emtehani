# امتحاني — Full Stack (ASP.NET Core + React)

منصة امتحانات عربية بواجهة RTL، مبنية بـ Clean Architecture:
`Emtehani.Domain`، `Emtehani.Application`، `Emtehani.Infrastructure`، `Emtehani.API`، وواجهة React + Vite.

## المتطلبات

- .NET 10 SDK
- Node.js 22.12+ (الواجهة تعلن هذا الحد في `engines`)
- SQL Server أو PostgreSQL
- PowerShell أو Terminal

## التشغيل المحلي

من مجلد `backend`:

```bash
dotnet restore Emtehani.slnx
dotnet build Emtehani.slnx
dotnet run --project Emtehani.API
```

- Swagger: `/swagger` (مفعّل تلقائياً في Development فقط).
- الفحص الصحي: `/health`.
- في Development يمكن تشغيل SQL Server LocalDB افتراضياً، أو تمرير `ConnectionStrings__DefaultConnection` / `DATABASE_URL`.
- في Development فقط، عند غياب إعدادات SMTP، تُفعّل الحسابات تلقائياً لتسهيل التجربة. لا تستخدم هذا السلوك في الإنتاج.

من مجلد `frontend`:

```bash
cp .env.example .env
npm install
npm run dev
```

`VITE_API_URL` هو أصل الـ API دون `/api`، مثل `http://localhost:5000`.

## الاختبارات والتحقق

```bash
# من مجلد backend
dotnet test Emtehani.slnx

# من مجلد frontend
npm ci
npm run build
```

اختبارات .NET تشمل اختبارات الوحدة لتطبيع الإجابات والتقدير، واختبارات تكامل عبر `WebApplicationFactory` مع SQLite، واختبارات مخطط PostgreSQL حقيقية عند تفعيلها:

```powershell
$env:EMTEHANI_RUN_POSTGRES_TESTS = "1"
$env:EMTEHANI_POSTGRES_TEST_CONNECTION = "Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=...;SSL Mode=Disable"
dotnet test Emtehani.slnx
```

تغطي هذه الاختبارات: تسجيل الطالب/المدرس دون كود دعوة، نجاح الإقلاع في الإنتاج دون كود دعوة، إنشاء امتحان ونشره وفتحه، كود الطالب، بدء المحاولة، الحفظ، التسليم مرة واحدة (idempotent)، تصحيح المقال بمعرّف إجابة التسليم، منع بدء محاولة دون انضمام، ومنع مدرس آخر من إدارة الامتحان، وفهرس/مخطط PostgreSQL.

### اختبارات الواجهة الشاملة (Playwright)

تتطلب قاعدة PostgreSQL 17 معزولة، وليس قاعدة الإنتاج:

```powershell
# من مجلد backend
dotnet build Emtehani.slnx

# من مجلد frontend
npm ci
npx playwright install chromium
$env:E2E_DATABASE_CONNECTION = "Host=127.0.0.1;Port=5432;Database=emtehani_e2e;Username=postgres;Password=...;SSL Mode=Disable"
npm run test:e2e
```

تُشغّل الاختبارات API مؤقتاً على المنفذ `5099` والواجهة على `4173`. تغطي: تسجيل المدرس، واجهة RTL، دورة امتحان كاملة مع الحفظ التلقائي واستئناف الصفحة، تسليماً متزامناً مع حفظ تلقائي، انتهاء المؤقت والتسليم التلقائي، تصحيح مقال، وتصدير CSV وصلاحياته. تُنشأ الحسابات بمعرّفات فريدة في قاعدة الاختبار؛ استخدم قاعدة مخصصة للاختبار ولا توجّهها إلى الإنتاج.

## متغيرات الإنتاج

يجب ضبط المتغيرات في لوحة Vercel (Environment Variables)، وليس في المستودع:

| المتغير | إلزامي | الوصف |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | نعم | `Production` |
| `Jwt__Key` أو `JWT_KEY` | نعم | سر عشوائي 32 حرفاً على الأقل، ولا يعاد استخدامه بين البيئات |
| `Jwt__Issuer` / `Jwt__Audience` | مستحسن | `Emtehani` / `Emtehani.Client` |
| `FrontendOrigin` | نعم | أصل الواجهة بالضبط، مثل `https://emtehani.vercel.app`، ويمكن فصل أكثر من أصل بفاصلة |
| `PublicApiUrl` | نعم | أصل الـ API العام **دون `/api`**، مثل `https://emtehani-api.vercel.app`؛ يُستخدم في رابط تأكيد البريد |
| `DATABASE_URL` أو `POSTGRES_URL` | نعم* | رابط PostgreSQL، مثل `postgres://user:password@host/db?sslmode=require` |
| `ConnectionStrings__DefaultConnection` | بديل | اتصال SQL Server أو PostgreSQL إذا لم يُستخدم `DATABASE_URL` |
| `Auth__AutoConfirmWithoutSmtp` | نعم | يجب أن يكون `false` في الإنتاج |
| `Email__From` | نعم | عنوان المرسل الموثوق |
| `Email__SmtpHost` | نعم | مثل `smtp.gmail.com` |
| `Email__SmtpPort` | نعم | عادة `587` |
| `Email__Username` / `Email__Password` | نعم | بيانات SMTP؛ استخدم App Password ولا تضعها في Git |
| `Email__EnableSsl` | نعم | `true` |
| `Swagger__Enabled` | لا | `false` في الإنتاج |
| `PORT` | لا | يضبطه Vercel/الحاوية عادة |

\* يلزم مصدر اتصال واحد صالح على الأقل. لا تضع القيم الحقيقية في `.env.example` أو أي ملف متعقّب.

### قاعدة البيانات والنسخ الاحتياطي

- التطبيق ينشئ المخطط عند بدء التشغيل عبر `EnsureCreated` ثم يطبق إصلاحات توافقية في `ExamWorkflowSchema` (أعمدة الحالة، جدول المشاركين، تنظيف المحاولات المكررة، وفهرس `InProgress` الفريد).
- ملفات `Migrations` الحالية خاصة بـ SQL Server ولا تُستخدم تلقائياً عند الإقلاع. لا تعتمد عليها وحدها لنشر PostgreSQL؛ راجع المخطط والنسخ الاحتياطي قبل النشر.
- خذ نسخة احتياطية أو أنشئ فرعاً من قاعدة Neon قبل أول نشر يحتوي تغييرات مخطط.
- للتحقق من النسخة الاحتياطية محلياً على قاعدة معزولة:

```powershell
pg_dump -Fc --no-owner --no-privileges -d "<source-db>" -f backup.dump
createdb -h 127.0.0.1 -p 5432 -U postgres emtehani_restore_check
pg_restore -h 127.0.0.1 -p 5432 -U postgres -d emtehani_restore_check --no-owner --no-privileges --exit-on-error backup.dump
```

- تحقق بعد الاستعادة من وجود جداول `Users` و`Exams` و`Submissions` و`Answers` و`AuditEvents`، ومن الفهرس الفريد `IX_Answers_SubmissionId_QuestionId` وفهرس `IX_Submissions_Exam_Student_InProgress`، ثم اختبر تسجيل الدخول وقراءة امتحان قبل الاعتماد على النسخة.
- على Neon، استخدم فرعاً أو استعادة نقطة زمنية (PITR) بدلاً من `pg_restore` فوق قاعدة الإنتاج. لا تنفّذ استعادة إنتاج دون نسخة تحقق وموافقة صريحة.

### البريد

- في الإنتاج، إن لم تُضبط SMTP فلن يُسمح بإنشاء الحسابات ويعيد التسجيل `503`؛ هذا مقصود لمنع حسابات غير موثقة.
- `PublicApiUrl` يجب أن يكون عنواناً عاماً بـ HTTPS حتى يعمل رابط التأكيد.
- لا تضع أسرار SMTP أو JWT أو OIDC في Git أو في تقرير أو لقطة شاشة.

## الأمان والخصوصية

- كلمات المرور: PBKDF2-SHA256 مع ملح عشوائي، ومقارنة ثابتة الزمن.
- JWT: HMAC-SHA256، تحقق من المُصدر والجمهور والصلاحية، ومفتاح 32+ حرفاٌ.
- تفعيل البريد عبر رمز عشوائي 32 بايت ومقارنة مباشرة للرمز، مع صلاحية 24 ساعة.
- CORS بأصول صريحة فقط، وأسرار إنتاج إلزامية، ورفض `*` والأصول غير الصالحة.
- رؤوس أمان للـ API والواجهة، وHSTS في الإنتاج، ومنع تخزين استجابات `/api`.
- تحديد معدل الطلبات، وحدود لحجم الطلب والبيانات，تحقق من الملكية والدور في نقاط النهاية.
- منع بدء المحاولة دون انضمام，منع الوصول لورقة الامتحان قبل الأوان，وإبطال التسليم المكرر.
- تصدير CSV محمي من حقن الصيغ，ومراجعة المقالات مقيدة بمدرس الامتحان.
- نقطة نهاية إعادة التعيين الإداري (`/api/admin/reset`) معطلة افتراضياً في الإنتاج وتتطلب JWT مع دور Admin وسجل تدقيق قبل الحذف.
- قفل تنافسي باستخدام مفاتيح استشارية PostgreSQL لمنع التضمين المتزامن للتسليم.

## قائمة التحقق قبل النشر

1. أضف المتغيرات الإلزامية في Vercel وتأكد من `ASPNETCORE_ENVIRONMENT=Production`.
2. استخدم مفتاح JWT عشوائياً جديداً ولا تشاركه بين الإنتاج والتجربة.
3. اضبط `FrontendOrigin` و`PublicApiUrl` بعنواني HTTPS الفعليين، دون مسار أو شرطة زائدة في الأصل.
4. اضبط SMTP واختبر رسالة تأكيد فعلية，ثم تأكد أن `Auth__AutoConfirmWithoutSmtp=false`.
5. تحقق من نجاح الإقلاع: سجّل مدرساً من صفحة التسجيل وتأكد من قبول الطلب دون رمز دعوة.
6. تأكد أن `Admin__ResetEnabled=false` (افتراضي) ما لم تكن تحتاج إلى نقطة النهاية للإعادة الطارئة.
7. خذ نسخة احتياطية من قاعدة البيانات，تحقق من استعادتها على بيئة معزولة，ثم راقب سجلات بدء التشغيل بعد النشر.
8. نفذ اختباراً كاملاً：صحة الخدمة，تسجيل مدرس，تأكيد بريد، إنشاء امتحان، نشر，فتح، كود طالب，بدء，حفظ，تسليم، مراجعة مقال，تصدير. شغّل Playwright محلياً أو في CI قبل النشر.
9. تحقق من عدم ظهور `.env.local` أو أي سر في `git status`.
10. احذف حسابات الاختبار وأنشئ حساب المدرس الحقيقي من واجهة التسجيل.

## ملاحظات معروفة

- لا توجد استعادة كلمة مرور تلقائية؛ تُدار الحسابات من الإدارة حالياً.
- رصد فقدان التركيز مؤشر لا دليل قاطع، ويظهر في سجل المحاولة.
- التصدير CSV متوافق مع Excel، ويُفضّل فتحه عبر استيراد البيانات عند الحاجة.
- صفحة تعريف المطور تحمل اسم Ahmed Gbreel — FullStack Developer · Software Engineer؛ أضف روابط التواصل الفعلية قبل النشر.
