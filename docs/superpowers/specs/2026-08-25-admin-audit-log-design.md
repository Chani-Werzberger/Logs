# Admin Audit Log — Design

**חלק מ:** R&D Logs Platform — V2, קבוצה B (אבטחה), תת-פרויקט B1
**תאריך:** 2026-08-25
**מבוסס על:** `מסמכי-אפיון/10-Security-Design.md` §7 ("Audit כללי לפעולות אדמין — נדחה ל-V2"), `docs/handoff/known-limitations.md` ("RBAC/Audit מלאים")
**Follows:** M0–M7 (V1 המלא) + V2 קבוצה A (OTLP/Logs ingestion), כולם ממוזגים ל-`main`.

## 1. מטרה

לתעד כל פעולת שינוי (Create/Update/Delete/Revoke) שמבצע משתמש Admin דרך מסכי הניהול — מי ביצע, מתי, על איזו ישות, ומה קרה. זה משלים את החריג היחיד שכבר קיים ב-V1 (audit על קידום Hypothesis→Conclusion) לכיסוי כללי של כל פעולות הניהול, כפי שתועד כפער מכוון ב-`10-Security-Design.md` §7 ו-§10.

## 2. היקף

**מתועד:** כל פעולת Create/Update/Delete/Revoke בכל אחד מ-12 ה-controllers תחת `[Authorize(Policy = "RequireAdmin")]`: `ApplicationsController`, `EnvironmentsController`, `ApiKeysController`, `CustomersController`, `AppUsersController`, `LogSourcesController`, `DeploymentsController`, `VersionsController`, `ModulesController`, `ScreenServicesController`, `ProcessesController`, `OperationsController`.

**לא מתועד:** פעולות קריאה (GET), פעולות ingestion (יש להן eventual visibility דרך ה-Events עצמם, לא audit), וקידום Hypothesis→Conclusion (ממשיך להשתמש במנגנון הקיים שלו ב-`FindingStatement.ApprovedBy`/`ApprovedAt` — לא מוחלף).

## 3. ארכיטקטורה

### 3.1 מודל נתונים

טבלה חדשה `AdminAuditLogEntry`:

| שדה | טיפוס | הערות |
|---|---|---|
| `Id` | `long` | PK |
| `PlatformUserId` | `int` | FK ל-`PlatformUser`, `Restrict` (לא מוחקים audit כשמוחקים משתמש) |
| `Timestamp` | `DateTime` | UTC |
| `EntityType` | `string` | שם הישות ("Application", "ApiKey" וכו') |
| `EntityId` | `string?` | מזהה הישות כמחרוזת (מאפשר גם `int` וגם `long` בלי type juggling); `null` רק אם הפעולה נכשלה לפני שנוצר מזהה (לא אמור לקרות בפועל, כי רושמים רק אחרי הצלחה) |
| `Action` | `string` | "Create" / "Update" / "Delete" / "Revoke" |
| `Description` | `string` | משפט טקסט קצר, לדוגמה: `"Created application 'RetailPulse'"` |

**לא כולל** old/new value diffing — רק תיאור טקסטואלי קצר, החלטה מפורשת (ר' §6).

### 3.2 מנגנון הכתיבה

שירות חדש `AuditLogger` (ב-`LogsPlatform.Web.Services`, תואם למוסכמת המיקום של `IngestionProcessor`/`HierarchyResolver` הקיימים) עם מתודה אחת:

```csharp
Task RecordAsync(int platformUserId, string entityType, string entityId, string action, string description);
```

כל אחת מ-12 ה-controllers מזריקה את `AuditLogger` ומוסיפה קריאה אחת בכל מתודת Create/Update/Delete/Revoke — תואם לדפוס הפשוט הקיים (`ApprovedBy`/`ApprovedAt` על `FindingStatement`), רק כטבלה נפרדת כי כאן יש ריבוי סוגי-ישות ולא פעולה בודדת. `platformUserId` מגיע מ-`User.FindFirstValue(ClaimTypes.NameIdentifier)` (או המקבילה — יש לוודא מול הקוד הקיים של `AuthController`/cookie auth בזמן כתיבת התוכנית).

### 3.3 צפייה

- **Endpoint חדש:** `GET /api/v1/admin/audit-log` (מוגן ב-`[Authorize(Policy = "RequireAdmin")]`), עם פרמטרי סינון אופציונליים: `platformUserId`, `entityType`, `action`, `from`, `to`, ו-`page`/`pageSize` — תואם למוסכמת ה-paging הקיימת ב-`GET /api/v1/events`.
- **מסך Admin חדש** (`AuditLogSection.razor`, תחת אותו מבנה כמו שאר סעיפי ה-Admin הקיימים): טבלה עם עמודות Timestamp/משתמש/EntityType/EntityId/Action/Description, עם שדות סינון תואמים לפרמטרי ה-endpoint.

## 4. זרימת נתונים

בקשת Admin (Create/Update/Delete/Revoke) → ה-controller מבצע את הפעולה הרגילה (כמו היום) → **בהצלחה בלבד** → קריאה ל-`AuditLogger.RecordAsync(...)` → שמירה ל-`AdminAuditLogEntry`. אם הפעולה נכשלת (למשל 404/409/ולידציה) — **אין** רישום audit, כי לא קרה שינוי בפועל.

## 5. בדיקות

בדיקות יחידה/אינטגרציה עבור `AuditLogger.RecordAsync` (שמירה נכונה של השדות), ולכל controller — לפחות בדיקה אחת שמוודאת שפעולת Create/Update/Delete/Revoke מוצלחת יוצרת רשומת audit עם `Action`/`EntityType` נכונים, ובדיקה אחת שמוודאת שפעולה כושלת (404/ולידציה) **לא** יוצרת רשומה. בדיקות ל-endpoint הסינון/paging של `GET /api/v1/admin/audit-log`.

## 6. מחוץ להיקף

Old/new value diffing (רק תיאור טקסטואלי, לא צילום-מצב של כל שדה). מדיניות retention/ארכוב ל-`AdminAuditLogEntry` עצמו — נשאר ללא הגבלה, כמו כל שאר הנתונים ב-V1/V2 המוקדם (נדחה יחד עם retention הכללי, קבוצה D). RBAC גרנולרי (קבוצה B2, תת-פרויקט נפרד). TLS enforcement (קבוצה B3, תת-פרויקט נפרד).
