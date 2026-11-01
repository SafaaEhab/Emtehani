<#
    عرض قاعدة بيانات امتحاني المحلية (SQL Server LocalDB)

    أمثلة:
      .\view-db.ps1                      # يعرض كل الجداول وعدد الصفوف
      .\view-db.ps1 -Table Users         # يعرض بيانات جدول Users
      .\view-db.ps1 -Table Exams -Top 20 # أول 20 صف من Exams
      .\view-db.ps1 -Table Users -Grid   # يفتح النتيجة في نافذة رسومية
      .\view-db.ps1 -Query "SELECT Email, Role FROM Users"

    قاعدة البيانات الافتراضية: EmtehaniDB على (localdb)\MSSQLLocalDB
#>
[CmdletBinding()]
param(
    [string]$Table,
    [string]$Query,
    [int]$Top = 100,
    [string]$Database = "EmtehaniDB",
    [string]$ServerInstance = "(localdb)\MSSQLLocalDB",
    [switch]$Grid
)

$ErrorActionPreference = "Stop"

# عرض الحروف العربية بشكل صحيح في الطرفية
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }

function Open-Connection {
    $connectionString = "Server=$ServerInstance;Database=$Database;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=10"
    $connection = New-Object System.Data.SqlClient.SqlConnection($connectionString)
    $connection.Open()
    return $connection
}

function Invoke-Query {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Sql
    )
    $command = $Connection.CreateCommand()
    $command.CommandText = $Sql
    $table = New-Object System.Data.DataTable
    $table.Load($command.ExecuteReader())
    # الفاصلة تمنع PowerShell من تحويل الجدول إلى قائمة صفوف
    return ,$table
}

$connection = $null
try {
    $connection = Open-Connection

    if ($Query) {
        $result = Invoke-Query -Connection $connection -Sql $Query
        if ($Grid) {
            $result | Out-GridView -Title $Query
        }
        else {
            $result | Format-Table -AutoSize
        }
        return
    }

    if ($Table) {
        # حماية بسيطة من أسماء الجداول غير الصالحة
        if ($Table -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
            throw "اسم الجدول غير صالح: $Table"
        }

        $exists = Invoke-Query -Connection $connection -Sql @"
SELECT COUNT(*) AS [Exists]
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME = '$Table' AND TABLE_TYPE = 'BASE TABLE'
"@
        if ([int]$exists.Rows[0]["Exists"] -eq 0) {
            throw "الجدول '$Table' غير موجود. شغّل السكربت بدون -Table لعرض أسماء الجداول."
        }

        Write-Host "`n== $Table (أول $Top صف) ==`n" -ForegroundColor Cyan
        $result = Invoke-Query -Connection $connection -Sql "SELECT TOP ($Top) * FROM [$Table]"
        if ($Grid) {
            $result | Out-GridView -Title "$Table - $Database"
        }
        else {
            $result | Format-Table -AutoSize
        }
        return
    }

    Write-Host "`nقاعدة البيانات: $Database على $ServerInstance`n" -ForegroundColor Green
    Invoke-Query -Connection $connection -Sql @"
SELECT
    t.name AS [الجدول],
    SUM(p.rows) AS [عدد الصفوف]
FROM sys.tables t
JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
GROUP BY t.name
ORDER BY t.name
"@ | Format-Table -AutoSize

    Write-Host "اعرضي بيانات جدول بالأمر:  .\view-db.ps1 -Table Users`n" -ForegroundColor Yellow
}
catch {
    Write-Host "`nخطأ: $($_.Exception.Message)`n" -ForegroundColor Red
    Write-Host "تأكدي من تشغيل المشروع مرة واحدة على الأقل حتى تُنشأ قاعدة البيانات." -ForegroundColor Yellow
    exit 1
}
finally {
    if ($connection) { $connection.Dispose() }
}
