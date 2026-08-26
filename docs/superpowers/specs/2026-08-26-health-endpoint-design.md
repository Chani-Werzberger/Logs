# Health-Check Endpoint — Design

**חלק מ:** R&D Logs Platform — V2, קבוצה D (תפעולי), תת-פרויקט D1
**תאריך:** 2026-08-26
**Follows:** M0–M7 (V1 המלא) + V2 קבוצה A (OTLP) + V2 קבוצה B (B1 audit log, B2 per-application RBAC) + V2 קבוצה C (advanced analysis correlators), כולם ממוזגים ל-`main`.

## 1. מטרה

אין כיום שום נקודת קצה שמדווחת על בריאות המערכת — לא על זמינות ה-DB, ולא על כך שה-Analysis Engine (רץ ברקע כל 5 דקות) עדיין פועל בפועל ולא "נתקע" בשקט. המטרה: נקודת קצה אחת, `GET /api/v1/health`, שמדווחת על שני הדברים האלה.

## 2. היקף

**כלול:** בדיקת קישוריות SQL Server (שאילתה קלה, מדידת זמן תגובה), ודיווח על מתי ה-Analysis Engine סיים בהצלחה tick אחרון (staleness detection).

**לא כלול (הוחלט במפורש):** ללא anonymous access — הנקודה נשארת מאחורי אותה מדיניות ברירת-מחדל של אימות שכל שאר האפליקציה מחייבת (`RequireAuthenticatedUser()`), למרות שזו לא הנוהג המקובל לכלי ניטור חיצוניים — החלטה מודעת לשמור על העמדה הבטוחה-כברירת-מחדל של הפרויקט, כל עוד אין עדיין יעד פריסה אמיתי (B3/TLS עדיין נדחה מאותה סיבה). retention policy (D2) הוא תת-פרויקט נפרד, לא כלול כאן.

## 3. ארכיטקטורה

### 3.1 מעקב אחר Analysis Engine

מחלקה חדשה, singleton, `AnalysisEngineHealthStatus`:

```csharp
public class AnalysisEngineHealthStatus
{
    private readonly object _lock = new();
    private DateTime? _lastTickCompletedAt;

    public void RecordTickCompleted(DateTime completedAtUtc)
    {
        lock (_lock) { _lastTickCompletedAt = completedAtUtc; }
    }

    public DateTime? LastTickCompletedAt
    {
        get { lock (_lock) { return _lastTickCompletedAt; } }
    }
}
```

`AnalysisEngineBackgroundService.TryRunOneTickAsync` קורא ל-`RecordTickCompleted(DateTime.UtcNow)` מיד אחרי `await runner.RunOneTickAsync()` המצליח (בתוך אותו `try`, לפני ה-`finally` שמשחרר את דגל ה-`_isRunning`). נרשם רק tick שהצליח — tick שנזרק חריגה לא מעדכן את הזמן (זה בדיוק מה שאמור לגרום ל"stale" בסופו של דבר, אם החריגות ממשיכות).

### 3.2 בדיקת DB

שאילתה קלה מול `LogsPlatformDbContext` (`SELECT 1`, או שקול — לדוגמה `await _context.Database.CanConnectAsync()` שכבר קיים ב-EF Core), עם מדידת זמן תגובה ב-milliseconds.

### 3.3 סיווג בריאות

| מצב | תנאי |
|---|---|
| DB: Healthy | השאילתה הצליחה |
| DB: Unhealthy | השאילתה נכשלה (timeout/exception) |
| Analysis Engine: Unknown | `LastTickCompletedAt == null` (האפליקציה עלתה זה עתה, אין עדיין tick ראשון) |
| Analysis Engine: Healthy | `LastTickCompletedAt` בתוך 15 הדקות האחרונות (פי 3 מתקופת ה-tick של 5 דקות) |
| Analysis Engine: Stale | `LastTickCompletedAt` ישן יותר מ-15 דקות |

**Overall status:** `Unhealthy` אם DB=Unhealthy **או** Analysis Engine=Stale. `Unknown` עבור ה-Analysis Engine **לא** גורם ל-Unhealthy הכללי (עליית אפליקציה טרייה היא מצב תקין, לא כשל).

### 3.4 תגובת ה-endpoint

```json
{
  "status": "Healthy",
  "database": { "status": "Healthy", "responseTimeMs": 12 },
  "analysisEngine": { "status": "Healthy", "lastTickCompletedAt": "2026-08-26T14:05:00Z", "secondsSinceLastTick": 42 }
}
```

`lastTickCompletedAt`/`secondsSinceLastTick` הם `null` כש-status=`Unknown`. קוד HTTP: `200` כש-overall Healthy, `503 Service Unavailable` כש-Unhealthy.

## 4. זרימת נתונים

בקשה ל-`GET /api/v1/health` → `HealthController` מריץ בו-זמנית את בדיקת ה-DB ואת קריאת `AnalysisEngineHealthStatus.LastTickCompletedAt` → מחשב סיווג לכל רכיב ואת ה-overall status → מחזיר JSON + קוד HTTP מתאים.

## 5. בדיקות

- בדיקת אינטגרציה (`TestWebApplicationFactory`, תואם למוסכמה הקיימת): DB זמין + tick עדכני → 200, overall Healthy.
- בדיקת אינטגרציה: אין עדיין tick (`AnalysisEngineHealthStatus` טרי, ברירת מחדל `null`) → 200, `analysisEngine.status=Unknown`, overall עדיין Healthy.
- בדיקת יחידה על `AnalysisEngineHealthStatus` עצמה: `RecordTickCompleted` ואז `LastTickCompletedAt` מחזיר את הערך; קריאה לפני `RecordTickCompleted` מחזירה `null`.
- בדיקת יחידה/אינטגרציה שמדמה tick ישן (הזרקת `AnalysisEngineHealthStatus` עם timestamp ישן ידני ל-container של הטסט) → מוודאת סיווג `Stale` ו-overall `Unhealthy` + קוד 503.
- בדיקת DB-down לא נכללת: הפרויקט לא משתמש ב-mocks בשום מקום (תמיד SQL Server אמיתי), ואין דרך פשוטה לכבות את ה-DB האמיתי רק לצורך בדיקה אחת בלי לפגוע בבדיקות המקבילות באותה מסד. מוסכמה מודעת, לא פער — הכיסוי היחיד לנתיב הזה הוא קריאת הקוד עצמו (try/catch סביב `CanConnectAsync`).

## 6. מחוץ להיקף

Retention policy (D2, תת-פרויקט נפרד). Anonymous access לנקודת הבריאות (הוחלט במפורש להשאיר מאחורי אימות). ניטור חיצוני/alerting אמיתי (Prometheus/Grafana וכו') — אין עדיין תשתית לזה, ולא התבקש.
