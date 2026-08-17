using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LogsPlatform.Infrastructure;

public class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter() : base(
        toProvider => toProvider.Kind == DateTimeKind.Local
            ? toProvider.ToUniversalTime()
            : DateTime.SpecifyKind(toProvider, DateTimeKind.Utc),
        fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc))
    {
    }
}
