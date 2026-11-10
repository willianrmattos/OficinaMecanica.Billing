using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Infrastructure.Security;
using OficinaMecanica.Billing.Integration.Tests.Support;

namespace OficinaMecanica.Billing.Integration.Tests.Api;

// Pipeline HTTP completo (Program.cs, middleware, controllers, MediatR, handlers reais) sobre EF InMemory e
// transporte em memória, com o gateway Fake. A assinatura do webhook é gerada com o mesmo algoritmo do validador.
public class ApiPublicaTests : IClassFixture<BillingApiFactory>
{
    private readonly BillingApiFactory _api;
    private readonly HttpClient _cliente;

    public ApiPublicaTests(BillingApiFactory api)
    {
        _api = api;
        _cliente = api.CreateClient();
    }

    private HttpRequestMessage Webhook(
        string dataId,
        string requestId,
        string? assinatura,
        object? corpo = null,
        bool dataIdNaQuery = true,
        string tipo = "payment")
    {
        var url = dataIdNaQuery ? $"/webhooks/mercadopago?data.id={dataId}&type={tipo}" : "/webhooks/mercadopago";
        var requisicao = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(corpo ?? new { type = tipo, action = "payment.updated", data = new { id = dataId } }),
                Encoding.UTF8,
                "application/json")
        };
        if (assinatura is not null)
            requisicao.Headers.Add("x-signature", assinatura);
        requisicao.Headers.Add("x-request-id", requestId);
        return requisicao;
    }

    private string Assinar(string dataId, string requestId) =>
        AssinaturaWebhookMercadoPago.GerarCabecalho(BillingApiFactory.SegredoDoFake, dataId, requestId, _api.Relogio.GetUtcNow());

    private Task<CenarioDePagamento> NovoCenarioAsync(Func<PagamentoBuilder, PagamentoBuilder>? ajustar = null) =>
        _api.ComBancoAsync(db => CenarioDePagamento.CriarAsync(db, _api.Gateway, _api.Relogio.GetUtcNow(), ajustar));

    // ---- Endpoints anonimos de operacao ----

    [Fact]
    public async Task Health_Anonimo_DeveResponder200()
    {
        var resposta = await _cliente.GetAsync("/health");

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resposta.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }

    [Fact]
    public async Task Metrics_Anonimo_DeveExporNoFormatoPrometheus()
    {
        await _cliente.GetAsync("/health");

        var resposta = await _cliente.GetAsync("/metrics");

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resposta.Content.ReadAsStringAsync()).Should().Contain("http_requests_received_total");
    }

    // O prometheus-net rotula cada requisição com o status final; como os testes rodam em série, a diferença entre duas
    // leituras de /metrics isola a chamada feita entre elas, mesmo com o registro de métricas compartilhado pelo processo.
    private async Task<double> RequisicoesDaSimulacaoPorCodigoAsync(string codigo)
    {
        var texto = await (await _cliente.GetAsync("/metrics")).Content.ReadAsStringAsync();
        return texto.Split('\n')
            .Where(l => l.StartsWith("http_requests_received_total{", StringComparison.Ordinal)
                        && l.Contains($"code=\"{codigo}\"") && l.Contains("controller=\"DevPagamentos\"") && l.Contains("action=\"Aprovar\""))
            .Sum(l => double.Parse(l[(l.LastIndexOf(' ') + 1)..], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Metrics_ErroMapeadoPeloMiddleware_DeveSerContadoComOCodigoRealENaoComoSucesso()
    {
        var antesComo404 = await RequisicoesDaSimulacaoPorCodigoAsync("404");
        var antesComo200 = await RequisicoesDaSimulacaoPorCodigoAsync("200");

        var resposta = await _cliente.PostAsync("/dev/pagamentos/fake-pref-inexistente/aprovar", null);

        resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await RequisicoesDaSimulacaoPorCodigoAsync("404")).Should().Be(antesComo404 + 1, "o erro mapeado para 404 aparece como 404 em /metrics");
        (await RequisicoesDaSimulacaoPorCodigoAsync("200")).Should().Be(antesComo200, "a chamada que falhou não conta como 200");
    }

    // ---- Swagger ----

    [Fact]
    public async Task Swagger_DeveSerGeradoComOsEndpointsOsIdsDeOperacaoEOEsquemaBearer()
    {
        var resposta = await _cliente.GetAsync("/swagger/v1/swagger.json");

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        using var documento = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        var raiz = documento.RootElement;
        raiz.GetProperty("info").GetProperty("title").GetString().Should().Be("OficinaMecanica Billing API");

        var caminhos = raiz.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        caminhos.Should().Contain(new[]
        {
            "/api/orcamentos",
            "/api/orcamentos/ordem-de-servico/{ordemDeServicoId}",
            "/api/pagamentos/ordem-de-servico/{ordemDeServicoId}",
            "/api/pagamentos/{id}/estornar",
            "/webhooks/mercadopago",
        });

        var idsDeOperacao = raiz.GetProperty("paths").EnumerateObject()
            .SelectMany(p => p.Value.EnumerateObject().Select(m => m.Value.GetProperty("operationId").GetString()))
            .ToList();
        idsDeOperacao.Should().Contain(new[]
        {
            "Orcamentos_ObterPorOrdemDeServico", "Orcamentos_Listar", "Pagamentos_ObterPorOrdemDeServico",
            "Pagamentos_Estornar", "Webhooks_ReceberMercadoPago",
        });
        idsDeOperacao.Should().OnlyHaveUniqueItems();

        var esquema = raiz.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        esquema.GetProperty("type").GetString().Should().Be("http");
        esquema.GetProperty("scheme").GetString().Should().Be("bearer");
        esquema.GetProperty("bearerFormat").GetString().Should().Be("JWT");
    }

    [Fact]
    public async Task Swagger_DeveTrazerDescricoesEmPortuguesERespostasDeCadaEndpoint()
    {
        var json = await _cliente.GetStringAsync("/swagger/v1/swagger.json");

        using var documento = JsonDocument.Parse(json);
        var webhook = documento.RootElement.GetProperty("paths").GetProperty("/webhooks/mercadopago").GetProperty("post");
        webhook.GetProperty("summary").GetString().Should().Contain("notificação");
        webhook.GetProperty("responses").EnumerateObject().Select(r => r.Name).Should().BeEquivalentTo("200", "400", "401", "503");

        var estorno = documento.RootElement.GetProperty("paths").GetProperty("/api/pagamentos/{id}/estornar").GetProperty("post");
        estorno.GetProperty("responses").EnumerateObject().Select(r => r.Name).Should().BeEquivalentTo("200", "400", "404");
    }

    [Fact]
    public async Task Swagger_ComPrefixoDaApim_DeveRefletirOPrefixoNoServers()
    {
        var requisicao = new HttpRequestMessage(HttpMethod.Get, "/swagger/v1/swagger.json");
        requisicao.Headers.Add("X-Forwarded-Prefix", "/billingserver");
        requisicao.Headers.Add("X-Forwarded-Proto", "https");
        requisicao.Headers.Add("X-Forwarded-Host", "apimfiap.azure-api.net");

        var resposta = await _cliente.SendAsync(requisicao);

        using var documento = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        documento.RootElement.GetProperty("servers")[0].GetProperty("url").GetString()
            .Should().Be("https://apimfiap.azure-api.net/billingserver");
    }

    [Fact]
    public async Task SwaggerUi_NaRaiz_DeveSerServidoSemAutenticacao()
    {
        var resposta = await _cliente.GetAsync("/");

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resposta.Content.ReadAsStringAsync()).Should().Contain("swagger");
    }

    [Fact]
    public async Task Swagger_ComFakeEmDevelopment_DeveIncluirAsRotasDeSimulacaoDePagamento()
    {
        var json = await _cliente.GetStringAsync("/swagger/v1/swagger.json");

        json.Should().Contain("/dev/pagamentos/{preferenceId}/aprovar").And.Contain("/dev/pagamentos/{preferenceId}/recusar");
    }

    // ---- Webhook: assinatura ----

    [Fact]
    public async Task Webhook_AssinaturaInvalida_DeveResponder401SemVazarDetalhes()
    {
        var resposta = await _cliente.SendAsync(Webhook("123456", "req-invalido", "ts=1,v1=00ff"));

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var corpo = await resposta.Content.ReadAsStringAsync();
        corpo.Should().Contain("Assinatura do webhook inválida.").And.NotContain("ts=").And.NotContain("segredo");
    }

    [Fact]
    public async Task Webhook_SemCabecalhoDeAssinatura_DeveResponder401()
    {
        var resposta = await _cliente.SendAsync(Webhook("123456", "req-sem-assinatura", assinatura: null));

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Webhook_AssinaturaComTsForaDaTolerancia_DeveResponder401()
    {
        var antiga = AssinaturaWebhookMercadoPago.GerarCabecalho(
            BillingApiFactory.SegredoDoFake, "123456", "req-ts-antigo", _api.Relogio.GetUtcNow().AddMinutes(-10));

        var resposta = await _cliente.SendAsync(Webhook("123456", "req-ts-antigo", antiga));

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Webhook_SemRequestId_DeveResponder400PorValidacao()
    {
        var requisicao = Webhook("123456", "ignorado", "ts=1,v1=00ff");
        requisicao.Headers.Remove("x-request-id");

        var resposta = await _cliente.SendAsync(requisicao);

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resposta.Content.ReadAsStringAsync()).Should().Contain("x-request-id");
    }

    [Fact]
    public async Task Webhook_SemIdDoPagamento_DeveResponder400PorValidacao()
    {
        var resposta = await _cliente.SendAsync(
            Webhook(string.Empty, "req-sem-id", "ts=1,v1=00ff", corpo: new { type = "payment" }, dataIdNaQuery: false));

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resposta.Content.ReadAsStringAsync()).Should().Contain("data.id");
    }

    [Theory]
    [InlineData("merchant_order")]
    [InlineData("subscription_preapproval")]
    [InlineData("")]
    public async Task Webhook_TopicoDiferenteDePayment_DeveConfirmarSemProcessar(string tipo)
    {
        var resposta = await _cliente.SendAsync(Webhook("123456", "req-outro-topico", assinatura: null, tipo: tipo));

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- Webhook: processamento ----

    [Fact]
    public async Task Webhook_PagamentoAprovadoNoGateway_DeveAprovarOPagamentoEMarcarOOrcamentoComoPago()
    {
        var cenario = await NovoCenarioAsync();
        var pagamentoDoGateway = _api.Gateway.AprovarPreferencia(cenario.PreferenceId);
        var requestId = Guid.NewGuid().ToString();

        var resposta = await _cliente.SendAsync(Webhook(pagamentoDoGateway.Id, requestId, Assinar(pagamentoDoGateway.Id, requestId)));

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        var pagamento = await _api.ComBancoAsync(db => db.Pagamentos.Include(p => p.Tentativas).SingleAsync(p => p.Id == cenario.Pagamento.Id));
        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
        pagamento.GatewayPaymentIdAprovado.Should().Be(pagamentoDoGateway.Id);
        pagamento.Tentativas.Should().ContainSingle().Which.StatusGateway.Should().Be("approved");
        var orcamento = await _api.ComBancoAsync(db => db.Orcamentos.SingleAsync(o => o.Id == cenario.Orcamento.Id));
        orcamento.Status.Should().Be(StatusOrcamento.Pago);
        var recebido = await _api.ComBancoAsync(db => db.WebhooksRecebidos.SingleAsync(w => w.RequestId == requestId));
        recebido.Processado.Should().BeTrue();
    }

    [Fact]
    public async Task Webhook_MesmoRequestIdReentregue_DeveResponder200SemProcessarDeNovo()
    {
        var cenario = await NovoCenarioAsync();
        var pagamentoDoGateway = _api.Gateway.AprovarPreferencia(cenario.PreferenceId);
        var requestId = Guid.NewGuid().ToString();
        var assinatura = Assinar(pagamentoDoGateway.Id, requestId);

        var primeira = await _cliente.SendAsync(Webhook(pagamentoDoGateway.Id, requestId, assinatura));
        var segunda = await _cliente.SendAsync(Webhook(pagamentoDoGateway.Id, requestId, assinatura));

        primeira.StatusCode.Should().Be(HttpStatusCode.OK);
        segunda.StatusCode.Should().Be(HttpStatusCode.OK);
        var recebidos = await _api.ComBancoAsync(db => db.WebhooksRecebidos.CountAsync(w => w.RequestId == requestId));
        recebidos.Should().Be(1);
    }

    [Fact]
    public async Task Webhook_DataIdSoNoCorpo_DeveUsarOIdDoCorpoNaAssinaturaENoProcessamento()
    {
        var cenario = await NovoCenarioAsync();
        var pagamentoDoGateway = _api.Gateway.AprovarPreferencia(cenario.PreferenceId);
        var requestId = Guid.NewGuid().ToString();

        var resposta = await _cliente.SendAsync(
            Webhook(pagamentoDoGateway.Id, requestId, Assinar(pagamentoDoGateway.Id, requestId), dataIdNaQuery: false));

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        var pagamento = await _api.ComBancoAsync(db => db.Pagamentos.SingleAsync(p => p.Id == cenario.Pagamento.Id));
        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
    }

    [Fact]
    public async Task Webhook_DataIdNumericoNoCorpo_DeveSerAceito()
    {
        var cenario = await NovoCenarioAsync();
        var pagamentoDoGateway = _api.Gateway.AprovarPreferencia(cenario.PreferenceId);
        var requestId = Guid.NewGuid().ToString();
        var requisicao = new HttpRequestMessage(HttpMethod.Post, "/webhooks/mercadopago")
        {
            Content = new StringContent(
                $"{{\"type\":\"payment\",\"action\":\"payment.created\",\"data\":{{\"id\":{pagamentoDoGateway.Id}}}}}", Encoding.UTF8, "application/json")
        };
        requisicao.Headers.Add("x-signature", Assinar(pagamentoDoGateway.Id, requestId));
        requisicao.Headers.Add("x-request-id", requestId);

        var resposta = await _cliente.SendAsync(requisicao);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        var pagamento = await _api.ComBancoAsync(db => db.Pagamentos.SingleAsync(p => p.Id == cenario.Pagamento.Id));
        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
    }

    [Fact]
    public async Task Webhook_TentativaRecusada_DeveRegistrarATentativaSemEncerrarOPagamento()
    {
        var cenario = await NovoCenarioAsync();
        var pagamentoDoGateway = _api.Gateway.RecusarPreferencia(cenario.PreferenceId);
        var requestId = Guid.NewGuid().ToString();

        var resposta = await _cliente.SendAsync(Webhook(pagamentoDoGateway.Id, requestId, Assinar(pagamentoDoGateway.Id, requestId)));

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        var pagamento = await _api.ComBancoAsync(db => db.Pagamentos.Include(p => p.Tentativas).SingleAsync(p => p.Id == cenario.Pagamento.Id));
        pagamento.Status.Should().Be(StatusPagamento.Pendente);
        pagamento.Tentativas.Should().ContainSingle().Which.StatusGateway.Should().Be("rejected");
    }

    [Fact]
    public async Task Webhook_GatewayAindaNaoConheceOPagamento_DeveResponder503ComRetryAfterParaOMercadoPagoReentregar()
    {
        var requestId = Guid.NewGuid().ToString();

        var resposta = await _cliente.SendAsync(Webhook("999000111", requestId, Assinar("999000111", requestId)));

        resposta.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        resposta.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(30));
        (await _api.ComBancoAsync(db => db.WebhooksRecebidos.CountAsync(w => w.RequestId == requestId)))
            .Should().Be(0, "a falha transitória não pode marcar o webhook como recebido");
    }

    [Fact]
    public async Task Webhook_PagamentoDoGatewayQueNaoTemPagamentoLocal_DeveResponder200EIgnorar()
    {
        var preferenciaOrfa = await _api.Gateway.CriarPreferenciaAsync(
            new(Guid.NewGuid(), "Sem pagamento local", 50m, DateTimeOffset.UtcNow.AddDays(1)));
        var pagamentoDoGateway = _api.Gateway.AprovarPreferencia(preferenciaOrfa.PreferenceId);
        var requestId = Guid.NewGuid().ToString();

        var resposta = await _cliente.SendAsync(Webhook(pagamentoDoGateway.Id, requestId, Assinar(pagamentoDoGateway.Id, requestId)));

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Webhook_ValorAprovadoDiferenteDoPagamento_DeveResponder200SemAprovar()
    {
        var cenario = await NovoCenarioAsync();
        var pagamentoDoGateway = _api.Gateway.RegistrarPagamento(cenario.PreferenceId, "approved", "accredited", valor: 0.01m);
        var requestId = Guid.NewGuid().ToString();

        var resposta = await _cliente.SendAsync(Webhook(pagamentoDoGateway.Id, requestId, Assinar(pagamentoDoGateway.Id, requestId)));

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        var pagamento = await _api.ComBancoAsync(db => db.Pagamentos.SingleAsync(p => p.Id == cenario.Pagamento.Id));
        pagamento.Status.Should().Be(StatusPagamento.Pendente);
    }

    // ---- Simulacao de pagamento (dev) ----

    [Fact]
    public async Task Dev_Aprovar_DeveDispararOWebhookComAssinaturaValidaEAprovarOPagamento()
    {
        var cenario = await NovoCenarioAsync();

        var resposta = await _cliente.PostAsync($"/dev/pagamentos/{cenario.PreferenceId}/aprovar", null);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        corpo.GetProperty("status").GetString().Should().Be("approved");
        var pagamento = await _api.ComBancoAsync(db => db.Pagamentos.SingleAsync(p => p.Id == cenario.Pagamento.Id));
        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
        pagamento.GatewayPaymentIdAprovado.Should().Be(corpo.GetProperty("gatewayPaymentId").GetString());
    }

    [Fact]
    public async Task Dev_Recusar_DeveRegistrarARecusaSemEncerrarOPagamento()
    {
        var cenario = await NovoCenarioAsync();

        var resposta = await _cliente.PostAsync($"/dev/pagamentos/{cenario.PreferenceId}/recusar", null);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        var pagamento = await _api.ComBancoAsync(db => db.Pagamentos.Include(p => p.Tentativas).SingleAsync(p => p.Id == cenario.Pagamento.Id));
        pagamento.Status.Should().Be(StatusPagamento.Pendente);
        pagamento.Tentativas.Should().ContainSingle().Which.StatusGateway.Should().Be("rejected");
    }

    [Fact]
    public async Task Dev_PreferenciaDesconhecida_DeveResponder404()
    {
        var resposta = await _cliente.PostAsync("/dev/pagamentos/fake-pref-inexistente/aprovar", null);

        resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
