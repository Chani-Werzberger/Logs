# הגדרת חיבור ל-SQL Server

מסמך זה מסביר איך להגדיר את החיבור בין `LogsPlatform.Web` למסד הנתונים שלך, ואיך להחליף למסד/שרת אחר בכל שלב.

## הרעיון הבסיסי

**ה-connection string בפועל אף פעם לא נמצא בקוד או ב-`appsettings.json`** — הוא נשמר ב-.NET User Secrets, מנגנון שמאחסן אותו מחוץ לתיקיית הפרויקט לגמרי (ב-`%APPDATA%\Microsoft\UserSecrets\<guid>\secrets.json`), כדי שלעולם לא ייכנס בטעות ל-git. הקוד רק קורא למפתח בשם `ConnectionStrings:LogsPlatformDb` — ראו [`Program.cs`](../src/LogsPlatform.Web/Program.cs):

```csharp
builder.Services.AddDbContext<LogsPlatformDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("LogsPlatformDb")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:LogsPlatformDb configuration.")));
```

זה אומר שיש **שני שלבים נפרדים** בכל פעם שרוצים לחבר את האפליקציה למסד נתונים (חדש או קיים) — לא מספיק רק אחד מהם:

1. **להגיד לאפליקציה בזמן ריצה** לאן להתחבר (User Secrets).
2. **ליצור בפועל את הטבלאות** באותו מסד (migration) — האפליקציה לא עושה את זה אוטומטית בהפעלה.

---

## שלב 1: קביעת ה-connection string

מריצים מתוך תיקיית `src/LogsPlatform.Web`:

```bash
cd src/LogsPlatform.Web
dotnet user-secrets set "ConnectionStrings:LogsPlatformDb" "<connection string שלך>"
```

### דוגמאות לפי סוג השרת

**LocalDB (ברירת המחדל בפיתוח, כבר מוגדר):**
```
Server=(localdb)\mssqllocaldb;Database=LogsPlatformDev;Trusted_Connection=True;
```

**SQL Server מלא מקומי, Windows Authentication** (למשל SQL Server Express תחת instance בשם `SQLEXPRESS`):
```
Server=localhost\SQLEXPRESS;Database=LogsPlatformDev;Trusted_Connection=True;TrustServerCertificate=True;
```
את שם ה-instance אפשר לבדוק דרך שירותי Windows (`services.msc`) — יופיע כ"SQL Server (שם-ה-instance)". אם זה instance ברירת מחדל (בלי שם), משתמשים רק ב-`Server=localhost;`.

**SQL Server עם אימות SQL (משתמש+סיסמה, לא Windows Authentication):**
```
Server=<server-name>;Database=LogsPlatformDev;User Id=<username>;Password=<password>;TrustServerCertificate=True;
```

**שרת מרוחק / Azure SQL:**
```
Server=<server-address>,1433;Database=LogsPlatformDev;User Id=<username>;Password=<password>;Encrypt=True;TrustServerCertificate=False;
```
לשרת מרוחק אמיתי (לא מקומי) עדיף `Encrypt=True` ו-`TrustServerCertificate=False` (לא כמו הדוגמאות המקומיות למעלה) — כי שם באמת רוצים אימות תעודה תקין, לא רק לעקוף אזהרת פיתוח מקומית.

### לבדוק מה מוגדר כרגע
```bash
dotnet user-secrets list --project src/LogsPlatform.Web
```

---

## שלב 2: יצירת הטבלאות באותו מסד (migration)

זה השלב שהכי קל לשכוח — בלעדיו תקבלו שגיאת SQL כמו `Invalid object name 'Applications'` בהרצה הראשונה.

```bash
dotnet ef database update \
  --project src/LogsPlatform.Infrastructure \
  --connection "<אותו connection string בדיוק משלב 1>"
```

**חשוב:** צריך להעביר את ה-`--connection` במפורש בכל פעם שמחליפים שרת. `dotnet ef` לא קורא את ה-User Secrets שהגדרתם למעלה — הוא משתמש כברירת מחדל ב-[`LogsPlatformDbContextFactory.cs`](../src/LogsPlatform.Infrastructure/LogsPlatformDbContextFactory.cs), שמצביע קשיח ל-LocalDB (`(localdb)\mssqllocaldb;Database=LogsPlatformDev`) — קובץ נפרד שקיים רק כדי שכלי ה-CLI של EF Core יוכל לעבוד בלי להריץ את כל האפליקציה. `--connection` עוקף את זה לצורך ההרצה הבודדת הזו בלבד.

אם ה-migration כבר רץ פעם על אותו מסד בדיוק (למשל חוזרים ל-LocalDB אחרי שהחלפתם), אין צורך להריץ שוב — `dotnet ef database update` פשוט לא יעשה כלום אם הטבלאות כבר קיימות ותואמות.

---

## שלב 3: הרצה

```bash
dotnet run --project src/LogsPlatform.Web --launch-profile http
```

בדיקה מהירה שהכל מחובר נכון — [http://localhost:5201/swagger](http://localhost:5201/swagger), או:
```bash
curl -X POST http://localhost:5201/api/v1/admin/applications \
  -H "Content-Type: application/json" \
  -d '{"name":"Test","description":"connection check"}'
```
תגובה מצופה: `201 Created` עם רשומה חדשה. אם מקבלים שגיאת חיבור — חזרו לשלב 1 (connection string שגוי/שרת לא נגיש). אם מקבלים `Invalid object name` — חזרו לשלב 2 (migration לא רץ על המסד הזה).

---

## מסדי נתונים נוספים שכדאי לדעת עליהם

הפרויקט משתמש בשני מסדים **נפרדים** מלבד ה-dev database שהגדרתם למעלה — שניהם לא קשורים להגדרה הזו ואין צורך לגעת בהם:

- **`LogsPlatformTests`** — מסד נפרד שהבדיקות (`dotnet test`) יוצרות ומוחקות אוטומטית בכל הרצה (`EnsureDeleted()`+`Migrate()`), תמיד על LocalDB, מוגדר קשיח ב-[`TestDatabase.cs`](../tests/LogsPlatform.Tests/Infrastructure/TestDatabase.cs).
- **ה-DB שה-design-time factory מצביע אליו** — כאמור בשלב 2, תמיד LocalDB, רק לצורך יצירת migrations חדשים בעתיד.

כלומר: שינוי ה-connection string שלמעלה משפיע **רק** על האפליקציה עצמה בזמן ריצה — לא על הבדיקות ולא על תהליך יצירת ה-migrations.
