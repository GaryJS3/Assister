using Assister.Diagnostics;
using Assister.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assister.Intents;

public static class IntentEndpoints
{
    public static void MapIntents(this WebApplication App)
    {
        var Api = App.MapGroup("/api/intents");
        Api.MapGet("", async (IntentStore Store, CancellationToken Token) => Results.Ok(await Store.DefinitionsAsync(Token)));
        Api.MapGet("/catalog", () => Results.Ok(new { handlers = IntentCatalog.Handlers, nativePhrases = IntentCatalog.NativePhrases, slots = new[] { "{target}", "{target:light}", "{target:switch}", "{brightness:percent}", "{area}" } }));
        Api.MapPut("/{id}", async (string Id, IntentDefinition Definition, IntentStore Store, CancellationToken Token) =>
        {
            if (Id != Definition.Id) { return Results.BadRequest(new { error = "The intent identifier does not match." }); }
            try
            {
                await Store.SaveAsync(Definition, Token);
                return Results.Ok((await Store.DefinitionsAsync(Token)).Single(Row => Row.Id == Id));
            }
            catch (ArgumentException Error) { return Results.BadRequest(new { error = Error.Message }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "This intent changed. Reload before saving." }); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "The intent could not be saved. Reload and try again." }); }
        });
        Api.MapDelete("/{id}", async (string Id, long Version, AssisterDbContext Database, CancellationToken Token) =>
        {
            if (Id.StartsWith("builtin-", StringComparison.Ordinal) || Id == "catalog-initialized") { return Results.BadRequest(new { error = "Disable a built-in instead of deleting it." }); }
            var Row = await Database.IntentDefinitions.SingleOrDefaultAsync(Row => Row.Id == Id, Token);
            if (Row is null) { return Results.NotFound(); }
            if (Row.Version != Version) { return Results.Conflict(new { error = "This intent changed. Reload before deleting it." }); }
            Database.IntentDefinitions.Remove(Row);
            try { await Database.SaveChangesAsync(Token); return Results.NoContent(); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "This intent changed. Reload before deleting it." }); }
        });
        Api.MapPost("/inspect", async (IntentInspectRequest Request, IntentWorkbench Workbench, CancellationToken Token) =>
        {
            try { return Results.Ok(await Workbench.InspectAsync(Request, Token)); }
            catch (ArgumentException Error) { return Results.BadRequest(new { error = Error.Message }); }
        });
        Api.MapPost("/execute", async (IntentExecuteRequest Request, IntentWorkbench Workbench, RunStore Runs, CancellationToken Token) =>
        {
            try { return Results.Ok(await Workbench.ExecuteAsync(Request, Runs, Token)); }
            catch (ArgumentException Error) { return Results.BadRequest(new { error = Error.Message }); }
            catch (InvalidOperationException Error) { return Results.Conflict(new { error = Error.Message }); }
        });
        Api.MapGet("/examples", async (IntentStore Store, CancellationToken Token) => Results.Ok(await Store.ExamplesAsync(Token)));
        Api.MapPut("/examples/{id}", async (string Id, IntentExample Example, IntentStore Store, CancellationToken Token) =>
        {
            if (Id != Example.Id) { return Results.BadRequest(new { error = "The example identifier does not match." }); }
            try { await Store.SaveExampleAsync(Example, Token); return Results.Ok(Example); }
            catch (ArgumentException Error) { return Results.BadRequest(new { error = Error.Message }); }
        });
        Api.MapDelete("/examples/{id}", async (string Id, AssisterDbContext Database, CancellationToken Token) =>
        {
            var Row = await Database.IntentExamples.FindAsync([Id], Token);
            if (Row is null) { return Results.NotFound(); }
            Database.IntentExamples.Remove(Row);
            await Database.SaveChangesAsync(Token);
            return Results.NoContent();
        });
        Api.MapPost("/tests", async (IntentWorkbench Workbench, CancellationToken Token) => Results.Ok(await Workbench.TestAsync(Token)));
    }
}
