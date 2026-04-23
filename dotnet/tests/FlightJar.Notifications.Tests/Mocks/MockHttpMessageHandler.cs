namespace FlightJar.Notifications.Tests.Mocks;

internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }
    public List<HttpRequestMessage> Requests { get; } = new();
    public int CallCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        CallCount++;
        // Snapshot body since HttpClient disposes the request after send.
        if (request.Content is not null)
        {
            _ = await request.Content.ReadAsStringAsync(ct);
        }
        Requests.Add(request);
        if (Handler is null)
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
        return Handler(request);
    }
}
