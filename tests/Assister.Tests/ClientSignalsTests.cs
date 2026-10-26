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
    public void DeviceInventorySurvivesRestartAndDisconnectWithoutClaimingOnline()
    {
        var Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
        var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assister:DataPath"] = Path }).Build();
        using var Services = new ServiceCollection().AddDataProtection().Services.BuildServiceProvider();
        var Auth = new ClientAuthentication(Config, Services.GetRequiredService<IDataProtectionProvider>());
        using var Store = new InteractionStore(Config);
        DateTimeOffset? First;
        using (var Signals = new ClientSignals(Config, Store, Auth))
        {
            Assert.True(Signals.Register("desktop", "desktop", new("windows", "Office", ["clipboard.write"], "1.2.3",
                new("Windows 11", "Example", "Desktop", "serial-123"))));
            First = Assert.Single(Signals.Clients("desktop")).FirstRegisteredAt;
            Assert.True(Signals.SetGroups("desktop", [" Upstairs ", "upstairs", "Office"]));
            Signals.Disconnect("desktop");
            Assert.False(Assert.Single(Signals.Clients("desktop")).Online);
            Assert.False(Signals.Heartbeat("desktop"));
        }
        using var Reopened = new ClientSignals(Config, Store, Auth);
        var Device = Assert.Single(Reopened.Devices());
        Assert.False(Device.Online);
        Assert.False(Reopened.Heartbeat("desktop"));
        Assert.True(Reopened.SetGroups("desktop", ["Upstairs", "Office"]));
        Assert.Equal("1.2.3", Device.ClientVersion);
        Assert.Equal("serial-123", Device.Hardware!.SerialNumber);
        Assert.Equal(new[] { "Upstairs", "Office" }, Device.Groups);
        Assert.Empty(Reopened.Clients("other-owner"));
        Assert.True(Reopened.Register("desktop", "desktop", new("windows", "Office renamed", [], "1.2.4")));
        Device = Assert.Single(Reopened.Devices());
        Assert.True(Device.Online);
        Assert.Equal(First, Device.FirstRegisteredAt);
        Assert.Equal(new[] { "Upstairs", "Office" }, Device.Groups);
        Assert.Equal("Office renamed", Device.DeviceName);
        Assert.Equal("serial-123", Device.Hardware!.SerialNumber);
    }

    [Fact]
    public void GroupNotificationsRespectOwnerMembershipAndSurviveRestart()
    {
        var Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
        var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Assister:DataPath"] = Path, ["RichClients:Clients:desktop:Owner"] = "home",
            ["RichClients:Clients:phone:Owner"] = "home"
        }).Build();
        using var Services = new ServiceCollection().AddDataProtection().Services.BuildServiceProvider();
        var Auth = new ClientAuthentication(Config, Services.GetRequiredService<IDataProtectionProvider>());
        using var Store = new InteractionStore(Config);
        using var Signals = new ClientSignals(Config, Store, Auth);
        Signals.Register("desktop", "home", new("windows", "Office", []));
        Signals.Register("phone", "home", new("android", "Phone", []));
        Signals.Register("stranger", "stranger", new("android", "Other household", []));
        Signals.SetGroups("desktop", ["Upstairs"]);
        Signals.SetGroups("stranger", ["Upstairs"]);
        using (var Notices = new NotificationStore(Config, Signals: Signals))
        {
            Notices.Create("home", new("alarm", "Wake up", "group-alarm", TargetGroup: "upstairs"));
            Assert.Single(Notices.List("home", "desktop", true));
            Assert.Empty(Notices.List("home", "phone", true));
            Assert.Empty(Notices.List("stranger", "stranger", true));
            Assert.Throws<ArgumentException>(() => Notices.Create("home", new("alarm", "Invalid", "bad", TargetClientId: "phone", TargetGroup: "upstairs")));
        }
        using var Reopened = new NotificationStore(Config, Signals: Signals);
        Assert.Single(Reopened.List("home", "desktop", true));
        Signals.SetGroups("phone", ["Upstairs"]);
        Assert.Single(Reopened.List("home", "phone", true));
        Assert.Throws<ArgumentException>(() => Signals.SetGroups("phone", [new string('x', 65)]));
        Assert.False(Signals.Register("phone", "home", new("android", "Phone", [], Hardware: new(SerialNumber: new string('x', 257)))));
    }

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
