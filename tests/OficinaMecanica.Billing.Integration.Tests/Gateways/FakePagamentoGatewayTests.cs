using FluentAssertions;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Infrastructure.Gateways;

namespace OficinaMecanica.Billing.Integration.Tests.Gateways;

public class FakePagamentoGatewayTests
{
    private readonly FakePagamentoGateway _gateway = new();

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

        primeiro.Id.Should().Be(FakePagamentoGateway.PrimeiroIdDePagamento.ToString());
        segundo.Id.Should().Be((FakePagamentoGateway.PrimeiroIdDePagamento + 1).ToString());
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
}
