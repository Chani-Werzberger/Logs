# What Was Done / What Remains

מיפוי מלא של 25 סעיפי האפיון המקורי (המסמך המלא, לא `03-הגדרת-V1.md` המצומצם) מול המימוש בפועל, נכון ל-2026-08-24. מקרא: ✅ הושלם · 🟡 הושלם חלקית · ❌ לא בוצע · ⏭️ נדחה במפורש.

| # | סעיף | מצב | הערות |
|---|---|---|---|
| 1 | מטרת הפרויקט | ✅ | המוצר הגנרי, Application-Aware, עם מנוע אבחון עצמאי — כולם קיימים ונבדקו ב-M5 (go/no-go). |
| 2.1 | מוצר גנרי | ✅ | נבדק מול 2 אפליקציות מדומות שונות (RetailPulse, FieldOps) ב-M5. |
| 2.2 | Application Awareness (היררכיה 5 שכבות) | ✅ | Application→Module→ScreenService→Process→Operation, מנוהל דרך UI/API ללא שינוי קוד (M1). |
| 2.3 | Fact מול Hypothesis/Conclusion | ✅ | `FindingStatementKind` (Fact/Observation/Hypothesis/Conclusion); המנוע אינו יכול לכתוב Conclusion — אכיפה ברמת קומפילציה (M4a); קידום ל-Conclusion דורש פעולת אדם מאומתת (M6). |
| 3 | מחקר מקדים | ✅ | מתועד ב-[01-מחקר-כלים-קיימים](../../מסמכי-אפיון/01-מחקר-כלים-קיימים/) — Seq/Sentry/App Insights/Grafana-Loki/OpenTelemetry, פער שוק, מסקנות. |
| 4 | אפיון Application | ✅ | Applications/Environments/Versions/Deployments/Modules/Screens/Services/Processes/Operations/LogSources/Customers — כולם דרך Admin UI+API (M1, B1-B3). |
| 5 | קליטת לוגים — Ingestion | ✅ | HTTP API + `LogsPlatform.Client`/Serilog sink; כל השדות המבוקשים קיימים ב-`EventPayload`/`IngestEventRequest`; ההבחנות Event/Operation/Request/Exception/Trace-Correlation מתועדות ב-[07-Ingestion-ו-API.md](../../מסמכי-אפיון/07-Ingestion-ו-API.md) §1. |
| 6 | ניהול וחקר לוגים (חיפוש) | 🟡 | רוב הסינונים קיימים (`EventQueryParameters`: זמן, Application, Environment, Severity, Module, Screen/Service, Process, Operation, User, Customer, Exception, Version, CorrelationId, TraceId, Duration, טקסט חופשי בהודעה). **חסר**: סינון לפי Deployment ספציפי, סינון לפי תוכן Metadata (רק Message). |
| 7 | Timeline | ✅ | `/timeline` + `TimelineQuery` (Application, CorrelationId, TraceId, Operation, User, Customer) — M3. |
| 8 | ניהול Exceptions | 🟡 | קיבוץ לפי fingerprint, כמות מופעים (**תוקן היום** — היה שבור מ-M2a ולא התעדכן על מופעים חוזרים), מועד ראשון/אחרון, מגמה יומית, Stack trace, Applications/Environments/Versions/Operations מושפעים. **חסר**: פירוט לפי Module/Service, לפי Customer ספציפי. |
| 9 | Deployment Awareness | ✅ | Deployment/AppVersion + `DeploymentCorrelator` (Deployment→Error Spike, עם ראיות), נבדק תרחיש מלא ב-M5. |
| 10 | מנגנון אבחון עצמאי — What's Unusual | ✅ | **זו אבן-הדרך go/no-go (M5)** — 6/6 תרחישי החריגה המחויבים (Error Spike, Performance Degradation, New Exception, Deployment-Related, Missing Activity, Customer-Specific) מזוהים נכון, 0 false positives על מספר seeds. |
| 11 | Correlation Analysis | 🟡 | נבנו ונבדקו: Deployment→Error Spike, Exception→כשל מאוחר יותר ב-workflow (`DownstreamFailureCorrelator`, M4b). **לא נבנו**: Customer→Pattern כקורלציה נפרדת (יש Detector לחריגות Customer, לא קורלציה), מספר Services→תשתית משותפת, ירידת פעילות→שגיאות קודמות. |
| 12 | Baseline | 🟡 | `BaselineCalculator` — חלון נע 28 יום, ממוצע/סטיית-תקן, bucketing לפי שעה-ביום, מפחית False Positives (0 FP מאומת ב-M5). **לא כולל** עונתיות יום-בשבוע או ML — החלטה מודעת, ראו [Known Limitations](known-limitations.md). |
| 13 | What's Unusual — תצוגה | ✅ | `Home.razor` הוא מסך What's Unusual; כל שדה מבוקש (What/When/Where/Severity/Confidence/Evidence/Fact-Observation-Hypothesis) מוצג (M4b). |
| 14 | חקירה מתוך Finding (Drill-down) | 🟡 | Drill-down לאירועים מקוריים/Timeline/Exceptions/Deployments עובד (M4b). **חסר**: קישור ישיר בין Findings קשורים זה לזה. |
| 15 | ממשק ניהול מבנה האפליקציה | ✅ | הוספה/שינוי/ארגון היררכיה מלאה דרך Admin UI, ללא שינוי קוד. |
| 16 | אבטחה | 🟡 | Authentication (cookie+API key), App/Environment isolation ברמת ה-repository, redaction hook, סריקת secrets — כולם קיימים (M6). Authorization היא בינארית (`IsAdmin`) לא RBAC גרנולרי — תואם את ההיקף שהוגדר. Audit כללי ומדיניות retention/ארכוב — לא בוצעו, ראו [Known Limitations](known-limitations.md). |
| 17 | V1 (מטרת ה-value proposition) | ✅ | הזרימה "קל לחבר → קל לקלוט → קל לחפש → קל להבין הקשר → המערכת מזהה חריגות בעצמה" הוכחה מקצה לקצה ב-M5 (סינתטי) וגם היום (אפליקציה חיצונית אמיתית). |
| 18 | ארכיטקטורה | ✅ | מתועדת מראש ב-[04-ארכיטקטורה.md](../../מסמכי-אפיון/04-ארכיטקטורה.md), Modular Monolith מומש. סטייה קלה מהתכנון: מנוע האבחון מומש בתוך `LogsPlatform.Web/Services/Analysis/` ולא כפרויקט `.Analysis` נפרד כפי שתוכנן במקור. |
| 19 | מודל נתונים | ✅ | מתועד מראש ב-[05-מודל-נתונים.md](../../מסמכי-אפיון/05-מודל-נתונים.md); כל הישויות המרכזיות מומשו. Correlations/Traces מומשו כעמודות על `Event` (`CorrelationId`/`TraceId`/`SpanId`) ולא כטבלה נפרדת — בחירת מידול מכוונת. |
| 20 | API | 🟡 | Ingestion, ניהול מבנה, Deployment, Query, Filtering, Findings, Authentication, Validation, Error handling (ProblemDetails), Versioning (`/api/v1/`), Idempotency (`EventKey`), Rate limiting — כולם קיימים. **חסר**: נקודת קצה ל-Health/status. |
| 21 | בדיקות | 🟡 | 326 בדיקות, SQL Server אמיתי (לא Mock/InMemory) — מכסות ingestion/query/filtering/correlation/exceptions/timeline/היררכיה/בידוד multi-app/multi-environment/concurrency/תרחישי כשל/למידת Baseline/זיהוי חריגות/False Positives/Findings/אבטחה. תרחישי סימולציה לכל 6 סוגי החריגה קיימים ומאומתים (M5). **חסר**: סוויטת ביצועים/עומס ייעודית מעבר למה שנבדק אגב תרחיש ה-M5. |
| 22 | מסמכי מסירה | ✅ | זהו התוצר של M7 עצמו — README מורחב, Known Limitations, מסמך זה. |
| 23 | תוצרי האפיון לפני קוד | ✅ | כל 12 מסמכי-האפיון נכתבו ואושרו לפני קוד; כל אבן-דרך (M1–M7) עברה מחזור עיצוב→אישור→תוכנית→ביצוע דרך Claude, בהתאם לנוהל. |
| 24 | נוהל העבודה | 🟡 | עבודה עצמאית, commits מסודרים, בדיקות מקיפות, תיעוד שוטף, Git — כולם בוצעו לאורך כל הפרויקט. דיווח סטטוס שבועי נמסר פעם אחת (כטקסט בשיחה, לא נשלח במייל בפועל דרך המערכת) — זו התחייבות ניהולית של המפתחת מול הפרויקט, לא רכיב תוכנה. |
| 25 | מטרת העל (זרימת 8 השלבים) | ✅ | כל 8 השלבים (הגדרת Application → הגדרת היררכיה → חיבור מקור לוגים → קליטה → חיפוש/חקירה → זיהוי אוטומטי → What's Unusual עם ראיות → Drill-down) הודגמו מקצה לקצה — גם בתרחיש הסינתטי (M5) וגם היום מול אפליקציה חיצונית אמיתית. |

## המלצות להמשך

1. **תמיכת OTLP כערוץ קליטה נוסף** — לא auto-instrumentation מאפס (OpenTelemetry כבר מספקת agents לכך), אלא Collector+adapter שמתרגם OTLP לפורמט הפנימי, כפי שכבר צוין ב-[07-Ingestion-ו-API.md](../../מסמכי-אפיון/07-Ingestion-ו-API.md) כהמלצת V2. יתרון: אפליקציות שכבר מותקנות עם OpenTelemetry SDK משלהן יוכלו להתחבר בלי לאמץ את ה-SDK הקנייני.
2. **RBAC גרנולרי + Audit כללי** — כבר מתועד כפריט V2 ב-[10-Security-Design.md](../../מסמכי-אפיון/10-Security-Design.md) §6-7.
3. **חיבור אפליקציות נוספות** — כדי לוודא שהגנריות אכן מחזיקה מעבר לשתי הסימולציות המקוריות ולאפליקציה החיצונית הראשונה.
4. **השלמת סוגי הקורלציה החסרים** (סעיף 11 לעיל) ו-endpoint ל-health/status (סעיף 20 לעיל) — פערים קטנים, ממוקדים, לא דורשים שינוי ארכיטקטוני.
