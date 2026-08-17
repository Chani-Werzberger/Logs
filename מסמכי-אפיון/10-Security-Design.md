# Security Design

**חלק מ:** אפיון פרויקט R&D — מערכת גנרית לניהול, חקירה ואבחון לוגים
**שלב:** תוצר מס' 10 מתוך 12 בסעיף 23 של האפיון המקורי
**תאריך:** 2026-08-17
**מבוסס על:** [04-ארכיטקטורה.md](04-ארכיטקטורה.md) סעיף 9, [03-הגדרת-V1.md](03-הגדרת-V1.md) (היקף אבטחה מוסכם: "אותנטיקציה פשוטה + הפרדה בין Environments")

## 1. מסגרת האיום (Threat Model) — קנה מידה ריאלי ל-V1

זהו כלי R&D פנימי, פיתוח עצמאי, ריצה מקומית, נתוני סימולציה בלבד. **לא** מתוכנן מול תוקף חיצוני מתוחכם או multi-tenant אמיתי. האיומים שכן רלוונטיים גם ב-V1: (א) דליפת secrets לקוד/git, (ב) בלבול בין Environments (למשל Production מוצג בטעות תחת Staging), (ג) חוצן/עובד שלא-אמור-לראות נתונים גולש לאפליקציה/סביבה אחרת דרך API, (ד) מידע רגיש (PII/secrets) שמפתח מזין בטעות ל-Metadata של לוג. אלה ה-4 שהמסמך הזה עונה עליהם ברצינות; איומי enterprise (SSO, DDoS, pentesting) — מפורשות מחוץ לסקופ V1.

## 2. Authentication

| ערוץ | מנגנון |
|---|---|
| Ingestion API | `X-Api-Key` per-Application ([07-Ingestion-ו-API.md](07-Ingestion-ו-API.md) סעיף 2) — מאוחסן כ-`KeyHash` (hash, לא plaintext) בטבלת `ApiKey`; המפתח המלא מוצג פעם אחת בלבד ביצירה ([09-UI-Design.md](09-UI-Design.md) סעיף 7), ניתן לביטול (`RevokedAt`). |
| UI (משתמשי אנוש) | משתמש/סיסמה. סיסמאות **חייבות** hashing (PBKDF2/BCrypt — לא MD5/SHA פשוט, לא plaintext, ללא יוצא מן הכלל). אין self-service signup — משתמשים נוצרים ע"י אדמין קיים. |

## 3. Authorization — המינימום ההגיוני, לא RBAC מלא

**החלטה:** דגל בוליאני יחיד `IsAdmin` על משתמש UI. `IsAdmin=true` → גישה למסך Admin (מבנה אפליקציה, Deployments, API Keys, ניהול משתמשים). `IsAdmin=false` → גישה ל-What's Unusual/Search/Exceptions בלבד, ללא Admin. **אין** הרשאות פר-Application ב-V1 (כל משתמש מאומת רואה את כל ה-Applications) — זו לא "RBAC גרנולרי" (שנשלל במפורש ב-V1), אלא ההבחנה המינימלית בין "יכול לשנות מבנה" ל"יכול לחקור בלבד". **פער ידוע ל-V2:** הרשאות פר-Application/פר-צוות, כשיהיו משתמשים/לקוחות אמיתיים מרובים.

## 4. הפרדת Applications ו-Environments — האכיפה בפועל

זו הערבות האבטחתית **החזקה ביותר** ב-V1, ולכן מקבלת אכיפה בשתי שכבות בלתי-תלויות:
1. **Ingestion:** API key ממופה ל-Application יחיד — אי אפשר לכתוב אירועים "בשם" Application אחר, גם בטעות תכנותית בצד הלקוח.
2. **Query:** כל repository query (מכל endpoint, כולל Analysis Engine עצמו) מחויב לכלול `ApplicationId`+`EnvironmentId` בתנאי הסינון **ברמת קוד ה-repository**, לא רק ברמת ה-UI/controller — כך שגם קריאה עתידית לקוד שתשכח לסנן ב-controller עדיין לא תדלוף בין Environments, כי ה-repository עצמו לא מאפשר שאילתה בלי scope. זו נקודת הבדיקה המרכזית בתוכנית הבדיקות (Multi-application isolation, סעיף 21 באפיון המקורי — Test Strategy הבא בתור יפרט את הבדיקה בפועל).

## 5. מידע רגיש בלוגים (PII/Secrets שמפתח מזין בטעות)

**זהו הפער שהאפיון המקורי דורש (סעיף 16) ושאף מסמך קודם לא טיפל בו ישירות.** הסיכון: `Message`/`MetadataJson`/`StackTrace` הם שדות חופשיים — מפתח/ת שמזין/ה `logger.LogError("Payment failed for card {CardNumber}", card)` יכול/ה להזרים PII/secrets ישירות למערכת בלי שהיא "יודעת" מה זה.

**מדיניות V1:** זיהוי PII אוטומטי (regex/ML לכרטיסי אשראי, מיילים וכו') הוא יכולת אמיתית — אבל **מוצהר במפורש כמחוץ לסקופ V1** (יקר לבנות נכון, קל לייצר false negatives מסוכנים אם נעשה שטחי). **מה כן ב-V1:**
- **Redaction hook ב-Client Library** ([07-Ingestion-ו-API.md](07-Ingestion-ו-API.md) סעיף 7) — נקודת הרחבה (`Func<string, string>` שהמפתח שמחבר את האפליקציה יכול לספק) שמופעלת על `Message`/`Metadata` **לפני** השליחה מהלקוח — כלומר המנגנון קיים, אך המדיניות (מה בדיוק להסתיר) היא באחריות מי שמחבר את האפליקציה, לא ניחוש אוטומטי של המערכת.
- **תיעוד מפורש ל-Known Limitations** (במסירה הסופית): "המערכת אינה מזהה/מסננת PII אוטומטית — באחריות מפתחי האפליקציה המחוברת שלא לשלוח מידע רגיש ב-Message/Metadata, או להשתמש ב-redaction hook."
- **StackTrace** מוצג רק למשתמשי UI מאומתים (לא endpoint ציבורי) — מגן לפחות מפני חשיפה לא-מכוונת כלפי חוץ.

## 6. ניהול Secrets

| סוג secret | איפה מאוחסן |
|---|---|
| Connection string ל-SQL Server | User Secrets (dev) / משתנה סביבה (ריצה) — **לעולם לא** ב-`appsettings.json` שנכנס ל-git |
| API Keys (ingestion) | `KeyHash` בלבד ב-DB; המפתח בפועל נמסר למפתח פעם אחת, המשתמש שומר אותו בצד שלו (למשל בתצורת ה-Client Library) |
| סיסמאות UI | hash מלא (PBKDF2/BCrypt) ב-DB, אף פעם לא plaintext, אף פעם לא לוג |
| `appsettings.Development.json` / secrets בפועל | ב-`.gitignore` מהקומיט הראשון — נבדק לפני כל commit ראשון לרפוזיטורי |

## 7. Audit — מה כן נאסף ב-V1, מה נדחה

**נדחה ל-V2 (כפי שנקבע):** Audit log כללי לכל פעולת אדמין (שינוי מבנה, יצירת משתמש וכו').

**כן נאסף ב-V1 — חריג יחיד ומכוון:** פעולת **"קידום Hypothesis ל-Conclusion"** ([09-UI-Design.md](09-UI-Design.md) סעיף 3, [08-Analysis-ו-Anomaly-Detection.md](08-Analysis-ו-Anomaly-Detection.md) סעיף 1) **תמיד** נרשמת — מי אישר, מתי, ומה הייתה הערת האישור. הנימוק: זו הפעולה היחידה במערכת שבה טענה אנושית הופכת ל"אמת מערכתית" (Conclusion) — היא הכי רגישה מבחינה אפיסטמית, ולכן מקבלת audit גם כש-audit כללי נדחה.

## 8. מדיניות שמירת מידע (Retention/Archival)

**מפורשות מחוץ לסקופ V1** ([03-הגדרת-V1.md](03-הגדרת-V1.md)). **פער ידוע** שיתועד ב-Known Limitations במסירה: אין מחיקה/ארכוב אוטומטיים — נתוני V1 (סימולציה) לא צפויים לצבור נפח שמצדיק את זה. המלצה ל-V2: job מחזורי שמוחק/מארכב Events מעבר לגיל מוגדר (configurable, per-Application).

## 9. תעבורה (Transport)

V1 רץ מקומית — HTTP מקומי מקובל לפיתוח. **דגל אדום לתיעוד:** ברגע שיש deployment אמיתי (מעבר ל-`localhost`), TLS הוא **תנאי סף**, לא "נחמד שיהיה" — יתועד כדרישת-חסימה ב-Known Limitations/V2, לא יישכח.

## 10. סיכום — מה בפנים, מה בחוץ (V1)

| נושא | סטטוס |
|---|---|
| Authentication (API key + user/password) | ✅ |
| Authorization בסיסי (Admin/non-Admin) | ✅ |
| הפרדת Application/Environment באכיפת query-layer | ✅ — הערבות המרכזית |
| Redaction hook ב-Client (לא זיהוי PII אוטומטי) | ✅ מנגנון, ❌ מדיניות-אוטומטית |
| Secrets מחוץ לקוד | ✅ |
| Audit של קידום Hypothesis→Conclusion | ✅ (חריג יחיד) |
| Audit כללי לפעולות אדמין | ❌ נדחה ל-V2 |
| RBAC גרנולרי / הרשאות פר-Application | ❌ נדחה ל-V2 |
| מדיניות שמירה/ארכוב | ❌ נדחה ל-V2 |
| TLS אכיפה | ❌ לא רלוונטי לריצה מקומית — ידרש ב-V2 |

---

*המסמך הבא — Test Strategy — יגדיר איך בפועל בודקים את סעיף 4 (בידוד Multi-application) ואת שאר הדרישות מסעיף 21 באפיון המקורי.*
