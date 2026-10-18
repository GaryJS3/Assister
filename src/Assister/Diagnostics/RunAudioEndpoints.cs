using Assister.Interactions;

namespace Assister.Diagnostics;

public static class RunAudioEndpoints
{
    public static void MapRunAudio(this WebApplication App)
    {
        App.MapGet("/api/diagnostics/runs/{id:guid}/audio", (Guid Id, RunStore Runs, RunAudioStore Audio, HttpContext Http) =>
        {
            Http.Response.Headers.CacheControl = "no-store";
            if (Runs.Get(Id) is not { } Run) return Results.NotFound();
            // Rich-client recordings retain their existing owner authentication boundary.
            if (Run.Source == "rich-client") return Results.Unauthorized();
            return Results.Ok(new[] { Audio.Info(Id, "input"), Audio.Info(Id, "output") });
        });
        App.MapGet("/api/diagnostics/runs/{id:guid}/audio/{direction}", (Guid Id, string Direction, RunStore Runs, RunAudioStore Audio, HttpContext Http) =>
        {
            Http.Response.Headers.CacheControl = "no-store";
            if (!RunAudioStore.ValidDirection(Direction) || Runs.Get(Id) is not { } Run) return Results.NotFound();
            if (Run.Source == "rich-client") return Results.Unauthorized();
            return Audio.Get(Id, Direction) is { } Wave ? Results.File(Wave, "audio/wav", enableRangeProcessing: true) : Results.NotFound();
        });
    }

    // These routes share the rich-client group's authentication and same-origin filter.
    public static void MapClientRunAudio(this RouteGroupBuilder Group)
    {
        Group.MapGet("/runs/{id:guid}/audio", (Guid Id, InteractionStore Store, IConfiguration Configuration, HttpContext Http) =>
        {
            Http.Response.Headers.CacheControl = "no-store";
            if (Store.ForRun(Id, (string)Http.Items["ClientOwner"]!) is not { } Item) return Results.NotFound();
            var Sizes = Store.AudioSizes(Item.Id);
            var Options = Store.Options(Item.Id);
            var Expires = Item.CreatedAt.AddHours(Math.Clamp(Configuration.GetValue("RichClients:AudioRetentionHours", 24), 1, 168));
            RunAudioClip Clip(string Direction, long? Size, bool Captured) => new(Direction,
                Size is > 0 ? "available" : Captured ? "expired" : "not-captured",
                Size is > 0 ? $"/api/client/runs/{Id}/audio/{Direction}" : null,
                Size is > 0 ? Size.Value + (Direction == "input" ? 44 : 0) : 0, ExpiresAt: Captured || Size is > 0 ? Expires : null);
            return Results.Ok(new[] { Clip("input", Sizes.InputSize, Options.AudioAttachmentId is not null),
                Clip("output", Sizes.OutputSize, Sizes.OutputCaptured) });
        });
        Group.MapGet("/runs/{id:guid}/audio/{direction}", (Guid Id, string Direction, InteractionStore Store, HttpContext Http) =>
        {
            Http.Response.Headers.CacheControl = "no-store";
            if (!RunAudioStore.ValidDirection(Direction) || Store.ForRun(Id, (string)Http.Items["ClientOwner"]!) is not { } Item) return Results.NotFound();
            var Wave = Direction == "input" ? Store.InputAudio(Item.Id) is { } Pcm ? RunAudioStore.Wave(Pcm, 16000, 2, 1) : null : Store.Audio(Item.Id);
            return Wave is null ? Results.NotFound() : Results.File(Wave, "audio/wav", enableRangeProcessing: true);
        });
    }
}
