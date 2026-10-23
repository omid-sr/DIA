using System.Data;
using Microsoft.Data.SqlClient;

namespace Api.DIA.Data;

internal static class SqlParams
{
    public static SqlParameter Guid(string name, Guid value) =>
        new(name, SqlDbType.UniqueIdentifier) { Value = value };

    public static SqlParameter Money(string name, decimal value) =>
        new(name, SqlDbType.Decimal) { Precision = 19, Scale = 4, Value = value };

    public static SqlParameter MoneyOrNull(string name, decimal? value) =>
        new(name, SqlDbType.Decimal)
        {
            Precision = 19,
            Scale = 4,
            Value = value.HasValue ? value.Value : DBNull.Value
        };

    public static SqlParameter Text(string name, string value, int size) =>
        new(name, SqlDbType.NVarChar, size) { Value = value };
}
