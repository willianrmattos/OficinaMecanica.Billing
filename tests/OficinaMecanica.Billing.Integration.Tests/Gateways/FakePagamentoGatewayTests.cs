using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Infrastructure.Gateways;

namespace OficinaMecanica.Billing.Integration.Tests.Gateways;

public class FakePagamentoGatewayTests
{
    private const long PrimeiroId = 7_000;

    private readonly FakePagamentoGateway _gateway = new(primeiroIdDePagamento: PrimeiroId);

    private static CriarPreferenciaGatewayRequest RequestPara(Guid pagamentoId, decimal valor = 150.75m) =>
        new(pagamentoId, "Ordem de serviço OS-1", valor, DateTimeOffset.UtcNow.AddDays(2));

    [Fact]
    public async Task CriarPreferencia_DeveDerivarOIdEOLinkDoPagamento()
    {
        var pagamentoId = Guid.NewGuid();

        var preferencia = await _gateway.CriarPreferenciaAsync(RequestPara(pagamentoId));

        preferencia.PreferenceId.Should().Be($"fake-pref-{pagamentoId:N}");
        preferencia.LinkPagamento.Should().Be($"https://fake.mercadopago.local/checkout/fake-pref-{pagamentoId:N}");
    }

    [Fact]
    public async Task CriarPreferencia_RepetidaParaOMesmoPagamento_DeveDevolverAMesmaPreferencia()
    {
        var pagamentoId = Guid.NewGuid();

        var primeira = await _gateway.CriarPreferenciaAsync(RequestPara(pagamentoId));
        var segunda = await _gateway.CriarPreferenciaAsync(RequestPara(pagamentoId));

        segunda.Should().Be(primeira);
    }

    [Fact]
    public async Task AprovarPreferencia_DeveCriarUmPagamentoAprovadoComValorEReferenciaDaPreferencia()
    {
        var pagamentoId = Guid.NewGuid();
        var preferencia = await _gateway.CriarPreferenciaAsync(RequestPara(pagamentoId, 150.75m));

        var pagamento = _gateway.AprovarPreferencia(preferencia.PreferenceId);

        pagamento.Status.Should().Be("approved");
        pagamento.StatusDetalhe.Should().Be("accredited");
        pagamento.Valor.Should().Be(150.75m);
        pagamento.Moeda.Should().Be("BRL");
        pagamento.ExternalReference.Should().Be(pagamentoId.ToString());
        (await _gateway.ObterPagamentoAsync(pagamento.Id)).Should().Be(pagamento);
    }

    [Fact]
    public async Task RecusarPreferencia_DeveCriarUmPagamentoRecusado()
    {
        var preferencia = await _gateway.CriarPreferenciaAsync(RequestPara(Guid.NewGuid()));

        var pagamento = _gateway.RecusarPreferencia(preferencia.PreferenceId);

        pagamento.Status.Should().Be("rejected");
        pagamento.StatusDetalhe.Should().Be("cc_rejected_other_reason");
    }

    [Fact]
    public async Task RegistrarPagamento_VariasTentativas_DeveGerarIdsSequenciaisDistintos()
    {
        var preferencia = await _gateway.CriarPreferenciaAsync(RequestPara(Guid.NewGuid()));

        var primeiro = _gateway.RecusarPreferencia(preferencia.PreferenceId);
        var segundo = _gateway.AprovarPreferencia(preferencia.PreferenceId);

        primeiro.Id.Should().Be(PrimeiroId.ToString());
        segundo.Id.Should().Be((PrimeiroId + 1).ToString());
    }

    private static async Task<(FakePagamentoGateway Gateway, string PreferenceId)> NovoGatewayComRelogioAsync(DateTimeOffset agora)
    {
        var gateway = new FakePagamentoGateway(new FakeTimeProvider(agora));
        var preferencia = await gateway.CriarPreferenciaAsync(RequestPara(Guid.NewGuid()));
        return (gateway, preferencia.PreferenceId);
    }

    private static long[] AprovarVezes((FakePagamentoGateway Gateway, string PreferenceId) alvo, int quantidade) =>
        Enumerable.Range(0, quantidade).Select(_ => long.Parse(alvo.Gateway.AprovarPreferencia(alvo.PreferenceId).Id)).ToArray();

    [Fact]
    public async Task IdsDoRelogio_DevemSerNumericosLongosECrescerDeUmEmUmDentroDaInstancia()
    {
        var agora = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        var gateway = await NovoGatewayComRelogioAsync(agora);

        var ids = AprovarVezes(gateway, 50);

        ids[0].Should().BeGreaterThanOrEqualTo(agora.ToUnixTimeMilliseconds() * 1_000, "a semente vem do relógio");
        ids.Select(id => id.ToString().Length).Should().OnlyContain(tamanho => tamanho >= 13 && tamanho <= 18, "o id tem o formato longo e numérico do Mercado Pago");
        ids.Zip(ids.Skip(1), (anterior, atual) => atual - anterior).Should().OnlyContain(passo => passo == 1);
    }

    [Fact]
    public async Task GatewaysReiniciadosComRelogiosDiferentes_NaoDevemGerarOMesmoId()
    {
        var antesDoRestart = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        var primeiro = await NovoGatewayComRelogioAsync(antesDoRestart);
        var segundo = await NovoGatewayComRelogioAsync(antesDoRestart.AddSeconds(5));

        var idsAntes = AprovarVezes(primeiro, 500);
        var idsDepois = AprovarVezes(segundo, 500);

        idsAntes.Intersect(idsDepois).Should().BeEmpty("o banco guarda o id do gateway com índice único");
        idsDepois.Min().Should().BeGreaterThan(idsAntes.Max(), "depois do restart os ids continuam crescendo");
    }

    [Fact]
    public async Task GatewaysCriadosNoMesmoInstanteDoRelogio_NaoDevemRepartirIds()
    {
        var agora = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        var primeiro = await NovoGatewayComRelogioAsync(agora);
        var segundo = await NovoGatewayComRelogioAsync(agora);

        var idsDoPrimeiro = AprovarVezes(primeiro, 500);
        var idsDoSegundo = AprovarVezes(segundo, 500);

        idsDoPrimeiro.Intersect(idsDoSegundo).Should().BeEmpty();
    }

    [Fact]
    public void GatewaysConstruidosEmParalelo_NaoDevemRepartirIds()
    {
        // Todos os gateways de uma rodada nascem no mesmo instante do relógio e ao mesmo tempo: só a reserva atômica da faixa
        // impede que dois deles comecem do mesmo id.
        const int threads = 32;
        const int rodadas = 60;
        const int gatewaysPorThreadEmCadaRodada = 20;
        const int idsPorGateway = 2;
        var relogio = new FakeTimeProvider(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        var ids = new ConcurrentBag<long>();

        for (var rodada = 0; rodada < rodadas; rodada++)
        {
            using var largada = new Barrier(threads);
            var tarefas = Enumerable.Range(0, threads)
                .Select(_ => Task.Factory.StartNew(() =>
                {
                    largada.SignalAndWait();
                    for (var i = 0; i < gatewaysPorThreadEmCadaRodada; i++)
                    {
                        var gateway = new FakePagamentoGateway(relogio);
                        var preferencia = gateway.CriarPreferenciaAsync(RequestPara(Guid.NewGuid())).GetAwaiter().GetResult();
                        foreach (var id in AprovarVezes((gateway, preferencia.PreferenceId), idsPorGateway))
                            ids.Add(id);
                    }
                }, TaskCreationOptions.LongRunning))
                .ToArray();

            Task.WaitAll(tarefas);
        }

        ids.Should().HaveCount(threads * rodadas * gatewaysPorThreadEmCadaRodada * idsPorGateway);
        ids.Should().OnlyHaveUniqueItems("o banco guarda o id do gateway com índice único");
    }

    [Fact]
    public async Task RegistrarPagamento_EmParalelo_DeveGerarIdsUnicos()
    {
        var preferencia = await _gateway.CriarPreferenciaAsync(RequestPara(Guid.NewGuid()));

        var ids = await Task.WhenAll(Enumerable.Range(0, 200)
            .Select(_ => Task.Run(() => _gateway.AprovarPreferencia(preferencia.PreferenceId).Id)));

        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task RegistrarPagamento_ComValorForcado_DeveSimularDivergenciaDeValor()
    {
        var preferencia = await _gateway.CriarPreferenciaAsync(RequestPara(Guid.NewGuid(), 150.75m));

        var pagamento = _gateway.RegistrarPagamento(preferencia.PreferenceId, "approved", "accredited", valor: 1m);

        pagamento.Valor.Should().Be(1m);
    }

    [Fact]
    public void RegistrarPagamento_PreferenciaDesconhecida_DeveLancarKeyNotFound()
    {
        var act = () => _gateway.AprovarPreferencia("fake-pref-inexistente");

        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public async Task ObterPagamento_Desconhecido_DeveDevolverNull()
    {
        (await _gateway.ObterPagamentoAsync("999")).Should().BeNull();
    }

    [Fact]
    public async Task ObterPagamento_ComPagamentosIndisponiveis_DeveDevolverNullMesmoParaUmPagamentoExistente()
    {
        var preferencia = await _gateway.CriarPreferenciaAsync(RequestPara(Guid.NewGuid()));
        var pagamento = _gateway.AprovarPreferencia(preferencia.PreferenceId);
        _gateway.PagamentosIndisponiveis = true;

        (await _gateway.ObterPagamentoAsync(pagamento.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Estornar_PagamentoAprovado_DeveDevolverUmEstornoEMarcarOPagamentoComoReembolsado()
    {
        var preferencia = await _gateway.CriarPreferenciaAsync(RequestPara(Guid.NewGuid()));
        var pagamento = _gateway.AprovarPreferencia(preferencia.PreferenceId);

        var refundId = await _gateway.EstornarAsync(pagamento.Id, "refund-1");

        refundId.Should().Be("fake-refund-1");
        (await _gateway.ObterPagamentoAsync(pagamento.Id))!.Status.Should().Be("refunded");
    }

    [Fact]
    public async Task Estornar_MesmaChaveDeIdempotencia_DeveDevolverOMesmoEstorno()
    {
        var preferencia = await _gateway.CriarPreferenciaAsync(RequestPara(Guid.NewGuid()));
        var pagamento = _gateway.AprovarPreferencia(preferencia.PreferenceId);

        var primeiro = await _gateway.EstornarAsync(pagamento.Id, "refund-1");
        var segundo = await _gateway.EstornarAsync(pagamento.Id, "refund-1");

        segundo.Should().Be(primeiro);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("pending")]
    public async Task Estornar_PagamentoNaoAprovado_DeveRecusar(string status)
    {
        var preferencia = await _gateway.CriarPreferenciaAsync(RequestPara(Guid.NewGuid()));
        var pagamento = _gateway.RegistrarPagamento(preferencia.PreferenceId, status, null);

        var act = () => _gateway.EstornarAsync(pagamento.Id, "refund-1");

        await act.Should().ThrowAsync<PagamentoRecusadoPeloGatewayException>();
    }

    [Fact]
    public async Task Estornar_PagamentoInexistente_DeveRecusar()
    {
        var act = () => _gateway.EstornarAsync("999", "refund-1");

        await act.Should().ThrowAsync<PagamentoRecusadoPeloGatewayException>();
    }

    [Fact]
    public async Task Estornar_ComFalhaConfigurada_DeveLancarAExcecaoConfigurada()
    {
        _gateway.FalhaNoEstorno = new HttpRequestException("gateway fora do ar");

        var act = () => _gateway.EstornarAsync("1", "refund-1");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Reiniciar_DeveLimparPreferenciasPagamentosEFalhasConfiguradas()
    {
        var preferencia = await _gateway.CriarPreferenciaAsync(RequestPara(Guid.NewGuid()));
        var pagamento = _gateway.AprovarPreferencia(preferencia.PreferenceId);
        _gateway.PagamentosIndisponiveis = true;
        _gateway.FalhaNoEstorno = new InvalidOperationException();

        _gateway.Reiniciar();

        _gateway.PagamentosIndisponiveis.Should().BeFalse();
        _gateway.FalhaNoEstorno.Should().BeNull();
        (await _gateway.ObterPagamentoAsync(pagamento.Id)).Should().BeNull();
        var act = () => _gateway.AprovarPreferencia(preferencia.PreferenceId);
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public async Task Reiniciar_NaoDeveRepetirIdsJaEntregues()
    {
        var preferencia = await _gateway.CriarPreferenciaAsync(RequestPara(Guid.NewGuid()));
        var antes = AprovarVezes((_gateway, preferencia.PreferenceId), 3);

        _gateway.Reiniciar();

        var nova = await _gateway.CriarPreferenciaAsync(RequestPara(Guid.NewGuid()));
        var depois = AprovarVezes((_gateway, nova.PreferenceId), 3);
        antes.Intersect(depois).Should().BeEmpty("um id já entregue não volta a ser gerado");
        depois.Min().Should().BeGreaterThan(antes.Max());
    }
}
