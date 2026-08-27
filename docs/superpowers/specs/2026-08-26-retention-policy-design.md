# Event Retention Policy — Design

**חלק מ:** R&D Logs Platform — V2, קבוצה D (תפעולי), תת-פרויקט D2
**תאריך:** 2026-08-26
**מבוסס על:** `מסמכי-אפיון/10-Security-Design.md` §"מדיניות שמירה/ארכוב" (פער ידוע, נדחה במפורש ל-V2: "job מחזורי שמוחק/מארכב Events מעבר לגיל מוגדר, configurable per-Application"), `מסמכי-אפיון/03-הגדרת-V1.md`.
**Follows:** M0–M7 (V1 המלא) + V2 קבוצות A, B1, B2, C, D1, כולם ממוזגים ל-`main`.

## 1. מטרה

טבלת `Events` היא ההיקף הגבוה ביותר בפרויקט (שורה אחת לכל log line שנקלט) וגדלה ללא הגבלה. המטרה: מנגנון שמוחק אוטומטית Events ישנים מדי, per-Application, כפי שכבר הומלץ במפורש ב-V1 כפער ידוע ל-V2.

## 2. היקף

**כלול:** מחיקה אוטומטית, יומית, של שורות `Event` ישנות מ-cutoff שנקבע per-Application.

**לא כלול (הוחלט במפורש):** שום ישות אחרת — `Finding`/`FindingStatement`/`Evidence`/`ExceptionGroup`/`AdminAuditLogEntry` נשארים ללא הגבלת-זמן, ללא תלות במדיניות הזו. ארכוב (export לפני מחיקה) — לא קיימת תשתית לזה בפרויקט, ונדחה לפרויקט נפרד עתידי אם יידרש. Batching/chunking של המחיקה — נדחה כפרימטורי, תואם לעקרון הפשטות של הפרויקט.

## 3. ארכיטקטורה

### 3.1 מודל נתונים

שדה חדש, nullable, על `Application`:

```csharp
public int? RetentionDays { get; set; }
```

**סמנטיקה (הוחלטה במפורש, לא ברירת המחדל שהומלצה):** `null` = שמירה לנצח (opt-in retention — אין מחיקה בכלל לאפליקציה שלא הוגדר לה ערך מפורש). ערך מספרי = מספר הימים לשמירה עבור אפליקציה זו בלבד.

מיגרציה חדשה מוסיפה את העמודה (nullable, ללא ברירת מחדל — `NULL` הוא הערך ההתחלתי הטבעי לכל האפליקציות הקיימות, תואם ל"שמירה לנצח כברירת מחדל").

### 3.2 מנגנון האכיפה

שירות רקע חדש, `RetentionCleanupService : BackgroundService`, מראה-ראי מדויקת של `AnalysisEngineBackgroundService` הקיים — אותה תבנית `PeriodicTimer` + `Interlocked`-guard נגד ריצות חופפות, אבל עם תקופה של **יום אחד** (לא 5 דקות).

בכל ריצה: עבור כל Application עם `RetentionDays != null` — `cutoff = DateTime.UtcNow.AddDays(-RetentionDays.Value)` → מחיקה של כל שורות `Event` עבור אותה Application עם `Timestamp < cutoff`, דרך `Task<int> DeleteOlderThanAsync(int applicationId, DateTime cutoffUtc)` חדשה על `IEventRepository` (ממומשת עם `ExecuteDeleteAsync()` של EF Core — מחיקה ישירה ב-SQL, לא טעינת ישויות לזיכרון). לכל Application עם מחיקה בפועל (count > 0) — שורת log מובנית אחת (`ILogger`, לא AdminAuditLogEntry — הוחלט במפורש) עם ה-ApplicationId, ה-cutoff, ומספר השורות שנמחקו.

### 3.3 ניהול (UI)

`Application` היום ניתן ליצירה בלבד (`Create`), ללא שום עדכון. נוסף:
- `PUT /api/v1/admin/applications/{id}` — מקבל אך ורק `RetentionDays` (לא עורך כללי ל-Application), **Super-Admin-בלבד** (נשאר תואם למדיניות הקיימת של `ApplicationsController` כולו, שכבר Super-Admin-בלבד מ-V2 קבוצה B2 — הגדרות ברמת-Application עצמה נשארות מחוץ למודל ה-per-Application-grant, בדיוק כמו `Create` שכבר Super-Admin-בלבד).
- `ApplicationsAdmin.razor`: שדה `RetentionDays` ניתן לעריכה inline בטבלת האפליקציות, מוצג/ניתן-לעריכה רק כש-`_isSuperAdmin` (אותו דגל שכבר קיים בקומפוננטה הזו מאז B2, לשליטה בטופס "צור אפליקציה").

## 4. זרימת נתונים

`RetentionCleanupService.ExecuteAsync` (טיימר יומי) → לכל Application → אם `RetentionDays != null` → `IEventRepository.DeleteOlderThanAsync(appId, cutoff)` → מחיקה ישירה ב-DB → אם נמחקו שורות, `ILogger.LogInformation(...)`.

עדכון `RetentionDays`: `PUT /api/v1/admin/applications/{id}` (Super-Admin) → `IApplicationRepository.UpdateRetentionAsync(id, retentionDays)` → משתקף מיידית ב-run הבא של `RetentionCleanupService` (אין צורך ב-restart).

## 5. בדיקות

- בדיקת יחידה על `IEventRepository.DeleteOlderThanAsync`: מוחק רק שורות ישנות מה-cutoff, רק עבור ה-Application הנתון (Application/Events אחרים לא נפגעים), מחזיר את מספר השורות שנמחקו.
- בדיקת אינטגרציה על `RetentionCleanupService`: tick אחד מוחק כראוי על פני מספר Applications עם ערכי `RetentionDays` שונים, כולל Application אחד עם `RetentionDays=null` שנשאר ללא פגיעה לגמרי.
- בדיקת controller: Super-Admin מצליח לעדכן `RetentionDays`; משתמש לא-Admin נחסם (401/403, תואם למדיניות הקיימת של `ApplicationsController`).
- בדיקת UI: לא נכללת ב-bUnit (הפרויקט אין לו harness כזה) — נבדק ידנית/Playwright אם נדרש בזמן ה-verification, לא כחלק מה-CI האוטומטי.

## 6. מחוץ להיקף

Retention/מחיקה על ישויות שאינן `Event` (Finding/ExceptionGroup/AuditLog וכו'). ארכוב לפני מחיקה. Batching/chunking על מחיקות גדולות. הגדרת ברירת-מחדל גלובלית (retention הוא opt-in בלבד, לא global-with-override). UI כללי לעריכת Application (name/description) — רק `RetentionDays` נוסף כרגע.
