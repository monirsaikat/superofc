using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SuperOfc.Domain.Common;

namespace SuperOfc.Infrastructure.Persistence.Converters;

/// <summary>Money is stored as INTEGER minor units so SUM and ORDER BY work in SQL.</summary>
public sealed class MoneyConverter() : ValueConverter<Money, long>(m => m.Minor, v => new Money(v));

/// <summary>All DateTime values are UTC; SQLite loses the Kind so it is restored on read.</summary>
public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

public sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v == null ? v : v.Value.Kind == DateTimeKind.Local ? v.Value.ToUniversalTime() : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc),
    v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));

/// <summary>Company wall-clock times (punches, first in, last out): stored and read without any time zone.</summary>
public sealed class LocalDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => DateTime.SpecifyKind(v, DateTimeKind.Unspecified),
    v => DateTime.SpecifyKind(v, DateTimeKind.Unspecified));

public sealed class NullableLocalDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Unspecified),
    v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Unspecified));
