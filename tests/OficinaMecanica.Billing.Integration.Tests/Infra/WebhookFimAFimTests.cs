using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Infrastructure.Security;
using OficinaMecanica.Contracts.Events;

namespace OficinaMecanica.Billing.Integration.Tests.Infra;

// A API real (Program.cs) sobre SQL Server e RabbitMQ reais, com o gateway Fake: o OS Service de teste pede o
// orçamento e o pagamento por mensagem, o "cliente" paga pelo webhook assinado e o resultado volta por mensagem.
[Trait("Category", "Infra")]
[Collection(ColecaoInfra.Nome)]
public class WebhookFimAFimTests
{
    private readonly InfraFixture _infra;

    public WebhookFimAFimTests(InfraFixture infra)
    {
        _infra = infra;
    }

    private static async Task<(Guid Ordem, Guid OrcamentoId, PagamentoSolicitado Pagamento, string PreferenceId)> PedirPagamentoAsync(AmbienteInfra ambiente)
    {
        var ordem = Guid.NewGuid();
        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.GerarOrcamento(ordem), Guid.NewGuid());
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<OrcamentoGerado>(ordem).Count == 1, "o orçamento foi gerado");
        var orcamentoId = ambiente.Coletor.De<OrcamentoGerado>(ordem).Single().OrcamentoId;

        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.SolicitarPagamento(ordem, orcamentoId), Guid.NewGuid());
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoSolicitado>(ordem).Count == 1, "o pagamento foi solicitado");
        var solicitado = ambiente.Coletor.De<PagamentoSolicitado>(ordem).Single();
        return (ordem, orcamentoId, solicitado, solicitado.LinkPagamento.Split('/').Last());
    }

    private static HttpRequestMessage WebhookAssinado(string dataId, string requestId) =>
        new(HttpMethod.Post, $"/webhooks/mercadopago?data.id={dataId}&type=payment")
        {
            Headers =
            {
                { "x-signature", AssinaturaWebhookMercadoPago.GerarCabecalho(AmbienteInfra.SegredoDoFake, dataId, requestId, DateTimeOffset.UtcNow) },
                { "x-request-id", requestId },
            },
            Content = JsonContent.Create(new { type = "payment", action = "payment.updated", data = new { id = dataId } })
        };

    [Fact]
    public async Task PagamentoAprovadoPeloCliente_DeveAprovarNoBancoEAvisarASagaPeloOutbox()
    {
        await using var ambiente = await AmbienteInfra.CriarComApiAsync(_infra);
        using var cliente = ambiente.CriarClienteHttp();
        var (ordem, orcamentoId, solicitado, preferenceId) = await PedirPagamentoAsync(ambiente);

        var resposta = await cliente.PostAsync($"/dev/pagamentos/{preferenceId}/aprovar", null);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoAprovado>(ordem).Count == 1, "o PagamentoAprovado chegou à saga");
        var aprovado = ambiente.Coletor.De<PagamentoAprovado>(ordem).Single();
        aprovado.PagamentoId.Should().Be(solicitado.PagamentoId);
        aprovado.Valor.Should().Be(MensagensDeTeste.ValorDoOrcamento);
        var pagamento = await ambiente.ComBancoAsync(db => db.Pagamentos.Include(p => p.Tentativas).SingleAsync(p => p.Id == solicitado.PagamentoId));
        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
        pagamento.GatewayPaymentIdAprovado.Should().Be(aprovado.GatewayPaymentId);
        pagamento.Tentativas.Should().ContainSingle();
        (await ambiente.ComBancoAsync(db => db.Orcamentos.SingleAsync(o => o.Id == orcamentoId))).Status.Should().Be(StatusOrcamento.Pago);
        (await ambiente.MensagensComErroAsync()).Should().Be(0);
    }

    [Fact]
    public async Task WebhookReentregue_DeveResponder200SemPublicarDeNovo()
    {
        await using var ambiente = await AmbienteInfra.CriarComApiAsync(_infra);
        using var cliente = ambiente.CriarClienteHttp();
        var (ordem, _, _, preferenceId) = await PedirPagamentoAsync(ambiente);
        var pagamentoDoGateway = ambiente.Gateway.AprovarPreferencia(preferenceId);
        var requestId = Guid.NewGuid().ToString();

        var primeira = await cliente.SendAsync(WebhookAssinado(pagamentoDoGateway.Id, requestId));
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoAprovado>(ordem).Count == 1, "o PagamentoAprovado chegou");
        var reentrega = await cliente.SendAsync(WebhookAssinado(pagamentoDoGateway.Id, requestId));
        var outroRequestId = await cliente.SendAsync(WebhookAssinado(pagamentoDoGateway.Id, Guid.NewGuid().ToString()));

        primeira.StatusCode.Should().Be(HttpStatusCode.OK);
        reentrega.StatusCode.Should().Be(HttpStatusCode.OK);
        outroRequestId.StatusCode.Should().Be(HttpStatusCode.OK);
        await Task.Delay(TimeSpan.FromSeconds(2));
        ambiente.Coletor.De<PagamentoAprovado>(ordem).Should().ContainSingle();
        (await ambiente.ComBancoAsync(db => db.WebhooksRecebidos.CountAsync(w => w.RequestId == requestId))).Should().Be(1);
    }

    [Fact]
    public async Task WebhookComAssinaturaInvalida_DeveResponder401SemTocarNoBanco()
    {
        await using var ambiente = await AmbienteInfra.CriarComApiAsync(_infra);
        using var cliente = ambiente.CriarClienteHttp();
        var (_, _, solicitado, preferenceId) = await PedirPagamentoAsync(ambiente);
        var pagamentoDoGateway = ambiente.Gateway.AprovarPreferencia(preferenceId);
        var requisicao = WebhookAssinado(pagamentoDoGateway.Id, Guid.NewGuid().ToString());
        requisicao.Headers.Remove("x-signature");
        requisicao.Headers.Add("x-signature", $"ts={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},v1={new string('a', 64)}");

        var resposta = await cliente.SendAsync(requisicao);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ambiente.ComBancoAsync(db => db.Pagamentos.SingleAsync(p => p.Id == solicitado.PagamentoId))).Status.Should().Be(StatusPagamento.Pendente);
        (await ambiente.ComBancoAsync(db => db.WebhooksRecebidos.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task AprovacaoTardia_DeveEstornarAutomaticamentePeloOutboxEAvisarASaga()
    {
        await using var ambiente = await AmbienteInfra.CriarComApiAsync(_infra);
        using var cliente = ambiente.CriarClienteHttp();
        var (ordem, _, solicitado, preferenceId) = await PedirPagamentoAsync(ambiente);
        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.CancelarPagamento(ordem, solicitado.PagamentoId), Guid.NewGuid());
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoCancelado>(ordem).Count == 1, "o pagamento foi cancelado");

        var resposta = await cliente.PostAsync($"/dev/pagamentos/{preferenceId}/aprovar", null);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoEstornado>(ordem).Count == 1, "o estorno automático foi concluído");
        ambiente.Coletor.De<PagamentoAprovado>(ordem).Should().BeEmpty("o pagamento tardio não aprova nada");
        var pagamento = await ambiente.ComBancoAsync(db => db.Pagamentos.Include(p => p.Estorno).SingleAsync(p => p.Id == solicitado.PagamentoId));
        pagamento.Status.Should().Be(StatusPagamento.Estornado);
        pagamento.AprovacaoTardia.Should().BeTrue();
        pagamento.Estorno!.GatewayRefundId.Should().StartWith("fake-refund-");
        (await ambiente.MensagensComErroAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MesmoWebhookEmParalelo_NaoDeveGerar500EDeveProcessarUmaVezSo()
    {
        await using var ambiente = await AmbienteInfra.CriarComApiAsync(_infra);
        using var cliente = ambiente.CriarClienteHttp();
        var (ordem, _, solicitado, preferenceId) = await PedirPagamentoAsync(ambiente);
        var pagamentoDoGateway = ambiente.Gateway.AprovarPreferencia(preferenceId);
        var requestId = Guid.NewGuid().ToString();

        var respostas = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cliente.SendAsync(WebhookAssinado(pagamentoDoGateway.Id, requestId))));

        respostas.Select(r => r.StatusCode).Should().OnlyContain(s => s == HttpStatusCode.OK || s == HttpStatusCode.Conflict);
        respostas.Should().Contain(r => r.StatusCode == HttpStatusCode.OK);
        // O Mercado Pago reentrega o que recebeu 409: depois da vencedora confirmar, todas devem responder 200.
        foreach (var _ in respostas.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            (await cliente.SendAsync(WebhookAssinado(pagamentoDoGateway.Id, requestId))).StatusCode.Should().Be(HttpStatusCode.OK);

        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoAprovado>(ordem).Count >= 1, "o PagamentoAprovado chegou");
        await Task.Delay(TimeSpan.FromSeconds(2));
        ambiente.Coletor.De<PagamentoAprovado>(ordem).Should().ContainSingle();
        (await ambiente.ComBancoAsync(db => db.WebhooksRecebidos.CountAsync(w => w.RequestId == requestId))).Should().Be(1);
        (await ambiente.ComBancoAsync(db => db.Pagamentos.SingleAsync(p => p.Id == solicitado.PagamentoId))).Status.Should().Be(StatusPagamento.Aprovado);
    }

    [Fact]
    public async Task NotificacoesDiferentesDoMesmoPagamentoEmParalelo_DeveAprovarUmaVezSoSemGerar500()
    {
        await using var ambiente = await AmbienteInfra.CriarComApiAsync(_infra);
        using var cliente = ambiente.CriarClienteHttp();
        var (ordem, _, solicitado, preferenceId) = await PedirPagamentoAsync(ambiente);
        var pagamentoDoGateway = ambiente.Gateway.AprovarPreferencia(preferenceId);

        var respostas = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => cliente.SendAsync(WebhookAssinado(pagamentoDoGateway.Id, Guid.NewGuid().ToString()))));

        respostas.Select(r => r.StatusCode).Should().OnlyContain(s => s == HttpStatusCode.OK || s == HttpStatusCode.Conflict);
        // Reentrega do que perdeu a corrida: agora o pagamento já existe aprovado e a resposta é 200.
        foreach (var _ in respostas.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            (await cliente.SendAsync(WebhookAssinado(pagamentoDoGateway.Id, Guid.NewGuid().ToString()))).StatusCode.Should().Be(HttpStatusCode.OK);

        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoAprovado>(ordem).Count >= 1, "o PagamentoAprovado chegou");
        await Task.Delay(TimeSpan.FromSeconds(2));
        ambiente.Coletor.De<PagamentoAprovado>(ordem).Should().ContainSingle();
        var pagamento = await ambiente.ComBancoAsync(db => db.Pagamentos.Include(p => p.Tentativas).SingleAsync(p => p.Id == solicitado.PagamentoId));
        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
        pagamento.Tentativas.Should().ContainSingle("o mesmo payment.id vira uma única tentativa");
    }

    [Fact]
    public async Task AplicacaoSobeComMigrationAutomaticaEmDevelopmentEResponde200NoHealth()
    {
        await using var ambiente = await AmbienteInfra.CriarComApiAsync(_infra);
        using var cliente = ambiente.CriarClienteHttp();

        var resposta = await cliente.GetAsync("/health");

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ambiente.ComBancoAsync(db => db.Database.GetAppliedMigrationsAsync())).Should().ContainSingle(m => m.EndsWith("_Inicial"));
    }
}
