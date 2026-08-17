# Ingestion / API Design

**חלק מ:** אפיון פרויקט R&D — מערכת גנרית לניהול, חקירה ואבחון לוגים
**שלב:** תוצר מס' 7 מתוך 12 בסעיף 23 של האפיון המקורי
**תאריך:** 2026-08-17
**מבוסס על:** [05-מודל-נתונים.md](05-מודל-נתונים.md), [06-מודל-אפליקציה.md](06-מודל-אפליקציה.md)

## 1. הבחנות מושגיות (נדרש במפורש בסעיף 5 באפיון המקורי)

| מושג | הגדרה | ייצוג בנתונים |
|---|---|---|
| **Event** | רשומת לוג בודדת שנקלטה — היחידה האטומית של המערכת | שורה בטבלת `Event` |
| **Operation** | node בהיררכיית האפליקציה (השכבה העמוקה ביותר) שמייצג פעולה לוגית בשם קבוע (למשל `ChargePayment`) — **סיווג**, לא מופע | שורה קבועה ב-`Operation`; אירועים רבים מצטטים אותה לאורך זמן |
| **Request** | **אינו ישות נפרדת ב-V1.** "בקשה" היא מונח עסקי לא-פורמלי למופע בודד של הפעלת Operation — מיוצג אך ורק ע"י קיבוץ Events שחולקים `CorrelationId`/`TraceId` משותף | קבוצת שורות `Event` |
| **Exception** | Event שנושא פרטי חריגה (`ExceptionGroupId`+`StackTrace` מלאים) — מופע בודד, לא אגרגציה | שורת `Event` עם `ExceptionGroupId` לא-NULL |
| **Trace** | שרשרת טכנית של spans בבקשה מבוזרת אחת (מודל OpenTelemetry: `TraceId` משותף, `SpanId`/`ParentSpanId` מקננים) | עמודות `TraceId`/`SpanId`/`ParentSpanId` על `Event` |
| **Correlation** | מזהה עסקי/לוגי רחב יותר, לאו-דווקא טכני — יכול לקשור אירועים שאינם חלק מאותו trace טכני אך שייכים לאותו תהליך עסקי (למשל OrderId שמקשר יצירת הזמנה, תשלום, ומשלוח שקרו בשלושה traces נפרדים) | עמודת `CorrelationId` על `Event`, מוגדרת ע"י המפתח, לא נגזרת אוטומטית |

## 2. Ingestion API

### `POST /api/v1/ingest/events`

מקבל אירוע בודד או batch (מערך JSON). **Auth:** header `X-Api-Key`, ממופה ל-`ApiKey`→`Application` — כל אירוע ב-batch משויך אוטומטית ל-Application של המפתח (לא נשלח ApplicationId בבקשה).

```json
POST /api/v1/ingest/events
X-Api-Key: <key>
Content-Type: application/json

[
  {
    "eventKey": "b3f1...guid",          // אופציונלי, ל-idempotency (סעיף 5)
    "timestamp": "2026-08-17T10:22:31Z",
    "severity": "Error",                 // Trace|Debug|Info|Warn|Error|Fatal — ממופה למספרי OTel (05-מודל-נתונים.md)
    "environment": "Production",
    "version": "2.3.1",
    "hierarchy": {                       // כל שדה אופציונלי, name-path — ראו סעיף 3
      "module": "Payments",
      "screenService": "PaymentGateway",
      "process": "ProcessPayment",
      "operation": "AuthorizeCard"
    },
    "correlationId": "order-8841",
    "traceId": "4bf9...", "spanId": "00f0...", "parentSpanId": null,
    "durationMs": 812.4,
    "customerId": "cust-042", "userId": "user-118",
    "message": "Card authorization failed",
    "messageTemplate": "Card authorization failed for {CardLast4}",
    "exception": {
      "type": "PaymentGatewayTimeoutException",
      "stackTrace": "..."
    },
    "metadata": { "cardLast4": "4242", "attempt": 2 }
  }
]
```

**תגובה — partial-success, בהשראת סמנטיקת OTLP** (מחקר OpenTelemetry, [01-מחקר-השוואתי.md](01-מחקר-כלים-קיימים/01-מחקר-השוואתי.md)): גם אם חלק מהאירועים ב-batch נכשלים בוולידציה, האירועים התקינים נקלטים. תשובה `202 Accepted`:

```json
{ "accepted": 9, "rejected": 1,
  "errors": [ { "index": 3, "reason": "severity: invalid value 'Critical'" } ] }
```

**כלל מנחה:** כשל בוולידציה של **שדה בודד** (למשל hierarchy path לא ידוע) לא דוחה את כל האירוע — ראו סעיף 3. רק כשל בשדה **חובה** (timestamp/severity/message חסרים) דוחה את האירוע הבודד, לא את ה-batch כולו.

## 3. מדיניות פתרון היררכיה (Hierarchy Resolution) — החלטה מרכזית

**השאלה:** כשמפתח/ת שולח/ת `"operation": "ChargePayment"`, האם המערכת יוצרת את ה-node אוטומטית אם הוא לא קיים, או דוחה?

**החלטה: לא יוצרים אוטומטית.** ההיררכיה מנוהלת במכוון דרך ה-Admin API/UI ([06-מודל-אפליקציה.md](06-מודל-אפליקציה.md)) — זו בדיוק הנקודה שמבדילה אותנו מ-Application Insights Application Map (טופולוגיה מתגלה-אוטומטית). יצירה אוטומטית הייתה הופכת את ההיררכיה לספרייה שמתנפחת מכל typo.

**אבל: לא מאבדים את האירוע בגלל זה.** אם `"operation": "ChrgePayment"` (typo) לא נמצא בהיררכיה הרשומה, ה-Event **עדיין נקלט** — שדה ה-Operation נשאר NULL, ומתווסף `hierarchyWarnings` בתגובה (`{"index": 0, "field": "operation", "reason": "not found, event stored without operation reference"}`). זו הפשרה: לא מאבדים data, אבל גם לא מזהמים את ההיררכיה בשקט.

## 4. Query API

| Endpoint | תיאור |
|---|---|
| `GET /api/v1/events` | סינון רב-ממדי: `applicationId`+`environmentId` (חובה), `from`/`to`, `severity`, `moduleId`/`screenServiceId`/`processId`/`operationId`, `correlationId`, `traceId`, `userId`, `customerId`, `exceptionGroupId`, `versionId`, `durationMinMs`/`durationMaxMs`, `q` (full-text על Message), `page`/`pageSize` |
| `GET /api/v1/events/{id}` | אירוע בודד + כל השדות |
| `GET /api/v1/timeline` | רצף אירועים מסודר לפי זמן, לפי `correlationId=` **או** `traceId=` **או** (`operationId=`+`userId=`) **או** `customerId=` — ממש את דרישת סעיף 7 באפיון המקורי |
| `GET /api/v1/exception-groups` | רשימה + סינון (application/environment/date-range), ממוינת לפי `LastSeenAt`/`OccurrenceCount` |
| `GET /api/v1/exception-groups/{id}` | פרטי הקבוצה + מגמת תדירות + Applications/Environments/Versions/Operations מושפעים |
| `GET /api/v1/findings` | סינון לפי `status`/`severity`/`type`/`from`/`to` |
| `GET /api/v1/findings/{id}` | Finding מלא: כל ה-`FindingStatement` (עם Kind) + כל ה-`Evidence` + קישורי Drill-Down |
| `GET /api/v1/health` | סטטוס DB + זמן ריצה אחרון של Analysis Engine — חשוב כי המנוע רץ בטיימר שקט, כשל בו לא יבלוט אחרת |

## 5. Admin API (מבנה אפליקציה)

CRUD אחיד לכל שכבות ההיררכיה, תחת `/api/v1/admin/applications/{appId}/...`:
`environments`, `modules`, `modules/{id}/screen-services`, `screen-services/{id}/processes`, `processes/{id}/operations`, `customers`, `versions`, `deployments`, `api-keys`.

כל resource תומך `GET` (רשימה, כולל לא-פעילים עם `?includeInactive=true`), `POST` (יצירה), `PUT` (עדכון — כולל rename), `DELETE` (soft-delete אם יש היסטוריה, hard-delete אם אין — ראו [06-מודל-אפליקציה.md](06-מודל-אפליקציה.md) סעיף 3; ה-API מחזיר `409 Conflict` עם `"reason": "has historical events, soft-deleted instead"` כשה-delete הופך ל-soft באופן שקוף).

## 6. טיפול בשגיאות, גרסאות, Idempotency, Rate Limiting

- **פורמט שגיאה אחיד** (בהשראת RFC 7807): `{ "type", "title", "status", "detail", "errors": {"field": ["msg"]} }` לכל 4xx/5xx.
- **Versioning:** נתיב URL (`/api/v1/...`) — מפורש, פשוט, לא תלוי-header.
- **Idempotency:** `eventKey` אופציונלי (GUID שהלקוח מייצר) בכל אירוע — אם אותו `eventKey` מגיע שוב (retry אחרי timeout), האירוע לא משוכפל. חשוב כי retry ברשת הוא תרחיש ריאלי בפייפליין לוגים.
- **Rate Limiting:** מגבלה בסיסית per-API-key (למשל 1000 events/דקה), מחזירה `429` + `Retry-After`. לא תוחכם ל-V1 — רק guardrail מול bug/generator שיוצא משליטה.

## 7. .NET Client Library (`LogsPlatform.Client`)

שני מצבי שימוש:

1. **ישיר** — `ILogsPlatformClient.SendEventAsync(...)` לשליטה מלאה.
2. **Serilog sink** — `.WriteTo.LogsPlatform(apiKey, baseUrl, environment: "Production")`, ממפה `LogContext` properties מוסכמים (`Module`, `ScreenService`, `Process`, `Operation`, `CorrelationId`, `CustomerId`) לשדות ה-hierarchy — מתאים ישירות להקשר Serilog שכבר נחקר ([01-מחקר-השוואתי.md](01-מחקר-כלים-קיימים/01-מחקר-השוואתי.md)).

**Batching + Resilience:** ה-client צובר אירועים ושולח ב-batch (כל 2 שניות או 100 אירועים, לפי המוקדם) — לא HTTP call לכל log line בודד. אם ה-API לא זמין: buffer מוגבל בגודל, drop-oldest בגלישה, אזהרה client-side (ל-Console, לא בחזרה לאותו pipeline — נמנע מלולאה אינסופית). **עקרון:** תקלה ב-ingestion **לעולם** לא מפילה את אפליקציית הלקוח.

## 8. מה נשאר לתוצר הבא

לוגיקת ה-Analysis Engine (מתי בדיוק Finding נוצר מתוך אירועים) — **Analysis/Anomaly Detection Design**, הבא בתור.

---

*חוזה זה הוא הבסיס למימוש בפועל — שינויים עתידיים (הוספת שדה חופשי, למשל) אמורים להיות backward-compatible תחת `/v1`, לא לדרוש `/v2` מיידי.*
