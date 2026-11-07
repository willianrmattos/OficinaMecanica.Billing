using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Infrastructure.Gateways.MercadoPago;
using OficinaMecanica.Billing.Infrastructure.Options;
using Xunit.Sdk;

namespace OficinaMecanica.Billing.Integration.Tests.Gateways;

public class MercadoPagoGatewayTests
{
    private const string AccessToken = "TEST-token-de-teste";
    private const string RespostaDePreferencia =
        "{\"id\":\"123456-pref-abc\",\"init_point\":\"https://www.mercadopago.com.br/checkout/v1/redirect?pref_id=123456-pref-abc\"}";
    private const string RespostaNaoEncontrado = "{\"message\":\"Payment not found\",\"error\":\"not_found\",\"status\":404,\"cause\":[]}";

    private static readonly DateTimeOffset ExpiraEm = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan LimiteDoTeste = TimeSpan.FromSeconds(5);

    private static OpcoesDoMercadoPago OpcoesPadrao() => new()
    {
        AccessToken = AccessToken,
        WebhookSecret = "segredo",
        NotificationBaseUrl = "https://apimfiap.azure-api.net/billingserver/",
    };

    private static MercadoPagoGateway CriarGateway(ManipuladorHttpFalso manipulador, OpcoesDoMercadoPago? opcoes = null, TimeSpan? timeout = null) =>
        new(
            new HttpClient(manipulador) { Timeout = timeout ?? TimeSpan.FromSeconds(10) },
            Options.Create(opcoes ?? OpcoesPadrao()),
            NullLogger<MercadoPagoGateway>.Instance);

    private static CriarPreferenciaGatewayRequest RequestPadrao(Guid? pagamentoId = null, decimal valor = 192.30m) =>
        new(pagamentoId ?? Guid.NewGuid(), "Ordem de serviço OS-20261107-ABC123", valor, ExpiraEm);

    // Falha rápido em vez de travar: com o retry do SDK ligado a chamada ficaria em backoff por muito mais que o limite, e
    // sem este limite o teste esperaria o backoff inteiro em vez de apontar o retry.
    private static async Task<Exception> CapturarFalhaAsync(Func<Task> chamada)
    {
        try
        {
            await chamada().WaitAsync(LimiteDoTeste);
        }
        catch (TimeoutException)
        {
            throw new XunitException(
                $"A chamada não terminou em {LimiteDoTeste.TotalSeconds}s: o retry do SDK deve estar repetindo a requisição.");
        }
        catch (Exception excecao)
        {
            return excecao;
        }

        throw new XunitException("A chamada deveria ter falhado.");
    }

    private static JsonElement CorpoDe(RequisicaoCapturada requisicao) => JsonDocument.Parse(requisicao.Corpo).RootElement;

    // ---- CriarPreferenciaAsync ----

    [Fact]
    public async Task CriarPreferencia_DeveMontarUmaPreferenciaComItemUnicoEReferenciaDoPagamento()
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(HttpStatusCode.Created, RespostaDePreferencia));
        var request = RequestPadrao();

        await CriarGateway(manipulador).CriarPreferenciaAsync(request);

        var requisicao = manipulador.Requisicoes.Should().ContainSingle().Subject;
        requisicao.Metodo.Should().Be(HttpMethod.Post);
        requisicao.Url.AbsolutePath.Should().Be("/checkout/preferences");
        var corpo = CorpoDe(requisicao);
        var itens = corpo.GetProperty("items");
        itens.GetArrayLength().Should().Be(1);
        itens[0].GetProperty("id").GetString().Should().Be(request.PagamentoId.ToString());
        itens[0].GetProperty("title").GetString().Should().Be(request.Descricao);
        itens[0].GetProperty("quantity").GetInt32().Should().Be(1);
        itens[0].GetProperty("currency_id").GetString().Should().Be("BRL");
        itens[0].GetProperty("unit_price").GetDecimal().Should().Be(192.30m);
        corpo.GetProperty("external_reference").GetString().Should().Be(request.PagamentoId.ToString());
    }

    [Fact]
    public async Task CriarPreferencia_DeveDevolverOIdEOLinkDeCheckoutDaPreferenciaCriada()
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(HttpStatusCode.Created, RespostaDePreferencia));

        var preferencia = await CriarGateway(manipulador).CriarPreferenciaAsync(RequestPadrao());

        preferencia.PreferenceId.Should().Be("123456-pref-abc");
        preferencia.LinkPagamento.Should().Be("https://www.mercadopago.com.br/checkout/v1/redirect?pref_id=123456-pref-abc");
    }

    [Fact]
    public async Task CriarPreferencia_DeveApontarONotificationUrlParaORotaDoWebhookSemBarraDuplicada()
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(HttpStatusCode.Created, RespostaDePreferencia));

        await CriarGateway(manipulador).CriarPreferenciaAsync(RequestPadrao());

        CorpoDe(manipulador.Requisicoes[0]).GetProperty("notification_url").GetString()
            .Should().Be("https://apimfiap.azure-api.net/billingserver/webhooks/mercadopago");
    }

    [Fact]
    public async Task CriarPreferencia_DeveExpirarNoPrazoDoPagamento()
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(HttpStatusCode.Created, RespostaDePreferencia));

        await CriarGateway(manipulador).CriarPreferenciaAsync(RequestPadrao());

        var corpo = CorpoDe(manipulador.Requisicoes[0]);
        corpo.GetProperty("expires").GetBoolean().Should().BeTrue();
        DateTimeOffset.Parse(corpo.GetProperty("expiration_date_to").GetString()!).Should().Be(ExpiraEm);
    }

    [Fact]
    public async Task CriarPreferencia_DeveExcluirOBoletoDosMeiosDePagamento()
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(HttpStatusCode.Created, RespostaDePreferencia));

        await CriarGateway(manipulador).CriarPreferenciaAsync(RequestPadrao());

        var excluidos = CorpoDe(manipulador.Requisicoes[0]).GetProperty("payment_methods").GetProperty("excluded_payment_types");
        excluidos.EnumerateArray().Select(e => e.GetProperty("id").GetString()).Should().Equal("ticket");
    }

    [Fact]
    public async Task CriarPreferencia_ComBackUrls_DeveEnviarAsUrlsEPedirRetornoAutomaticoNaAprovacao()
    {
        var opcoes = OpcoesPadrao();
        opcoes.BackUrlSucesso = "https://oficina.exemplo/pagamento/sucesso";
        opcoes.BackUrlPendente = "https://oficina.exemplo/pagamento/pendente";
        opcoes.BackUrlFalha = "https://oficina.exemplo/pagamento/falha";
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(HttpStatusCode.Created, RespostaDePreferencia));

        await CriarGateway(manipulador, opcoes).CriarPreferenciaAsync(RequestPadrao());

        var corpo = CorpoDe(manipulador.Requisicoes[0]);
        var backUrls = corpo.GetProperty("back_urls");
        backUrls.GetProperty("success").GetString().Should().Be("https://oficina.exemplo/pagamento/sucesso");
        backUrls.GetProperty("pending").GetString().Should().Be("https://oficina.exemplo/pagamento/pendente");
        backUrls.GetProperty("failure").GetString().Should().Be("https://oficina.exemplo/pagamento/falha");
        corpo.GetProperty("auto_return").GetString().Should().Be("approved");
    }

    [Fact]
    public async Task CriarPreferencia_SemBackUrls_NaoDeveEnviarBackUrlsNemAutoReturn()
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(HttpStatusCode.Created, RespostaDePreferencia));

        await CriarGateway(manipulador).CriarPreferenciaAsync(RequestPadrao());

        var corpo = CorpoDe(manipulador.Requisicoes[0]);
        corpo.TryGetProperty("back_urls", out _).Should().BeFalse();
        corpo.TryGetProperty("auto_return", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CriarPreferencia_DeveUsarOTokenDaConfiguracaoENaoDeixarOSdkRepetirAChamada()
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(HttpStatusCode.InternalServerError, "{}"));

        await CapturarFalhaAsync(() => CriarGateway(manipulador).CriarPreferenciaAsync(RequestPadrao()));

        manipulador.Requisicoes.Should().ContainSingle("o retry do cliente fica desligado para não somar timeouts com a transação aberta");
        manipulador.Requisicoes[0].Cabecalhos["Authorization"].Should().Be($"Bearer {AccessToken}");
    }

    [Fact]
    public async Task CriarPreferencia_DeveEnviarAChaveDeIdempotenciaDerivadaDoPagamento()
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(HttpStatusCode.Created, RespostaDePreferencia));
        var request = RequestPadrao();

        await CriarGateway(manipulador).CriarPreferenciaAsync(request);

        manipulador.Requisicoes[0].Cabecalhos["X-Idempotency-Key"].Should().Be($"preference-{request.PagamentoId}");
    }

    [Fact]
    public async Task CriarPreferencia_TituloMuitoLongo_DeveTruncarParaOLimiteDoGateway()
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(HttpStatusCode.Created, RespostaDePreferencia));
        var request = RequestPadrao() with { Descricao = new string('x', 400) };

        await CriarGateway(manipulador).CriarPreferenciaAsync(request);

        CorpoDe(manipulador.Requisicoes[0]).GetProperty("items")[0].GetProperty("title").GetString()!.Length.Should().Be(250);
    }

    [Fact]
    public async Task CriarPreferencia_ValorComMaisDeDuasCasas_DeveNormalizarParaDuasCasas()
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(HttpStatusCode.Created, RespostaDePreferencia));

        await CriarGateway(manipulador).CriarPreferenciaAsync(RequestPadrao(valor: 10.005m));

        CorpoDe(manipulador.Requisicoes[0]).GetProperty("items")[0].GetProperty("unit_price").GetDecimal().Should().Be(10.01m);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task CriarPreferencia_RecusaDefinitivaDoGateway_DeveVirarPagamentoRecusadoSemVazarOCorpo(HttpStatusCode status)
    {
        var manipulador = new ManipuladorHttpFalso(_ =>
            ManipuladorHttpFalso.Json(status, "{\"message\":\"invalid payer 12345678909 cliente@exemplo.com\",\"error\":\"bad_request\",\"status\":400,\"cause\":[]}"));

        var act = () => CriarGateway(manipulador).CriarPreferenciaAsync(RequestPadrao());

        var excecao = (await act.Should().ThrowAsync<PagamentoRecusadoPeloGatewayException>()).Which;
        excecao.Message.Should().Be("O gateway recusou a criação da preferência de pagamento.");
        excecao.Message.Should().NotContain("12345678909").And.NotContain("cliente@exemplo.com");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task CriarPreferencia_FalhaTecnicaOuDeCredencial_DevePropagarSemVirarRecusa(HttpStatusCode status)
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(status, "{\"message\":\"falha\",\"status\":" + (int)status + "}"));

        var excecao = await CapturarFalhaAsync(() => CriarGateway(manipulador).CriarPreferenciaAsync(RequestPadrao()));

        excecao.Should().NotBeOfType<PagamentoRecusadoPeloGatewayException>();
        manipulador.Requisicoes.Should().ContainSingle("sem retry no cliente");
    }

    [Fact]
    public async Task CriarPreferencia_Timeout_DevePropagarSemVirarRecusa()
    {
        var manipulador = new ManipuladorHttpFalso(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });

        var act = () => CriarGateway(manipulador, timeout: TimeSpan.FromMilliseconds(150)).CriarPreferenciaAsync(RequestPadrao());

        var excecao = (await act.Should().ThrowAsync<Exception>()).Which;
        excecao.Should().NotBeOfType<PagamentoRecusadoPeloGatewayException>();
    }

    // ---- ObterPagamentoAsync ----

    [Fact]
    public async Task ObterPagamento_Encontrado_DeveMapearOsCamposENormalizarOValor()
    {
        var referencia = Guid.NewGuid();
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(HttpStatusCode.OK,
            "{\"id\":1234567890,\"status\":\"approved\",\"status_detail\":\"accredited\",\"transaction_amount\":192.299,\"currency_id\":\"BRL\",\"external_reference\":\"" + referencia + "\"}"));

        var pagamento = await CriarGateway(manipulador).ObterPagamentoAsync("1234567890");

        pagamento.Should().Be(new PagamentoGatewayDto("1234567890", "approved", "accredited", 192.30m, "BRL", referencia.ToString()));
        manipulador.Requisicoes[0].Metodo.Should().Be(HttpMethod.Get);
        manipulador.Requisicoes[0].Url.AbsolutePath.Should().Be("/v1/payments/1234567890");
        manipulador.Requisicoes[0].Cabecalhos["Authorization"].Should().Be($"Bearer {AccessToken}");
    }

    [Fact]
    public async Task ObterPagamento_GatewayResponde404_DeveDevolverNull()
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(HttpStatusCode.NotFound, RespostaNaoEncontrado));

        var pagamento = await CriarGateway(manipulador).ObterPagamentoAsync("1234567890");

        pagamento.Should().BeNull();
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("-5")]
    [InlineData("12.5")]
    public async Task ObterPagamento_IdentificadorNaoNumerico_DeveDevolverNullSemChamarOGateway(string id)
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(HttpStatusCode.OK, "{}"));

        var pagamento = await CriarGateway(manipulador).ObterPagamentoAsync(id);

        pagamento.Should().BeNull();
        manipulador.Requisicoes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task ObterPagamento_OutrasFalhas_DevePropagarEmVezDeVirarNull(HttpStatusCode status)
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(status, "{\"message\":\"falha\",\"status\":" + (int)status + "}"));

        await CapturarFalhaAsync(() => CriarGateway(manipulador).ObterPagamentoAsync("1234567890"));

        manipulador.Requisicoes.Should().ContainSingle("sem retry no cliente");
    }

    [Fact]
    public async Task ObterPagamento_CamposAusentesNaResposta_DeveDevolverValoresNeutrosParaODominioRecusar()
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(HttpStatusCode.OK, "{\"status\":\"pending\"}"));

        var pagamento = await CriarGateway(manipulador).ObterPagamentoAsync("77");

        pagamento.Should().Be(new PagamentoGatewayDto("77", "pending", null, 0m, string.Empty, null));
    }

    // ---- EstornarAsync ----

    [Fact]
    public async Task Estornar_DeveEnviarOEstornoTotalComAChaveDeIdempotenciaRecebida()
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(HttpStatusCode.Created,
            "{\"id\":987654,\"payment_id\":1234567890,\"amount\":192.3,\"status\":\"approved\"}"));

        var refundId = await CriarGateway(manipulador).EstornarAsync("1234567890", "refund-chave-1");

        refundId.Should().Be("987654");
        var requisicao = manipulador.Requisicoes.Should().ContainSingle().Subject;
        requisicao.Metodo.Should().Be(HttpMethod.Post);
        requisicao.Url.AbsolutePath.Should().Be("/v1/payments/1234567890/refunds");
        requisicao.Cabecalhos["X-Idempotency-Key"].Should().Be("refund-chave-1");
        requisicao.Cabecalhos["Authorization"].Should().Be($"Bearer {AccessToken}");
        requisicao.Corpo.Should().NotContain("amount", "sem valor o estorno é total");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Estornar_RecusaDefinitiva_DeveVirarPagamentoRecusadoSemVazarOCorpo(HttpStatusCode status)
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(status, "{\"message\":\"payer 12345678909\",\"status\":400}"));

        var act = () => CriarGateway(manipulador).EstornarAsync("1234567890", "refund-chave-1");

        var excecao = (await act.Should().ThrowAsync<PagamentoRecusadoPeloGatewayException>()).Which;
        excecao.Message.Should().Be("O gateway recusou o estorno.").And.NotContain("12345678909");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Estornar_FalhaTecnica_DevePropagarSemVirarRecusa(HttpStatusCode status)
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(status, "{\"message\":\"falha\",\"status\":" + (int)status + "}"));

        var excecao = await CapturarFalhaAsync(() => CriarGateway(manipulador).EstornarAsync("1234567890", "refund-chave-1"));

        excecao.Should().NotBeOfType<PagamentoRecusadoPeloGatewayException>();
        manipulador.Requisicoes.Should().ContainSingle("sem retry no cliente");
    }

    [Fact]
    public async Task Estornar_IdentificadorDePagamentoNaoNumerico_DeveRecusarSemChamarOGateway()
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(HttpStatusCode.OK, "{}"));

        var act = () => CriarGateway(manipulador).EstornarAsync("nao-numerico", "refund-chave-1");

        await act.Should().ThrowAsync<PagamentoRecusadoPeloGatewayException>();
        manipulador.Requisicoes.Should().BeEmpty();
    }

    [Fact]
    public async Task Estornar_RespostaSemIdentificadorDoEstorno_DeveFalharEmVezDeInventarUmId()
    {
        var manipulador = new ManipuladorHttpFalso(_ => ManipuladorHttpFalso.Json(HttpStatusCode.Created, "{\"status\":\"approved\"}"));

        var act = () => CriarGateway(manipulador).EstornarAsync("1234567890", "refund-chave-1");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- Classificacao dos erros ----

    [Theory]
    [InlineData(400, true)]
    [InlineData(404, true)]
    [InlineData(410, true)]
    [InlineData(422, true)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(408, false)]
    [InlineData(409, false)]
    [InlineData(423, false)]
    [InlineData(425, false)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    [InlineData(503, false)]
    [InlineData(200, false)]
    public void EhRecusaDefinitiva_DeveClassificarSoOs4xxQueNaoMudamComRetry(int status, bool esperado)
    {
        MercadoPagoGateway.EhRecusaDefinitiva(status).Should().Be(esperado);
    }
}
