# LogsPlatform

מערכת גנרית לניהול, חקירה ואבחון של לוגים ממערכות תוכנה שונות — Application-Aware: מכירה את המבנה הלוגי של האפליקציה המחוברת (Application → Module → Screen/Service → Process → Operation), מקשרת אירועים להקשר הזה, ומזהה בעצמה חריגות, מגמות ושילובים חשודים — עם הפרדה קפדנית בין Fact/Observation (מדוד) ל-Hypothesis/Conclusion (מוסבר, מאושר רק על ידי אדם).

לרקע מלא על המוצר, המחקר שקדם לו וההיקף של V1, ראו [מסמכי-אפיון](מסמכי-אפיון/02-אפיון-המוצר.md).

## דרישות מקדימות

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- SQL Server נגיש (LocalDB / SQL Server Express / מופע מלא) — הפרויקט נבדק מול `localhost\SQLEXPRESS`

## התקנה

```bash
git clone <repo-url>
cd לוגים
dotnet restore
```

**הגדרת מחרוזת החיבור** (User Secrets בלבד — לעולם לא ב-`appsettings.json` או בקוד):

```bash
cd src/LogsPlatform.Web
dotnet user-secrets set "ConnectionStrings:LogsPlatformDb" "Server=<your-server>;Database=LogsPlatformDev;Trusted_Connection=True;TrustServerCertificate=True;"
```

**הרצת המיגרציות — שלב חובה, לא אוטומטי:**

```bash
cd ../..
dotnet ef database update --project src/LogsPlatform.Infrastructure --startup-project src/LogsPlatform.Infrastructure
```

⚠️ **חשוב:** `Program.cs` **לא** מריץ מיגרציות אוטומטית בהפעלה. אם מדלגים על השלב הזה, ההרצה הראשונה תיכשל עם `Invalid object name 'PlatformUsers'` (או טבלה אחרת) ברגע הראשון שהאפליקציה מנסה לגשת ל-DB. ודאו גם ש-`dotnet ef migrations list --project src/LogsPlatform.Infrastructure --startup-project src/LogsPlatform.Infrastructure` לא מציג אף פריט עם `(Pending)` — אם כן, ה-DB ומחרוזת החיבור לא תואמים.

**סריקת Secrets לפני commit** (חד-פעמי, לא אוטומטי כברירת מחדל ב-git):

```bash
git config core.hooksPath .githooks
```

## הרצה

```bash
dotnet run --project src/LogsPlatform.Web
```

בהרצה הראשונה בלבד (כשאין עדיין אף `PlatformUser`), הקונסולה תדפיס שם משתמש (`admin`) וסיסמה שנוצרה אקראית — **מוצגת פעם אחת בלבד ולא נשמרת בשום מקום אחר**. התחברות דרך `/login`.

## הרצת בדיקות

```bash
dotnet test
```

הריצה המלאה (326 בדיקות נכון ל-2026-08-24) לוקחת כ-11–14 דקות — כל הבדיקות רצות מול SQL Server אמיתי (לא InMemory), ולכן דורשות את אותה מחרוזת חיבור/גישה ל-DB כמו ההרצה הרגילה. אין צורך בהגדרה נוספת מעבר לשלב ההתקנה.

## תיעוד API

Swagger UI זמין רק בסביבת Development, בכתובת `/swagger` (לדוגמה `http://localhost:5201/swagger` אם רצים על פורט 5201).

## קונפיגורציה / Secrets

| מפתח | תיאור | חובה |
|---|---|---|
| `ConnectionStrings:LogsPlatformDb` | מחרוזת החיבור ל-SQL Server | כן, אין ברירת מחדל — האפליקציה זורקת חריגה בהפעלה אם חסר |

זהו המפתח היחיד שהאפליקציה עצמה (`LogsPlatform.Web`) צורכת. אפליקציות **צרכניות** שמתחברות אליה דרך `LogsPlatform.Client` (למשל דוגמת YesService) מגדירות אצלן, בנפרד, `LogsPlatform:ApiKey` ו-`LogsPlatform:BaseUrl` משלהן — זה לא חלק מהקונפיגורציה של הפרויקט הזה.

## מבנה ותיעוד מעמיק

| נושא | מסמך |
|---|---|
| ארכיטקטורה | [04-ארכיטקטורה.md](מסמכי-אפיון/04-ארכיטקטורה.md) |
| מודל נתונים | [05-מודל-נתונים.md](מסמכי-אפיון/05-מודל-נתונים.md) |
| מודל אפליקציה (היררכיה) | [06-מודל-אפליקציה.md](מסמכי-אפיון/06-מודל-אפליקציה.md) |
| Ingestion ו-API | [07-Ingestion-ו-API.md](מסמכי-אפיון/07-Ingestion-ו-API.md) |
| מנוע אבחון / Anomaly Detection | [08-Analysis-ו-Anomaly-Detection.md](מסמכי-אפיון/08-Analysis-ו-Anomaly-Detection.md) |
| עיצוב UI | [09-UI-Design.md](מסמכי-אפיון/09-UI-Design.md) |
| אבטחה | [10-Security-Design.md](מסמכי-אפיון/10-Security-Design.md) |
| אסטרטגיית בדיקות | [11-Test-Strategy.md](מסמכי-אפיון/11-Test-Strategy.md) |
| תוכנית עבודה ואבני דרך | [12-תוכנית-עבודה-ואבני-דרך.md](מסמכי-אפיון/12-תוכנית-עבודה-ואבני-דרך.md) |

מסמכי מסירה נוספים: [Known Limitations](docs/handoff/known-limitations.md), [מה בוצע / מה נשאר](docs/handoff/what-was-done.md).
