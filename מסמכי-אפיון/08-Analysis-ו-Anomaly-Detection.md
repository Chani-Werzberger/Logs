# Analysis / Anomaly Detection Design

**חלק מ:** אפיון פרויקט R&D — מערכת גנרית לניהול, חקירה ואבחון לוגים
**שלב:** תוצר מס' 8 מתוך 12 בסעיף 23 של האפיון המקורי
**תאריך:** 2026-08-17
**מבוסס על:** [05-מודל-נתונים.md](05-מודל-נתונים.md) (טבלאות Baseline/Finding/FindingStatement/Evidence), [06-מודל-אפליקציה.md](06-מודל-אפליקציה.md) (2 האפליקציות ו-6 התרחישים)

זהו התוצר המרכזי — כאן ההבדלה התחרותית שהמחקר מצא ([02-פער-שוק.md](01-מחקר-כלים-קיימים/02-פער-שוק.md)) הופכת ללוגיקה ממשית.

## 1. עקרון-העל: מי מותר לו לכתוב Conclusion

**ה-Analysis Engine לעולם לא כותב `FindingStatement` מסוג `Conclusion` באופן אוטומטי.** המנוע כותב רק `Fact` (מדידה), `Observation` (חריגה שנמדדה ביחס ל-Baseline), ו-`Hypothesis` (הסבר אפשרי מקורלטור, תמיד מנוסח כניחוש-לא-מוכח). **`Conclusion` הוא סטטוס שרק בן-אדם יכול לקבוע** — פעולת UI מפורשת ("סמן/י כמאושר") שמקדמת `Hypothesis` קיים ל-`Conclusion`, ותועד מי אישר ומתי. זו המימוש הישיר של סעיף 2.3 באפיון המקורי ("אין להציג השערה כ-root cause ללא הוכחה") — לא כהנחיית UI, אלא כאילוץ בקוד המנוע עצמו.

## 2. חישוב Baseline

**שיטה:** לכל צירוף `(Operation, MetricType, BucketHourOfDay)` — או `(ExceptionGroup, MetricType, BucketHourOfDay)` — נאספות תצפיות יומיות היסטוריות (כמה אירועים/exceptions קרו, או מה הייתה משך ממוצע, באותה שעה-ביום בכל אחד מהימים האחרונים), ומהן מחושבים `MeanValue`/`StdDevValue`/`SampleCount`.

```
לכל Operation פעיל, לכל MetricType (EventCount, DurationMs), לכל שעה 0..23:
  samples = []
  לכל אחד מ-28 הימים האחרונים:
    value = מדידת המטריקה עבור אותה שעה באותו יום (ספירה, או ממוצע-duration)
    if value is not null: samples.add(value)
  אם samples.count >= MIN_SAMPLES (14):
    MeanValue = mean(samples); StdDevValue = stddev(samples)
  else:
    עדיין נשמר, אבל SampleCount < 14 → ישפיע על Confidence (סעיף 6)
```

**חלון היסטוריה:** 28 יום גלגלי (rolling) — מספיק כדי לכסות תבנית שבועית (4 מחזורי שבוע) בלי לדרוש הרבה חודשים לפני שהמערכת "מתחילה לעבוד". זו הבחירה הפשוטה שנקבעה ב-[03-הגדרת-V1.md](03-הגדרת-V1.md) — לא seasonality decomposition כמו Dynamic Thresholds של Azure.

**תדירות עדכון:** Phase נפרד ואיטי יותר מהזיהוי עצמו (ראו סעיף 8) — פעם ביום מספיק, כי ההתנהגות ה"רגילה" לא משתנה תוך דקות.

## 3. ארבעת הגלאים (Detectors) — מייצרים Observation

### 3.1 Rate Anomaly Detector (גנרי — משרת גם Error Spike וגם Missing Activity)

```
current = ספירת EventCount (או ExceptionCount) בשעה האחרונה, ל-Operation/ExceptionGroup נתון
baseline = Baseline row עבור (Operation, MetricType, שעה-נוכחית)
z = (current - baseline.MeanValue) / max(baseline.StdDevValue, MIN_STDDEV_FLOOR)

if z > SPIKE_THRESHOLD (ברירת מחדל 3):     → Finding type=ErrorSpike (או PerformanceDegradation אם MetricType=DurationMs)
if z < -SPIKE_THRESHOLD AND baseline.MeanValue > MIN_MEANINGFUL_ACTIVITY: → Finding type=MissingActivity
```

`MIN_STDDEV_FLOOR` (למשל 0.5) מונע חלוקה במספר קרוב-לאפס כש-Operation יציב-מאוד באופן חריג (stddev כמעט 0 → כל תנודה קטנה הייתה נראית "אינסופית" חריגה בלי הרצפה הזו). `MIN_MEANINGFUL_ACTIVITY` מונע Finding מיותר על Operation שגם ביום רגיל כמעט ולא רץ.

**כיסוי תרחישים:** Error Spike (RetailPulse/ChargePayment), Missing Activity (RetailPulse/PullSupplierFeed), Performance Degradation (FieldOps/MatchAvailability, על MetricType=DurationMs) — 3 מתוך 6 התרחישים ב-[06-מודל-אפליקציה.md](06-מודל-אפליקציה.md).

### 3.2 New Exception Detector

```
כש-ExceptionGroup נוצר לראשונה (FirstSeenAt = עכשיו, אין baseline קודם בהגדרה):
  → Finding type=NewException, Confidence=High מיידית (זו לא שאלה סטטיסטית — "לא ראינו את זה מעולם" הוא Fact ודאי, לא הערכה)
```

**כיסוי תרחישים:** New Exception (RetailPulse/ReserveStock).

### 3.3 Customer Outlier Detector — peer-comparison, לא היסטורי-עצמי

שונה מהותית מ-3.1: לא משווים Customer מול ההיסטוריה **שלו עצמו**, אלא מול **שאר הלקוחות הפעילים** על אותו Operation/ExceptionGroup באותו חלון זמן — כי לקוח חדש לגמרי לא יכול "לחרוג מ-baseline" שאין לו.

```
לחלון הזמן הנוכחי (למשל היום האחרון), לכל Operation/ExceptionGroup:
  rates = { לכל Customer פעיל: קצב האירוע (למשל exception-rate) שלו בחלון }
  אם count(rates) >= MIN_PEER_CUSTOMERS (5):
    population_mean, population_stddev = mean/stddev(rates.values, למעט הלקוח הנבדק)
    לכל Customer: z = (rate - population_mean) / max(population_stddev, MIN_STDDEV_FLOOR)
    if |z| > CUSTOMER_OUTLIER_THRESHOLD (ברירת מחדל 3): → Finding type=CustomerAnomaly
```

`MIN_PEER_CUSTOMERS` מונע false positive באפליקציה עם מעט לקוחות (בהפרש טבעי בין 2 לקוחות ייראה "קיצוני" בלי אוכלוסיית השוואה משמעותית). **כיסוי תרחיש:** Customer-Specific Anomaly (RetailPulse).

## 4. שני הקורלטורים (Correlators) — מייצרים Hypothesis, לעולם לא Conclusion

### 4.1 Deployment Correlator

```
לכל Finding חדש מסוג ErrorSpike/PerformanceDegradation/NewException:
  deployments = Deployments לאותו (ApplicationId, EnvironmentId) עם DeployedAt בחלון
                [Finding.DetectedAt - 60min, Finding.DetectedAt]
  אם נמצא Deployment:
    הוסף Evidence (EvidenceType=Deployment, ReferenceId=deployment.Id)
    הוסף FindingStatement(Kind=Hypothesis,
       "Deployment {Version} הותקן ב-{EnvironmentName} ב-{DeployedAt}, {X} דקות לפני תחילת החריגה.
        ייתכן קשר, אך הוא לא אושר.")
```

**חשוב:** הניסוח תמיד מהוסס ("ייתכן", "לא אושר") — זו לא בחירת UI אלא הטקסט שה-Correlator עצמו מרכיב. **כיסוי תרחיש:** Deployment-Related Anomaly (FieldOps/AggregateJobs).

### 4.2 Downstream Failure Correlator

```
לכל Finding מסוג NewException/ErrorSpike על Operation מסוים:
  related_events = Events עם אותו CorrelationId, ב-Timestamp מאוחר מהאירוע החריג, על Operation אחר
  אם נמצאו Events עם Severity>=Error באותה שרשרת:
    הוסף Evidence (EvidenceType=Event, ReferenceId=...) לכל אחד
    הוסף FindingStatement(Kind=Hypothesis,
       "לאחר אירוע זה, נרשמו {N} שגיאות נוספות באותה שרשרת (CorrelationId) ב-Operation-ים אחרים
        ({op names}). ייתכן שזהו כשל מאוחר שנגרר מהאירוע הזה — לא אושר.")
```

## 5. בניית Finding — דוגמה מלאה (Error Spike, RetailPulse/ChargePayment)

```
Finding: Type=ErrorSpike, Severity=High, Confidence=High, Status=New
├── FindingStatement[Fact]:        "Operation ChargePayment רשם 42 שגיאות בין 02:00–03:00 ב-17/08/2026."
├── FindingStatement[Observation]: "זהו פי 6.3 מהקצב הרגיל לשעה זו (Baseline: 6.7±2.1 שגיאות/שעה, מבוסס 21 ימי היסטוריה)."
├── FindingStatement[Hypothesis]:  "Deployment לגרסה 2.3.1 הותקן ב-Production ב-01:47, 13 דקות לפני תחילת החריגה. ייתכן קשר, לא אושר."
├── Evidence → ExceptionGroup(PaymentGatewayTimeoutException)
├── Evidence → Deployment(#Version 2.3.1, 01:47)
└── Evidence → Baseline row (Operation=ChargePayment, Hour=2, Mean=6.7, StdDev=2.1)
```

זהו בדיוק פורמט הדוגמה מסעיף 13 באפיון המקורי (Fact/Observation/Hypothesis).

## 6. חישוב Confidence

| רמה | תנאי |
|---|---|
| **High** | `\|z\| > 5` **וגם** `SampleCount >= 14`, **או** New Exception (ודאות דטרמיניסטית) |
| **Medium** | `3 < \|z\| <= 5`, **או** `\|z\| > 5` אך `SampleCount < 14` (חריגה גדולה אך baseline לא-בשל) |
| **Low** | `SampleCount < 14` (baseline לא מבוסס דיו) — **מוצג, לא מוסתר**, אבל מתויג בבירור כלא-אמין סטטיסטית |

## 7. פרמטרים (טבלה אחת, ניתנת לכיוונון — לא thresholds קבועים בקוד מפוזר)

| פרמטר | ברירת מחדל | משמעות |
|---|---|---|
| `SPIKE_THRESHOLD` | 3 (z-score) | סף "3-sigma" סטטיסטי סטנדרטי ופשוט — לא ML |
| `MIN_STDDEV_FLOOR` | 0.5 | מונע over-sensitivity על מדדים כמעט-קבועים |
| `MIN_MEANINGFUL_ACTIVITY` | 5/שעה | מתחת לזה, "ירידה" לא מיוצרת Finding (רעש) |
| `BASELINE_LOOKBACK_DAYS` | 28 | חלון rolling ללמידת Baseline |
| `MIN_SAMPLES` | 14 | סף ל-Confidence גבוה |
| `DEPLOYMENT_CORRELATION_WINDOW` | 60 דקות | חלון חיפוש Deployment לפני חריגה |
| `MIN_PEER_CUSTOMERS` | 5 | מינימום לקוחות פעילים להשוואת-עמיתים משמעותית |
| `CUSTOMER_OUTLIER_THRESHOLD` | 3 (z-score) | סף חריגת Customer מהאוכלוסייה |

## 8. מחזור ריצת Analysis Engine (מפרט את סעיף 7 בארכיטקטורה)

**Phase 1 — Baseline Update** (יומי): מריץ את האלגוריתם בסעיף 2 על כל Operation/ExceptionGroup פעילים.

**Phase 2 — Detection** (כל 5 דקות): מריץ 4 הגלאים על החלון האחרון, ואז 2 הקורלטורים על כל Finding חדש שנוצר.

**מניעת כפילויות (dedup):** לפני יצירת Finding חדש, בודקים אם כבר קיים Finding **פתוח** (`Status IN (New, Acknowledged)`) לאותו `(Scope, Type)`. אם כן — מעדכנים אותו (מוסיפים Fact חדש עם המדידה העדכנית) במקום ליצור כפול. Finding חדש נוצר רק אם הקודם `Resolved`/`Dismissed`, או שחלף חלון "צינון" (למשל 24 שעות) מאז הזיהוי האחרון של אותו סוג באותו scope.

## 9. עקרונות הפחתת False Positives (סעיף 12 באפיון המקורי)

1. **Bucketing לפי שעה-ביום** — Operation שרץ בעומס שונה ביום מול לילה לא נתפס "חריג" סתם כי הזמן שונה.
2. **סף מינימום דגימות (`MIN_SAMPLES`)** — Baseline לא-בשל מוריד Confidence ל-Low, לא מדוכא לגמרי (עדיין רואים, אבל לא מוצג כוודאי).
3. **`MIN_STDDEV_FLOOR`/`MIN_MEANINGFUL_ACTIVITY`** — מונעים "רעש סטטיסטי" על מדדים כמעט-קבועים או כמעט-ריקים.
4. **Dedup** — אנומליה מתמשכת לא מייצרת התראה חדשה כל 5 דקות; מתעדכן Finding קיים.
5. **`MIN_PEER_CUSTOMERS`** — לא מסמנים Customer כחריג כשאין מספיק עמיתים להשוואה הוגנת.

## 10. מיפוי סופי: תרחיש → גלאי/קורלטור

| תרחיש (מ-[06-מודל-אפליקציה.md](06-מודל-אפליקציה.md)) | מיוצר ע"י |
|---|---|
| Error Spike (RetailPulse/ChargePayment) | Rate Anomaly Detector (EventCount, כיוון חיובי) |
| Performance Degradation (FieldOps/MatchAvailability) | Rate Anomaly Detector (DurationMs) |
| New Exception (RetailPulse/ReserveStock) | New Exception Detector |
| Deployment-Related Anomaly (FieldOps/AggregateJobs) | Rate Anomaly Detector + Deployment Correlator (מצרף Hypothesis) |
| Missing Activity (RetailPulse/PullSupplierFeed) | Rate Anomaly Detector (EventCount, כיוון שלילי) |
| Customer-Specific Anomaly (RetailPulse) | Customer Outlier Detector |

כל 6 התרחישים מכוסים ע"י 4 גלאים + 2 קורלטורים — לא נדרש מנגנון נוסף.

---

*לוגיקת ה-thresholds כאן פשוטה בכוונה (z-score על ממוצע/סטיית-תקן) — לא seasonality decomposition או ML, כפי שנקבע ב-V1. המסמך הבא (UI Design) קובע איך זה מוצג למשתמש.*
