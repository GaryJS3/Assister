using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Assister.Persistence;

public sealed class LocalStore(AssisterDbContext Database)
{
    private async Task<DbCommand> CommandAsync(string Sql, object?[] Values, CancellationToken Token)
    {
        await Database.Database.OpenConnectionAsync(Token);
        var Command = Database.Database.GetDbConnection().CreateCommand();
        Command.CommandText = Sql;
        for (var Index = 0; Index < Values.Length; Index++)
        {
            var Parameter = Command.CreateParameter();
            Parameter.ParameterName = "$p" + Index;
            Parameter.Value = Values[Index] ?? DBNull.Value;
            Command.Parameters.Add(Parameter);
        }
        return Command;
    }
    public async Task<int> ExecuteAsync(string Sql, CancellationToken Token, params object?[] Values)
    {
        await using var Command = await CommandAsync(Sql, Values, Token);
        return await Command.ExecuteNonQueryAsync(Token);
    }
    public async Task<IReadOnlyList<string[]>> QueryAsync(string Sql, CancellationToken Token, params object?[] Values)
    {
        await using var Command = await CommandAsync(Sql, Values, Token);
        await using var Reader = await Command.ExecuteReaderAsync(Token);
        var Rows = new List<string[]>();
        while (await Reader.ReadAsync(Token))
        {
            Rows.Add(Enumerable.Range(0, Reader.FieldCount).Select(Index => Reader.IsDBNull(Index) ? "" : Convert.ToString(Reader.GetValue(Index), System.Globalization.CultureInfo.InvariantCulture)!).ToArray());
            if (Rows.Count > 100) { throw new InvalidDataException("Query exceeded its row limit."); }
        }
        return Rows;
    }
}
