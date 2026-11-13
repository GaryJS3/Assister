using System.Net;
using Assister.Client;

namespace Assister.IntegrationTests;

public sealed class ClientRecoveryTests
{
    [Fact]
    public async Task NotificationIsDeliveredAfterTheReadRecovers()
    {
        using var Handler = new Replies(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var Http = new HttpClient(Handler) { BaseAddress = new("http://localhost/") };
        using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var Observer = new AssisterClient(Http).ObserveNotificationsAsync(Deadline.Token).GetAsyncEnumerator();
        Assert.True(await Observer.MoveNextAsync());
        Assert.Equal("Recovered", Observer.Current.Text);
        Assert.Equal(2, Handler.Calls);
    }

    [Fact]
    public async Task NotificationObserverRecoversFromTransientHttpFailure()
    {
        using var Handler = new Replies(HttpStatusCode.ServiceUnavailable, HttpStatusCode.Unauthorized);
        using var Http = new HttpClient(Handler) { BaseAddress = new("http://localhost/") };
        var Client = new AssisterClient(Http);
        await using var Observer = Client.ObserveNotificationsAsync().GetAsyncEnumerator();
        var Error = await Assert.ThrowsAsync<HttpRequestException>(() => Observer.MoveNextAsync().AsTask());
        Assert.Equal(HttpStatusCode.Unauthorized, Error.StatusCode);
        Assert.Equal(2, Handler.Calls);
    }

    [Fact]
    public async Task CancellationStopsRecoveryBackoff()
    {
        using var Deadline = new CancellationTokenSource();
        using var Handler = new Replies(HttpStatusCode.ServiceUnavailable);
        using var Http = new HttpClient(Handler) { BaseAddress = new("http://localhost/") };
        var Client = new AssisterClient(Http, ConnectionState: _ => Deadline.Cancel());
        await using var Observer = Client.ObserveNotificationsAsync(Deadline.Token).GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Observer.MoveNextAsync().AsTask());
        Assert.Equal(1, Handler.Calls);
    }

    private sealed class Replies(params HttpStatusCode[] Statuses) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            var Status = Statuses[Math.Min(Calls++, Statuses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = Status == HttpStatusCode.OK ? System.Net.Http.Json.JsonContent.Create(new[]
                {
                    new Assister.Contracts.ClientNotification(Guid.NewGuid(), "reminder", "Recovered", DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), "notify", null, "pending")
                }) : null
            });
        }
    }
}
