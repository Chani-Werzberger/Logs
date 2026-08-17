# UI Design

**חלק מ:** אפיון פרויקט R&D — מערכת גנרית לניהול, חקירה ואבחון לוגים
**שלב:** תוצר מס' 9 מתוך 12 בסעיף 23 של האפיון המקורי
**תאריך:** 2026-08-17
**מבוסס על:** [07-Ingestion-ו-API.md](07-Ingestion-ו-API.md) (Query/Findings API), [08-Analysis-ו-Anomaly-Detection.md](08-Analysis-ו-Anomaly-Detection.md) (מבנה Finding), Blazor Server ([04-ארכיטקטורה.md](04-ארכיטקטורה.md))

## 1. עקרון מנחה — היפוך סדר החקירה מול המתחרים

מחקר ה-UX ([01-מחקר-השוואתי.md](01-מחקר-כלים-קיימים/01-מחקר-השוואתי.md), סעיף Seq/UX) מצא ש-Seq (וכלים דומים) בנויים סביב: Search → Filter → Inspect → Analyze → Correlate → Chart/Alert — **המשתמש מתחיל משאלה**. אצלנו הבית הוא **What's Unusual → למה? → ראיות → אירועים קשורים → Drill Down** — **המערכת מתחילה עם תשובה**, והמשתמש בוחר אם לחקור לעומק. זו לא רק בחירת מסך-בית — זו המימוש הישיר של הצעת הערך (סעיף 17 באפיון המקורי: "המערכת בעצמה מזהה ומסבירה").

## 2. מבנה ניווט (IA)

```
[Application + Environment selector — תמיד גלוי, בראש המסך] ← אוכף את בידוד ה-Environment גם ויזואלית
├── What's Unusual  (דף הבית)
├── Search           (חיפוש אירועים חופשי)
├── Exceptions        (קבוצות Exceptions)
└── Admin             (מבנה אפליקציה — מוצג רק למשתמשים עם הרשאת אדמין)

Timeline — אין לו כניסת ניווט עצמאית. תמיד מגיעים אליו בהקשר (מתוך Event/Finding/Customer/User) —
כי Timeline בלי הקשר הוא בדיוק ה"עוד כלי לוגים" שאנחנו לא רוצים להיות.
```

## 3. What's Unusual — דף הבית

**רשימת Findings**, ממוינת כברירת מחדל לפי Severity ואז DetectedAt (החדש/החמור ביותר למעלה). כל שורה: אייקון-Type, Severity badge, Confidence badge, כותרת, Application/Operation, DetectedAt, Status. **פילטרים:** Severity, Confidence, Status, Type, טווח תאריכים — כל הפילטרים ממופים ישירות ל-`GET /api/v1/findings` ([07-Ingestion-ו-API.md](07-Ingestion-ו-API.md)).

**מצב ריק:** "לא נמצאו חריגות בטווח הזמן/הסינון הנוכחי" — לא מסך שגיאה, מצב תקין לגמרי (ולפעמים המצב הרצוי).

### מסך Finding בודד — הלב של ה-UI כולו

1. **כותרת + badges** (Severity/Confidence/Status).
2. **גוף ה-Finding, מוצג ויזואלית לפי Kind** — לא רשימת טקסט שטוחה:
   - `Fact` — ניטרלי (אפור/כחול כהה), "מה שנמדד".
   - `Observation` — כחול, עם אייקון גרף, "מה שנצפה כחריגה".
   - `Hypothesis` — כתום/ענבר, עם אייקון שאלה ותווית קבועה **"טרם אושר"** — לעולם לא מוצג בעיצוב שנראה כמו מסקנה סופית.
   - `Conclusion` — ירוק, מופיע **רק** אם בן-אדם קידם Hypothesis במפורש (עם "אושר ע"י {user} ב-{date}").
3. **Evidence** — רשימת קישורים (Event/ExceptionGroup/Deployment/Baseline/Operation), כל אחד עם תיאור קצר ולחיצה שמנווטת למסך הרלוונטי (Event→Search מסונן, ExceptionGroup→מסך Exceptions, Deployment→Admin/Deployments).
4. **פעולות:** Acknowledge / Resolve / Dismiss, ו-**"קדם/י ל-Conclusion"** — פעולה נפרדת per-Hypothesis-statement, דורשת בחירת ה-statement הספציפי + הערת אישור קצרה (חובה) לפני שהיא נשמרת. זו נקודת האכיפה היחידה במערכת שבה Hypothesis הופך ל-Conclusion — בכוונה לא אוטומטית (ראה [08-Analysis-ו-Anomaly-Detection.md](08-Analysis-ו-Anomaly-Detection.md) סעיף 1).
5. **Drill Down:** כפתורי "צפה ב-Timeline", "צפה באירועים המקוריים", "צפה בקבוצת ה-Exception" — לפי מה שרלוונטי לסוג ה-Finding.

## 4. Search — חקירת אירועים חופשית

פאנל סינון (ממופה ל-`GET /api/v1/events`): טווח זמן, Severity, בחירת היררכיה מדורגת (Module→ScreenService→Process→Operation, כל שכבה מסננת את הבאה), CorrelationId, TraceId, UserId, CustomerId, ExceptionGroup, Version, טווח Duration, תיבת חיפוש חופשי (full-text על Message). טבלת תוצאות: Timestamp, Severity, נתיב Operation מלא, Message מקוצר, Duration, CorrelationId. **לחיצה על שורה** פותחת פאנל צד עם כל השדות + Metadata (JSON מעוצב) + StackTrace (אם קיים) + כפתור **"צפה ב-Timeline"**.

## 5. Timeline — הקשר כרונולוגי

רשימה כרונולוגית של כל Events שחולקים CorrelationId/TraceId (או Operation+User, או Customer, לפי איך הגיעו לכאן). כל שורה: זמן יחסי (`+0ms`, `+812ms`...), Operation, Severity, Duration, Message מקוצר. אם הגיעו מ-Finding — **האירוע שהחל את החריגה מודגש ויזואלית** (מסגרת/צבע רקע שונה) כדי לענות ישירות על "איפה התחילה הבעיה" (סעיף 7 באפיון המקורי).

## 6. Exceptions

**רשימה:** fingerprint מקוצר, ExceptionType, OccurrenceCount, FirstSeen/LastSeen, sparkline קטן (ספירה יומית, N ימים אחרונים), Applications/Operations מושפעים. ממוינת כברירת מחדל לפי LastSeenAt.
**מסך קבוצה:** Stack trace מלא (representative), גרף מגמה, טבלת מופעים אחרונים (לחיצה → Search מסונן ל-ExceptionGroupId הזה), Findings שמצטטים את הקבוצה כ-Evidence.

## 7. Admin — מבנה אפליקציה

בחירת Application → טאבים: **Environments**, **מבנה** (עץ ניתן-להרחבה: Module→ScreenService→Process→Operation, עריכה inline: rename/deactivate — לפי הכללים ב-[06-מודל-אפליקציה.md](06-מודל-אפליקציה.md) סעיף 3, כולל אזהרה מפורשת לפני deactivate של node עם היסטוריה: "יש X אירועים משויכים — הצומת יוסתר אך ההיסטוריה תישמר"), **Versions**, **Deployments** (טופס רישום: Environment+Version+DeployedAt+הערות), **Customers**, **API Keys** (יצירה/ביטול, המפתח המלא מוצג פעם אחת בלבד ביצירה).

## 8. Login

טופס משתמש/סיסמה בסיסי — תואם את ההחלטה על אותנטיקציה פשוטה ([04-ארכיטקטורה.md](04-ארכיטקטורה.md) סעיף 9). אין self-service signup ב-V1 — משתמשים נוצרים ע"י אדמין.

## 9. עקרונות עיצוב כלליים

- **Desktop-first, לא responsive-מלא** — כלי חקירה פנימי למפתחים, לא אפליקציית צרכן. עדיפות לצפיפות מידע (טבלאות, לא כרטיסים גדולים) על פני "יופי" ויזואלי.
- **בחירת Application+Environment תמיד גלויה וקבועה** — לא רק נוחות, גם תזכורת ויזואלית מתמדת לבידוד שנאכף ב-API.
- **צבע/תווית ה-Kind (Fact/Observation/Hypothesis/Conclusion) עקבי בכל המערכת** — אותה סכימת צבעים בכל מקום שמוצג Finding (כולל תצוגות מקוצרות ברשימות), כדי שהמשתמש ילמד לזהות "זה עדיין ניחוש" במבט אחד, גם בלי לקרוא טקסט.

## 10. מה נשאר לתוצרים הבאים

מדיניות הרשאות מדויקת (מי יכול לבצע "קידום ל-Conclusion", מי אדמין) — **Security Design**, הבא בתור. תוכנית בדיקה ל-workflows האלה (כולל בדיקת ה-6 תרחישים דרך ה-UI בפועל) — **Test Strategy**.

---

*וידאוט מפורט של כל מסך (ברמת pixel/component) לא בסקופ המסמך הזה — זה מסמך ארכיטקטורת-מידע והתנהגות, לא spec עיצובי גרפי; יוחלט תוך כדי מימוש בהתאם לרכיבי Blazor הזמינים.*
