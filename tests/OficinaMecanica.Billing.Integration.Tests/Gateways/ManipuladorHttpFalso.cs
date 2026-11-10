using System.Net;
using System.Text;

namespace OficinaMecanica.Billing.Integration.Tests.Gateways;

public sealed record RequisicaoCapturada(HttpMethod Metodo, Uri Url, IReadOnlyDictionary<string, string> Cabecalhos, string Corpo);

// HttpMessageHandler programável: registra o que o SDK enviou e devolve a resposta escolhida pelo teste.
public sealed class ManipuladorHttpFalso : HttpMessageHandler
{
    private readonly Func<RequisicaoCapturada, CancellationToken, Task<HttpResponseMessage>> _responder;

    public List<RequisicaoCapturada> Requisicoes { get; } = new();

    public ManipuladorHttpFalso(Func<RequisicaoCapturada, HttpResponseMessage> responder)
        : this((requisicao, _) => Task.FromResult(responder(requisicao)))
    {
    }

    public ManipuladorHttpFalso(Func<RequisicaoCapturada, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string corpo) =>
        new(status) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var cabecalhos = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var corpo = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var capturada = new RequisicaoCapturada(request.Method, request.RequestUri!, cabecalhos, corpo);
        Requisicoes.Add(capturada);

        return await _responder(capturada, cancellationToken);
    }
}
