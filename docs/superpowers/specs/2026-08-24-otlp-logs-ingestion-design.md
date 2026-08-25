# OTLP/Logs Ingestion — Design

**חלק מ:** R&D Logs Platform — V2, קבוצה A (קליטה)
**תאריך:** 2026-08-24
**מבוסס על:** `03-הגדרת-V1.md` §5 ("OTLP/OpenTelemetry native ingestion — נדחה... Collector+adapter הוא עבודה נוספת משמעותית"), `07-Ingestion-ו-API.md` §1-2, [docs/handoff/what-was-done.md](../../handoff/what-was-done.md) (המלצה #1)
**Follows:** M0–M7, כולם ממוזגים ל-`main`. זהו הפרויקט הראשון מחוץ להיקף התכנון המקורי (V2).

## 1. מטרה

לאפשר לאפליקציות המשתמשות כבר ב-OpenTelemetry SDK משלהן (לאו דווקא .NET) להתחבר ל-LogsPlatform **בלי לאמץ את `LogsPlatform.Client` הקנייני** — פשוט להצביע את ה-OTLP exporter הקיים שלהן אל endpoint חדש. זה שונה מהמסקנה שנבדקה מוקדם יותר היום (auto-instrumentation "ללא שינוי קוד בכלל") — כאן עדיין נדרשת קונפיגורציה/קוד בצד הלקוח (הגדרת exporter+endpoint+headers), רק לא קוד קנייני ל-LogsPlatform.

## 2. ארכיטקטורה

Endpoint חדש `POST /v1/logs` (הנתיב הסטנדרטי של OTLP/HTTP) ב-`LogsPlatform.Web`, **מקביל** ל-`/api/v1/ingest/events` הקיים — לא מחליף אותו. שני ה-endpoints מזינים את אותו צינור פנימי.

- **פורמט:** Protobuf בלבד (לא JSON) — זה מה שרוב ה-exporters האמיתיים שולחים כברירת מחדל; תמיכה רק ב-JSON הייתה נותנת אימות שקרי מול לקוחות שלא קיימים בפועל. שימוש בחבילת `OpenTelemetry.Proto` (או ה-`.proto` הרשמי של OTLP, מקומפל דרך `Grpc.Tools`/`Google.Protobuf`) לפענוח `ExportLogsServiceRequest`.
- **Transport:** HTTP בלבד (לא gRPC) — תואם למה שכבר קיים בפרויקט (Kestrel HTTP, בלי תשתית gRPC).
- **סיגנל יחיד:** Logs בלבד. לא Traces, לא Metrics — Traces/Spans לא רלוונטיים למודל הנתונים של LogsPlatform (שמבוסס Event בודד, לא span tree), ו-Metrics מחוץ לתחום המוצר לגמרי.
- **אימות:** אותו `X-Api-Key` header הקיים (`ApiKeyAuthenticationHandler`/`ApiKeyAuthenticationOptions.SchemeName`) — לא נבנה מנגנון אימות נפרד. OTel exporters תומכים ב-custom headers דרך קונפיגורציה סטנדרטית.
- **ללא OTel Collector כתהליך נפרד.** ה-endpoint עצמו הוא היעד. לקוח שרוצה כן להשתמש ב-Collector (למשל לצורך batching/retry/multi-destination) יכול להצביע אותו אל ה-endpoint הזה — אבל זה לא נדרש ולא חלק מההיקף.

## 3. מיפוי שדות (`LogRecord` → `IngestEventRequest`)

| OTLP `LogRecord` | LogsPlatform | הערות |
|---|---|---|
| `TimeUnixNano` | `Timestamp` | המרת nanoseconds ל-`DateTime` UTC |
| `SeverityNumber` (1-24) | `Severity` | מיפוי ישיר: TRACE(1-4)/DEBUG(5-8)/INFO(9-12)/WARN(13-16)/ERROR(17-20)/FATAL(21-24) — כבר תואם כי שדות ה-Severity הפנימיים נבנו מראש לפי מוסכמות OTel |
| `Body` | `Message` | `AnyValue` שאינו מחרוזת (למשל KvList) מומר ל-JSON כמחרוזת |
| `TraceId` / `SpanId` | `TraceId` / `SpanId` | המרת bytes ל-hex string, ישירות — השדות כבר קיימים על `Event` |
| `Resource.attributes["deployment.environment"]` | `Environment` | attribute **תקני** של OTel — לא קונבנציה מותאמת-אישית |
| `Attributes["exception.type"]` + `["exception.stacktrace"]` | `Exception` (`IngestExceptionPayload`) | מוסכמת OTel הרשמית לחריגות על log records |
| `Attributes["logsplatform.module"]` / `["logsplatform.screen_service"]` / `["logsplatform.process"]` / `["logsplatform.operation"]` | `Hierarchy` | קונבנציית attributes מותאמת-אישית — OTLP אין לו מושג מובנה להיררכיה 5 השכבות |
| `Attributes["logsplatform.customer_id"]` | `CustomerId` | קונבנציה מותאמת-אישית, כנ"ל |
| `Attributes["logsplatform.user_id"]` | `UserId` | קונבנציה מותאמת-אישית, כנ"ל |
| שאר `Attributes` (לא ממופים לעיל) | `Metadata` | עוברים כפי שהם למילון ה-Metadata הקיים |
| `Resource.attributes["service.name"]` | — | **לא** ממופה ל-Application — זיהוי ה-Application נשאר דרך ה-API Key, תואם למוסכמה הקיימת בכל שאר ה-API |

Attributes חסרים (למשל אין `logsplatform.module`) פשוט לא ממלאים את השדה המקביל — תואם להתנהגות הקיימת ב-`07-Ingestion-ו-API.md` ("כל שדה אופציונלי, name-path").

## 4. זרימת נתונים

בקשת HTTP POST (Protobuf) → אימות `X-Api-Key` (אותו handler קיים) → פענוח `ExportLogsServiceRequest` → לכל `LogRecord`: מיפוי ל-`IngestEventRequest` לפי הטבלה לעיל → הזנה לאותו `IngestionProcessor` הקיים (validation/idempotency/hierarchy resolution/rate limiting) → תגובת OTLP סטנדרטית (`ExportLogsServiceResponse`, עם `partial_success` אם חלק מהרשומות נדחו — תואם לסמנטיקת ה-partial-success שכבר קיימת ב-API הקנייני).

## 5. בדיקות

מעבר לבדיקות יחידה על לוגיקת המיפוי (Severity/Timestamp/Attributes→Hierarchy), **אימות אמיתי מול OTel SDK רשמי** — לא רק בקשות Protobuf בנויות-ביד: אפליקציית .NET קטנה עם `OpenTelemetry` + `OpenTelemetry.Exporter.OpenTelemetryProtocol` (חבילות NuGet רשמיות), עם exporter מוגדר להצביע על ה-endpoint החדש, שולחת לוג אמיתי דרך `ILogger`/OTel Logging API. זה בדיוק סוג האימות (לקוח אמיתי, לא רק HTTP ידני) שגילה את שני הבאגים האמיתיים מוקדם יותר היום.

## 6. מחוץ להיקף

Traces/Spans, Metrics, gRPC transport, OTel Collector כתהליך נפרד, מיפוי `service.name`→Application (האימות נשאר דרך API Key בלבד).
