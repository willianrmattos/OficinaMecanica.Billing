using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Integration.Tests.Support;

namespace OficinaMecanica.Billing.Integration.Tests.Api;

public class ApiConfiguracaoTests
{
    // A partida que falha dentro do WebApplicationFactory disputa o host com a thread do teste: ora chega a
    // OptionsValidationException, ora um ObjectDisposedException do provider já descartado. Por isso aqui cada caso parte de uma
    // base de Production válida e varia UMA única chave; se a exceção de validação chegar, ela precisa ter exatamente a falha
    // esperada, e se vier a corrida conhecida, só ela é aceita (qualquer outro tipo de erro reprova). O tipo e a mensagem de cada
    // caso são afirmados sem corrida em PartidaDaConfiguracaoTests, com host genérico.
    private static void DeveFalharAPartidaPorValidacao(BillingApiFactory api, string mensagemEsperada)
    {
        var excecao = Record.Exception(() => api.CreateClient());

        excecao.Should().NotBeNull("a validação das opções deve impedir a partida");
        var validacoes = Desembrulhar(excecao!).OfType<OptionsValidationException>().ToList();
        if (validacoes.Count > 0)
            validacoes.Single().Failures.Should().ContainSingle().Which.Should().Contain(mensagemEsperada);
        else
            excecao.Should().BeOfType<ObjectDisposedException>("é a corrida conhecida do WebApplicationFactory, não outra falha");
    }

    private static IEnumerable<Exception> Desembrulhar(Exception excecao)
    {
        yield return excecao;

        IEnumerable<Exception> internas = excecao is AggregateException agregada
            ? agregada.InnerExceptions
            : excecao.InnerException is null ? Array.Empty<Exception>() : new[] { excecao.InnerException };

        foreach (var interna in internas.SelectMany(Desembrulhar))
            yield return interna;
    }

    // Production com tudo válido: MercadoPago configurado, RabbitMQ e outbox do EF. Cada teste de falha parte daqui e muda uma chave.
    private static BillingApiFactory BaseDeProducao() =>
        new BillingApiFactory()
            .EmAmbiente("Production")
            .Com("Pagamento:Provider", "MercadoPago")
            .Com("MercadoPago:AccessToken", "TEST-token")
            .Com("MercadoPago:WebhookSecret", "segredo")
            .Com("MercadoPago:NotificationBaseUrl", "https://apimfiap.azure-api.net/billingserver")
            .Com("Mensageria:Transporte", "RabbitMq")
            .Com("Mensageria:Persistencia", "EntityFramework");

    // "Testing" é o ambiente não-Development que ainda aceita o transporte em memória: serve para simular o que a API faz
    // fora de Development (sem rotas de simulação, Fake recusado) sem precisar do RabbitMQ.
    private static BillingApiFactory ComMercadoPagoForaDeDevelopment() =>
        new BillingApiFactory()
            .EmAmbiente("Testing")
            .Com("Pagamento:Provider", "MercadoPago")
            .Com("MercadoPago:AccessToken", "TEST-token")
            .Com("MercadoPago:WebhookSecret", "segredo")
            .Com("MercadoPago:NotificationBaseUrl", "https://apimfiap.azure-api.net/billingserver");

    [Fact]
    public async Task ComMercadoPagoForaDeDevelopment_RotasDeSimulacaoNaoExistemNemAparecemNoSwagger()
    {
        await using var api = ComMercadoPagoForaDeDevelopment();
        var cliente = api.CreateClient();

        var rota = await cliente.PostAsync("/dev/pagamentos/fake-pref-x/aprovar", null);
        var swagger = await cliente.GetStringAsync("/swagger/v1/swagger.json");

        rota.StatusCode.Should().Be(HttpStatusCode.NotFound);
        swagger.Should().NotContain("/dev/pagamentos");
        swagger.Should().Contain("/webhooks/mercadopago");
    }

    [Fact]
    public async Task ComMercadoPago_WebhookSemAssinaturaValidaContinuaRecusado()
    {
        await using var api = ComMercadoPagoForaDeDevelopment();
        var cliente = api.CreateClient();
        var requisicao = new HttpRequestMessage(HttpMethod.Post, "/webhooks/mercadopago?data.id=123&type=payment");
        requisicao.Headers.Add("x-signature", $"ts={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},v1={new string('0', 64)}");
        requisicao.Headers.Add("x-request-id", "req-1");

        var resposta = await cliente.SendAsync(requisicao);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EmProducao_SemOAccessTokenDoMercadoPago_ApiNaoDeveSubir()
    {
        await using var api = BaseDeProducao().Com("MercadoPago:AccessToken", null);

        DeveFalharAPartidaPorValidacao(api, "MercadoPago:AccessToken é obrigatório");
    }

    [Fact]
    public async Task EmProducao_ComGatewayFake_ApiNaoDeveSubir()
    {
        await using var api = BaseDeProducao().Com("Pagamento:Provider", "Fake");

        DeveFalharAPartidaPorValidacao(api, "Pagamento:Provider=Fake só é permitido em Development");
    }

    [Theory]
    [InlineData("Production", "Mensageria:Transporte", "EmMemoria", "Mensageria:Transporte=EmMemoria só é permitido")]
    [InlineData("Production", "Mensageria:Persistencia", "Nenhuma", "Mensageria:Persistencia=Nenhuma só é permitido")]
    [InlineData("Staging", "Mensageria:Transporte", "EmMemoria", "Mensageria:Transporte=EmMemoria só é permitido")]
    [InlineData("Staging", "Mensageria:Persistencia", "Nenhuma", "Mensageria:Persistencia=Nenhuma só é permitido")]
    public async Task ForaDeDevelopmentOuTesting_UmaChaveDeMensageriaEmMemoria_ApiNaoDeveSubir(
        string ambiente, string chave, string valor, string mensagemEsperada)
    {
        await using var api = BaseDeProducao().EmAmbiente(ambiente).Com(chave, valor);

        DeveFalharAPartidaPorValidacao(api, mensagemEsperada);
    }

    [Fact]
    public async Task EmTesting_TransporteEmMemoriaESemOutbox_ApiSobe()
    {
        await using var api = ComMercadoPagoForaDeDevelopment();

        var resposta = await api.CreateClient().GetAsync("/health");

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Billing:ReconciliadorIntervaloSegundos", "Billing:ReconciliadorIntervaloSegundos deve ser maior que zero")]
    [InlineData("Billing:ReconciliadorTamanhoDoLote", "Billing:ReconciliadorTamanhoDoLote deve estar entre 1 e 1000")]
    [InlineData("Billing:ReconciliadorMaximoDeTentativasDeEstorno", "Billing:ReconciliadorMaximoDeTentativasDeEstorno deve ser maior que zero")]
    public async Task EmProducao_ReconciliadorHabilitadoComUmaChaveInvalida_ApiNaoDeveSubir(string chave, string mensagemEsperada)
    {
        await using var api = BaseDeProducao().Com("Billing:ReconciliadorHabilitado", "true").Com(chave, "0");

        DeveFalharAPartidaPorValidacao(api, mensagemEsperada);
    }

    [Fact]
    public async Task EmDevelopmentComFake_RotaDeSimulacaoExiste()
    {
        await using var api = new BillingApiFactory();
        var cliente = api.CreateClient();

        var resposta = await cliente.PostAsync("/dev/pagamentos/fake-pref-inexistente/aprovar", null);

        resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await resposta.Content.ReadAsStringAsync()).Should().Contain("Recurso não encontrado");
    }
}
