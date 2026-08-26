# Per-Application RBAC — Design

**חלק מ:** R&D Logs Platform — V2, קבוצה B (אבטחה), תת-פרויקט B2
**תאריך:** 2026-08-25
**מבוסס על:** `מסמכי-אפיון/10-Security-Design.md` §3 ("אין הרשאות פר-Application ב-V1... פער ידוע ל-V2: הרשאות פר-Application/פר-צוות, כשיהיו משתמשים/לקוחות אמיתיים מרובים"), `docs/handoff/known-limitations.md` ("RBAC/Audit מלאים")
**Follows:** M0–M7 (V1 המלא) + V2 קבוצה A (OTLP) + V2 קבוצה B1 (Audit Log, כולל תיקון חיווט ה-UI), כולם ממוזגים ל-`main`.

## 1. מטרה

כרגע דגל בוליארי יחיד (`PlatformUser.IsAdmin`) קובע גישת ניהול לכל האפליקציות — מי שהוא Admin רואה/מנהל את **כל** האפליקציות, אין דרך להעניק למישהו הרשאת ניהול על אפליקציה ספציפית בלבד. זה בדיוק הפער שתועד מראש כ"נדחה ל-V2". המטרה: לאפשר להעניק למשתמש/ת ניהול (לא Super Admin) גישת-ניהול לאפליקציות ספציפיות בלבד.

## 2. היקף

**מוגן (דורש הרשאה):** כל פעולת שינוי (Create/Update/Deactivate/Revoke) על ישות השייכת לאפליקציה — בדיוק אותן 11 ישויות/12 controllers ו-11 קומפוננטות Blazor שקושרו ל-audit log ב-B1: `AppEnvironment`, `ApiKey`, `Customer`, `AppUser`, `LogSource`, `Deployment`, `AppVersion`, `AppModule`, `ScreenService`, `ProcessNode`, `Operation`.

**נשאר Super-Admin-בלבד (לא ניתן להעניק כהרשאה פר-אפליקציה):** יצירת Application חדשה (`ApplicationsController.Create` — פעולה גלובלית מטבעה, אין עדיין אפליקציה קיימת להעניק גישה עליה), ניהול משתמשי-מערכת (`PlatformUsersSection.razor`), צפייה ב-Audit Log המלא (`/admin/audit-log`).

**לא מוגן (נשאר פתוח כפי שהוא היום):** כל פעולות הקריאה/חקירה — "מה חריג", חיפוש, חריגות, `GET /api/v1/events`, `GET /api/v1/findings` וכו'. **החלטה מפורשת:** כל משתמש/ת מאומת/ת ממשיך/ה לראות/לחקור את **כל** האפליקציות, ללא קשר להרשאות ניהול. RBAC כאן חל רק על שינוי, לא על צפייה.

## 3. ארכיטקטורה

### 3.1 מודל נתונים

טבלה חדשה `PlatformUserApplicationGrant` — גישה בינארית בלבד (אין רמות הרשאה כמו Admin/Viewer, כי צפייה כבר פתוחה לכולם — ההרשאה היחידה שיש לתת היא "יכול/ה לנהל אפליקציה זו"):

| שדה | טיפוס | הערות |
|---|---|---|
| `Id` | `int` | PK |
| `PlatformUserId` | `int` | FK ל-`PlatformUser`, `Cascade` (אם נמחק המשתמש, אין טעם בהרשאה שלו) |
| `ApplicationId` | `int` | FK ל-`Application`, `Cascade` (אם נמחקת האפליקציה, אין טעם בהרשאה עליה) |

אינדקס ייחודי על `(PlatformUserId, ApplicationId)` — אין טעם בשורה כפולה.

### 3.2 מנגנון האכיפה

שירות חדש `IApplicationAccessService` עם מתודה אחת:

```csharp
Task<bool> CanManageApplicationAsync(int platformUserId, int applicationId);
```

לוגיקה: `true` אם `PlatformUser.IsAdmin == true` (Super Admin עוקף הכל), **או** אם קיימת שורת `PlatformUserApplicationGrant` תואמת. אחרת `false`.

כל פעולת שינוי בכל אחד מ-12 ה-controllers (למעט `ApplicationsController.Create`) וכל אחת מ-11 קומפוננטות ה-Blazor המקבילות קוראת לבדיקה הזו **לפני** ביצוע השינוי, עם ה-`applicationId` הרלוונטי. עבור controllers/קומפוננטות שהמסלול שלהם לא כולל `appId` ישירות (`ScreenServicesController`/`ProcessesController`/`OperationsController` ומקביליהם ב-UI, שמנווטים דרך `moduleId`/`screenServiceId`/`processId`) — הבדיקה הולכת בעקבות שרשרת-הבעלות הקיימת שאותם controllers כבר עוברים בה היום (למשל: כדי לבדוק ש-`ProcessNode` שייך ל-`ScreenServiceId` הנתון, ה-controller כבר טוען את ה-`ScreenService`; אותה טעינה חושפת גם את ה-`ModuleId` שלו, וממנו את ה-`ApplicationId`) — כלומר לא נדרשות שאילתות נוספות ל-DB מעבר למה שכבר קורה, רק שרשור של המידע שכבר נטען.

כישלון בבדיקה: ב-controllers — `403 Forbidden`. ב-Blazor — הודעת שגיאה inline, באותה מוסכמה של `_createError`/`_editError` הקיימת בכל הקומפוננטות.

### 3.3 ניהול הרשאות (UI)

הרחבת `PlatformUsersSection.razor` הקיים: לכל `PlatformUser` ברשימה, Super Admin רואה רשימת-בחירה (checkboxes) של כל האפליקציות הקיימות, מסמן/ת אילו מהן מוענקות למשתמש/ת הזו. שינוי בבחירה מוסיף/מוחק שורות `PlatformUserApplicationGrant` בהתאם. אין endpoint API נפרד — הקומפוננטה פונה ישירות ל-repository, תואם למוסכמה הקיימת בכל שאר קומפוננטות ה-Admin בפרויקט הזה.

## 4. זרימת נתונים

בקשת שינוי (controller או Blazor) → פתרון ה-`applicationId` הרלוונטי (ישיר מהמסלול, או דרך שרשרת-בעלות קיימת) → `IApplicationAccessService.CanManageApplicationAsync(currentPlatformUserId, applicationId)` → אם `false`: `403`/הודעת שגיאה, הפעולה לא מתבצעת. אם `true`: הפעולה ממשיכה כרגיל (כולל רישום ל-Audit Log כפי שכבר קיים מ-B1).

## 5. בדיקות

בדיקת יחידה ל-`IApplicationAccessService.CanManageApplicationAsync` (Super Admin תמיד `true`, משתמש עם grant מתאים `true`, משתמש בלי grant `false`). לכל אחד מ-4 קבוצות ה-controllers (מקביל לחלוקה מ-B1): בדיקה אחת שמוודאת ש-Super Admin יכול לבצע פעולת שינוי, בדיקה אחת שמוודאת שמשתמש-ניהול עם grant יכול, ובדיקה אחת שמוודאת שמשתמש-ניהול בלי grant מקבל 403 — לא בדיקה נפרדת לכל controller בקבוצה (תואם למוסכמת "כיסוי מייצג, לא מלא" שכבר יושמה ב-B1). בדיקה אחת שמוודאת שפעולות קריאה/חקירה (`GET /api/v1/events`) **לא** נחסמות למשתמש בלי grant.

## 6. מחוץ להיקף

חסימת פעולות קריאה/חקירה לפי הרשאה (הוחלט במפורש להשאיר פתוח). רמות הרשאה מרובות (Admin/Viewer/וכו' פר-אפליקציה) — הרשאה בינארית בלבד. הרשאות פר-צוות/קבוצת-משתמשים (המסמך המקורי הזכיר "פר-צוות" כאפשרות עתידית — לא כלול כאן). TLS/HTTPS enforcement (קבוצה B3, תת-פרויקט נפרד, עדיין נדחה בהיעדר סביבת פריסה אמיתית).
