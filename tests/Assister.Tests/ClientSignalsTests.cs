using System.Text.Json;
using Assister.Contracts;
using Assister.Interactions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Assister.Tests;

public sealed class ClientSignalsTests
{
    [Fact]
    public async Task RestartClosesPendingRequestsAndDoesNotReplayDeviceActions()
    {
        var Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
        var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assister:DataPath"] = Path }).Build();
        using var Services = new ServiceCollection().AddDataProtection().Services.BuildServiceProvider();
        var Auth = new ClientAuthentication(Config, Services.GetRequiredService<IDataProtectionProvider>());
        using var Store = new InteractionStore(Config);
        var Conversation = Store.CreateConversation("tablet"); var Interaction = Store.Submit(Conversation.Id, "tablet", new("test", "key"));
        Guid Id;
        Task<DeviceResponse> Pending;
        using (var Signals = new ClientSignals(Config, Store, Auth))
        {
            Assert.True(Signals.Register("tablet", "tablet", new("android", "Test", ["clipboard.write"])));
            Pending = Signals.RequestAsync(Interaction.Id, "tablet", "clipboard.write", JsonSerializer.SerializeToElement(new { text = "hello" }), TimeSpan.FromSeconds(5), CancellationToken.None);
            Id = Assert.Single(Signals.Pending("tablet")).RequestId;
        }
        Assert.Equal("server_restarted", (await Pending).Error!.Code);
        using var Reopened = new ClientSignals(Config, Store, Auth);
        Assert.Empty(Reopened.Pending("tablet"));
        Assert.Equal("server_restarted", Reopened.Outcome("tablet", Id)!.Error!.Code);
    }
}
