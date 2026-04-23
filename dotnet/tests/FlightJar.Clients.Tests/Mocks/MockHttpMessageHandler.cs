namespace FlightJar.Clients.Tests.Mocks;

/// <summary>
/// In-memory <see cref="HttpMessageHandler"/> for tests. Callers set
/// <see cref="Handler"/> to a function that maps request -> response;
/// <see cref="CallCount"/> counts invocations.
/// </summary>
internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }
    public int CallCount { get; private set; }
    public List<HttpRequestMessage> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        Requests.Add(request);
        if (Handler is null)
        {
            throw new InvalidOperationException("MockHttpMessageHandler: no Handler set");
        }
        return Task.FromResult(Handler(request));
    }
}
