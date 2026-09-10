using System.Net;
using System.Threading.Channels;

namespace GenAIPlatform.UnitTests;

internal sealed class ModelRetryTestHttpMessageHandler(
    Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private readonly Channel<int> sends = Channel.CreateUnbounded<int>();

    public List<string> Keys { get; } = [];
    public List<string> Payloads { get; } = [];
    public int Count => Keys.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Keys.Add(Assert.Single(request.Headers.GetValues("Idempotency-Key")));
        Payloads.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
        sends.Writer.TryWrite(Count);
        return await respond(Count, cancellationToken);
    }

    public async Task WaitForSendAsync(int expected)
    {
        var actual = await sends.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(expected, actual);
    }

    public static HttpResponseMessage Response(HttpStatusCode status = HttpStatusCode.OK)
    {
        return new(status)
        {
            Content = new StringContent(status == HttpStatusCode.OK
                ? "{\"model\":\"test\",\"choices\":[{\"message\":{\"content\":\"accepted\"}}]}"
                : "{}")
        };
    }
}
