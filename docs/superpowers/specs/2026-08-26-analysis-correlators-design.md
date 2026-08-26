# Advanced Correlators — Design

**חלק מ:** R&D Logs Platform — V2, קבוצה C (ניתוח מתקדם)
**תאריך:** 2026-08-26
**מבוסס על:** `מסמכי-אפיון/08-Analysis-ו-Anomaly-Detection.md` (הגלאים/הקורלטורים הקיימים ב-V1, סעיפים 3-4)
**Follows:** M0–M7 (V1 המלא) + V2 קבוצה A (OTLP) + V2 קבוצה B (B1 audit log, B2 per-application RBAC), כולם ממוזגים ל-`main`.

## 1. מטרה

V1 מספק 4 גלאים (Rate Anomaly, New Exception, Customer Outlier) ו-2 קורלטורים (Deployment, Downstream Failure) — מכסים את 6 התרחישים המקוריים, בכוונה פשוטים (z-score, ללא ML/seasonality). המטרה כאן: להוסיף שלושה קורלטורים חדשים, מרחיבים את יכולת ה-Hypothesis-building של המנוע מבלי לשנות את עיקרון-העל ("המנוע לעולם לא כותב Conclusion אוטומטית" — נשאר בתוקף ללא שינוי).

## 2. שלושת הקורלטורים החדשים

### 2.1 Upstream-Cause Correlator

מראה-ראי מדויקת של ה-Downstream Failure Correlator הקיים, בכיוון ההפוך בזמן:

```
לכל Finding חדש מסוג NewException/ErrorSpike, עם triggerEvent ידוע (CorrelationId, OperationId, Timestamp):
  related_events = Events עם אותו CorrelationId, ב-Timestamp מוקדם מ-triggerEvent.Timestamp, על Operation אחר, Severity>=Error
  אם נמצאו:
    הוסף Evidence (EvidenceType=Event) לכל אחד
    הוסף FindingStatement(Hypothesis,
      "לפני אירוע זה, נרשמו {N} שגיאות קודמות באותה שרשרת (CorrelationId), ב-{M} Operation-ים אחרים.
       ייתכן שזהו הגורם המקורי לאירוע זה — לא אושר.")
```

**החלטה מפורשת (אושרה):** אין מודל dependency-graph חדש בין Operations — רק שרשור כרונולוגי לפי `CorrelationId`, בדיוק כמו ה-Downstream Correlator הקיים. שומר על עקרון הפשטות של V1.

**חיווט:** מכיוון שהקורלטור צריך מידע ברמת ה-Event הגולמי (CorrelationId, OperationId המפעיל, Timestamp מדויק) שלא קיים ב-`Finding` עצמו, הוא מוזרק ל-`RateAnomalyDetector` ול-`NewExceptionDetector` ונקרא **לצד** הקריאה הקיימת ל-`DownstreamFailureCorrelator.RunAsync(...)` — לא נקודת-קריאה חדשה, אותה נקודה בדיוק, קורלטור שני.

### 2.2 Concurrent-Finding Correlator

```
לכל Finding חדש:
  others = Findings אחרים עם Status IN (New, Acknowledged), על אותה Application (בכל Environment), למעט ה-Finding הנוכחי עצמו
  אם count(others) >= 1:
    הוסף Evidence (EvidenceType=Finding, ReferenceId=other.Id) לכל אחד מ-others
    הוסף FindingStatement(Hypothesis,
      "{N} Finding-ים נוספים פתוחים כרגע על אפליקציה זו. ייתכן שיש להם גורם משותף — לא אושר.")
```

**החלטות מפורשות (אושרו, לא ברירת המחדל המומלצת שלי):** היקף = כל ה-Application (לא מוגבל ל-Environment בודד), ללא חלון-זמן (כל Finding פתוח נחשב, לא רק כאלה שהתגלו לאחרונה), סף = Finding פתוח אחד ומעלה מספיק.

### 2.3 Recurrence Correlator

```
לכל Finding חדש:
  prior = ה-Finding האחרון ביותר (DetectedAt מקסימלי) עם Status IN (Resolved, Dismissed),
          התואם באותו מפתח-dedup שכבר קיים ב-V1: (ApplicationId, EnvironmentId, ScopeType, ScopeId, Type)
  אם נמצא:
    הוסף Evidence (EvidenceType=Finding, ReferenceId=prior.Id)
    הוסף FindingStatement(Hypothesis,
      "נראה שזוהי הישנות של בעיה קודמת שנפתרה/נדחתה ({prior.Status}, זוהתה ב-{prior.DetectedAt}) — לא אושר שזהו אותו גורם-שורש.")
```

**החלטה מפורשת (אושרה):** מתאים רק ל-Finding הקודם האחרון ביותר באותו מפתח, לא כל ההיסטוריה — שומר על רשימת Evidence קצרה וממוקדת.

## 3. שינויים תומכים

- **`EvidenceType` enum** (`Evidence.cs`) — הוספת ערך `Finding`, לצד `Event`/`ExceptionGroup`/`Deployment`/`Baseline`/`Operation` הקיימים.
- **`IFindingRepository`** — שתי מתודות חדשות:
  - `Task<IReadOnlyList<Finding>> GetOtherOpenFindingsForApplicationAsync(int applicationId, long excludeFindingId)`
  - `Task<Finding?> FindMostRecentClosedAsync(int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, FindingType type, long excludeFindingId)`
- **`FindingDetail.razor`** — ענף `else if` נוסף עבור `EvidenceType.Finding`, מקשר ל-`/findings/{referenceId}` (תואם לתבנית הקיימת של קישור-לפי-סוג).
- **`Program.cs`** — רישום DI לשלוש המחלקות החדשות (`AddScoped`), והוספתן כפרמטרים בבנאים של `RateAnomalyDetector`/`NewExceptionDetector` (Upstream) ו-`AnalysisEngineTickRunner` (Concurrent + Recurrence).

## 4. זרימת נתונים

**Upstream:** זהה בדיוק לזרימת ה-Downstream הקיימת, רק הפוכה בכיוון הזמן — מופעל מתוך הגלאי (RateAnomalyDetector/NewExceptionDetector) מיד לאחר כתיבת Finding חדש, כשה-triggerEvent כבר ידוע.

**Concurrent + Recurrence:** זהה בדיוק לזרימת ה-Deployment Correlator הקיימת — `AnalysisEngineTickRunner.RunForApplicationEnvironmentAsync` שולף את ה-Findings שהתגלו ב-tick הנוכחי (`GetDetectedSinceAsync`) ומריץ את שני הקורלטורים החדשים על כל אחד, לצד ה-Deployment Correlator הקיים.

## 5. בדיקות

בדיקת יחידה לכל קורלטור חדש (בדיוק כמו `DownstreamFailureCorrelatorTests.cs`/`DeploymentCorrelator`'s own test coverage): מקרה שבו התנאי מתקיים (Evidence+Statement נוספים), ומקרה שבו לא (Finding לא רלוונטי מבחינת type, או שאין Findings/Events תואמים). בדיקת אינטגרציה אחת ב-`AnalysisEngineTickRunnerTests.cs` (מקבילה לזו הקיימת ל-Deployment Correlator) שמוודאת שקורלטור אחד לפחות מהשניים החדשים (Concurrent/Recurrence) באמת מופעל דרך ה-tick runner המלא, לא רק כיחידה מבודדת.

## 6. מחוץ להיקף

מודל dependency-graph מפורש בין Operations (ל-Upstream, נדחה בכוונה). חלון-זמן ל-Concurrent Correlator (נדחה — נבחר "ללא חלון" במפורש). היסטוריית-הישנות מלאה ב-Recurrence Correlator (נדחה — רק ה-Finding האחרון ביותר). כל שיפור לחישוב ה-Baseline עצמו (bucketing/seasonality/adaptive thresholds) — הוחלט מפורשות שהמיקוד כאן הוא קורלטורים, לא Baseline.
