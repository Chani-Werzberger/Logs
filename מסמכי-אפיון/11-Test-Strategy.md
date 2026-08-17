# Test Strategy

**חלק מ:** אפיון פרויקט R&D — מערכת גנרית לניהול, חקירה ואבחון לוגים
**שלב:** תוצר מס' 11 מתוך 12 בסעיף 23 של האפיון המקורי
**תאריך:** 2026-08-17
**מבוסס על:** כל התוצרים הקודמים (04–10), במיוחד [06-מודל-אפליקציה.md](06-מודל-אפליקציה.md) (6 התרחישים), [08-Analysis-ו-Anomaly-Detection.md](08-Analysis-ו-Anomaly-Detection.md) (פרמטרים)

**עיקרון-על (מסעיף 21 באפיון המקורי):** המטרה אינה רק לבדוק שהקוד רץ בלי שגיאה — אלא לבדוק **שהמערכת באמת מזהה את מה שהיינו רוצים שתזהה**, ובאותה מידה, **לא** מזהה חריגה איפה שאין אחת.

## 1. שכבות בדיקה

| שכבה | היקף | פרויקט |
|---|---|---|
| **Unit** | לוגיקה טהורה ללא DB — חישובי z-score, fingerprint hashing, כללי resolution היררכיה, בניית FindingStatement | `LogsPlatform.Tests` |
| **Integration** | API endpoints מול SQL Server אמיתי (LocalDB/מקומי) — ingestion→query round-trip, CRUD היררכיה, auth | `LogsPlatform.Tests` |
| **Scenario (E2E)** | היסטוריה מסונתזת מלאה + 6 תרחישים מוזרקים + ריצת Analysis Engine בפועל + בדיקת ה-Findings שנוצרו | `SyntheticDataGenerator` + `LogsPlatform.Tests` |

## 2. מטריצת כיסוי — לפי סעיף 21 באפיון המקורי

| דרישה | גישת בדיקה | הערה |
|---|---|---|
| Ingestion | Integration: valid/invalid events, batch partial-success, idempotency (`eventKey` כפול), API key תקף/פג/שגוי | |
| Query / Filtering | Integration: כל ממד סינון בנפרד + שילובים, pagination, full-text | |
| Correlation | Integration: Timeline לפי CorrelationId/TraceId/Operation+User/Customer מחזיר סדר נכון; Unit: Deployment/Downstream Correlators מייצרים Hypothesis נכון | |
| Exceptions | Unit: fingerprint (אותו stack+type+template → אותה קבוצה; שונה → קבוצה אחרת); Integration: OccurrenceCount מדויק | |
| Timeline | ראה Correlation | |
| Application hierarchy | Integration: CRUD, rename משמר ייחוס היסטורי, soft-delete חוסם עם היסטוריה / hard-delete מותר בלעדיה (לפי [06-מודל-אפליקציה.md](06-מודל-אפליקציה.md) סעיף 3) | |
| **Multi-application isolation** | Integration: API key של App A **לעולם** לא מחזיר/כותב נתוני App B, גם בבקשות פתולוגיות (ID ניחוש, Application header מזויף) | הערבות הכי קריטית — ראה [10-Security-Design.md](10-Security-Design.md) סעיף 4 |
| Multi-environment behavior | Integration: אותו מבחן, ברמת Environment בתוך אותה Application | |
| Performance | **מוקטן ל-V1** — ראה סעיף 7 | |
| Large volumes | **מוקטן ל-V1** — ראה סעיף 7 | |
| Concurrency | Unit/Integration ממוקד: Analysis Engine לא רץ פעמיים-במקביל אם ריצה קודמת התארכה ("skip if already running"); כתיבות ingestion מקבילות לא משחיתות Baseline/ExceptionGroup counters | לא concurrency ברמת enterprise |
| Failure scenarios | Integration: Ingestion API לא-זמין → Client buffers/drops בלי להפיל את אפליקציית הלקוח ([07-Ingestion-ו-API.md](07-Ingestion-ו-API.md) סעיף 7); Analysis Engine לא נופל על Baseline חסר/לא-בשל | |
| **Baseline learning** | Scenario: Baseline מתכנס נכון מול היסטוריה מסונתזת עם פילוג ידוע; Confidence=Low כש-SampleCount<14 | ליבת סעיף 3 למטה |
| **Anomaly detection** | Scenario: 6 התרחישים המוזרקים מייצרים בדיוק את ה-Findings הצפויים | ליבת סעיף 3 למטה |
| **False positives** | Scenario: ימי "שקט" (התנהגות רגילה) **לא** מייצרים Findings | ליבת סעיף 4 למטה |
| Findings | Unit: מבנה FindingStatement נכון (Kind/Order), dedup לא יוצר כפילויות, Conclusion נוצר **רק** דרך פעולת UI מפורשת — לעולם לא ע"י Analysis Engine | אוכף את [08-Analysis-ו-Anomaly-Detection.md](08-Analysis-ו-Anomaly-Detection.md) סעיף 1 |
| Security | ראה סעיף 6 למטה | |

## 3. Scenario Test — הליבה: מחולל נתוני סימולציה

`SyntheticDataGenerator` בונה היסטוריה של **40 יום** (28 יום מינימום ל-Baseline + מרווח ביטחון) לשתי האפליקציות ([06-מודל-אפליקציה.md](06-מודל-אפליקציה.md)):

1. **ימים 1–35: "שקט"** — פעילות רגילה עם דפוס שעה-ביום עקבי (למשל `ChargePayment`: ~50 קריאות/שעה בשעות עבודה, ~5/שעה בלילה, ~1–2 שגיאות/שעה כרקע טבעי) + רעש אקראי סביר (לא קבוע-מתמטית — כדי לא "לרמות" את חישוב סטיית-התקן).
2. **ימים 36–40: 6 התרחישים מוזרקים**, כל אחד בזמן/מיקום מבוקר (לפי הטבלה ב-[06-מודל-אפליקציה.md](06-מודל-אפליקציה.md) סעיף 4), דרך אותו Ingestion API שלקוח אמיתי היה משתמש בו (לא כתיבה ישירה ל-DB — כדי שהבדיקה תכסה את השרשרת המלאה).

**קריטריוני קבלה (Acceptance):**
- לאחר הרצת Analysis Engine (Baseline Update + Detection), נוצרים **בדיוק 6** Findings חדשים — לא פחות, לא יותר.
- לכל Finding: `Type` נכון, `Scope` (Operation/ExceptionGroup/Customer) נכון, ה-`Fact` statement מכיל את הערך המספרי הנכון (תואם למה שהוזרק בפועל — לא קירוב).
- Confidence=`High` בכל 6 (כי היסטוריית 35 יום > `MIN_SAMPLES`=14).
- ל-Finding של Deployment-Related Anomaly: קיים `FindingStatement[Hypothesis]` שמצטט את ה-Deployment הנכון (Evidence מצביע ל-`Deployment.Id` הנכון).
- ל-Finding של Exception→Downstream: קיים Hypothesis שמצטט את ה-Events המאוחרים הנכונים באותו CorrelationId.

## 4. בדיקת False-Positive — לא פחות חשובה מ-3

**לפני** שמזריקים את 6 התרחישים, מריצים Analysis Engine על ימי ה"שקט" (36–35) בלבד ובודקים: **אפס Findings נוצרים**. זו לא בדיקת-שוליים — היא הוכחה ישירה שה-thresholds ([08-Analysis-ו-Anomaly-Detection.md](08-Analysis-ו-Anomaly-Detection.md) סעיף 7: `SPIKE_THRESHOLD=3`, `MIN_STDDEV_FLOOR`, `MIN_MEANINGFUL_ACTIVITY`) לא רגישים מדי על נתונים בריאים-אך-רועשים. **תוספת מומלצת:** להריץ את מחולל ה"שקט" עם כמה seeds אקראיים שונים (למשל 5 ריצות), ולוודא 0 Findings בכולן — מקרה בודד יכול "להצליח במקרה".

## 5. בדיקת Baseline Learning בפני עצמה

מעבר לבדיקת ה-Scenario המלאה: יחידת-בדיקה ממוקדת שמזינה סדרת דגימות עם ממוצע/סטיית-תקן **ידועים מראש** (לא מסונתזים אקראית) ומוודאת שאלגוריתם ה-Baseline ([08-Analysis-ו-Anomaly-Detection.md](08-Analysis-ו-Anomaly-Detection.md) סעיף 2) מחשב `MeanValue`/`StdDevValue` נכונים בטווח סבירות סטטיסטית, וש-`SampleCount<14` ⇒ `Confidence=Low` נאכף בפועל (לא רק מתועד).

## 6. Security — רשימת בדיקות ממוקדת ([10-Security-Design.md](10-Security-Design.md))

- API key חסר/שגוי/מבוטל → `401`.
- ניסיון גישה ל-Admin endpoints ע"י משתמש `IsAdmin=false` → `403`.
- סיסמה מאוחסנת כ-hash (בדיקת DB ישירה בסביבת טסט — לעולם לא plaintext).
- **בדיקה סטטית:** סריקת repo לפני כל commit (grep-based מספיק ל-V1) שמוודאת שאין connection strings/API keys ב-`appsettings.json`/קוד.
- Redaction hook ([10-Security-Design.md](10-Security-Design.md) סעיף 5): כשמוגדר, מוחל בפועל לפני שליחה מה-Client.

## 7. Performance / Large Volumes / Concurrency — היקף מוקטן, בכוונה ובגלוי

לפי [03-הגדרת-V1.md](03-הגדרת-V1.md), V1 נבדק מול **נתוני סימולציה**, לא production אמיתי. לכן:
- **Performance:** נבדק ברמת "סביר לשימוש עצמאי" — למשל, חיפוש מחזיר תוך <2 שניות מול נפח הסימולציה (~40 יום × 2 אפליקציות, לא מיליוני events). **לא** load-testing enterprise.
- **Large volumes:** נפח הבדיקה הוא "בסדר גודל שמדגים את העיקרון", לא "בסדר גודל production". זהו **פער ידוע ומוצהר**, יתועד ב-Known Limitations, ויוערך-מחדש כשתהיה אפליקציה אמיתית מחוברת (V2+).
- **Concurrency:** נבדק רק התרחיש הריאלי היחיד ל-V1 (Analysis Engine לא רץ כפול, כתיבות ingestion מקבילות לא משחיתות מונים) — לא concurrent-users בקנה מידה גדול.

## 8. Definition of Done ל-V1 — קשור ישירות ל-Success Criteria

תואם ל-6 הקריטריונים ב-[03-הגדרת-V1.md](03-הגדרת-V1.md) סעיף 2. V1 "גמור" כאשר: כל מטריצת הכיסוי (סעיף 2) עוברת, ה-Scenario Test (סעיף 3) עובר עם 6/6 Findings נכונים, בדיקת ה-False-Positive (סעיף 4) עוברת ב-0 Findings שגויים על פני מספר seeds, ובדיקות ה-Security (סעיף 6) עוברות כולן.

---

*המסמך הבא והאחרון בסדרה — תוכנית עבודה ואבני דרך — מתרגם את כל 11 התוצרים (כולל זה) לרצף עבודה בפועל.*
