using FluentAssertions;
using OficinaMecanica.Billing.Application.Queries.ObterPagamentoPorOrdemDeServico;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Interfaces;

namespace OficinaMecanica.Billing.Application.Tests.Queries;

public class ObterPagamentoPorOrdemDeServicoQueryHandlerTests
{
    private readonly Mock<IPagamentoRepository> _pagamentoRepositoryMock = new();
    private readonly ObterPagamentoPorOrdemDeServicoQueryHandler _handler;

    public ObterPagamentoPorOrdemDeServicoQueryHandlerTests()
    {
        _handler = new ObterPagamentoPorOrdemDeServicoQueryHandler(_pagamentoRepositoryMock.Object);
    }

    private void DadoPagamento(Pagamento pagamento) =>
        _pagamentoRepositoryMock
            .Setup(r => r.ObterPorOrdemDeServicoIdAsync(pagamento.OrdemDeServicoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagamento);

    [Fact]
    public async Task Handle_PagamentoPendente_DeveRetornarODtoSemEstorno()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();
        DadoPagamento(pagamento);

        var dto = await _handler.Handle(new ObterPagamentoPorOrdemDeServicoQuery(pagamento.OrdemDeServicoId), CancellationToken.None);

        dto.Should().NotBeNull();
        dto!.Id.Should().Be(pagamento.Id);
        dto.OrcamentoId.Should().Be(pagamento.OrcamentoId);
        dto.OrdemDeServicoId.Should().Be(pagamento.OrdemDeServicoId);
        dto.Status.Should().Be("Pendente");
        dto.Valor.Should().Be(pagamento.Valor);
        dto.LinkPagamento.Should().Be(pagamento.LinkPagamento);
        dto.ExpiraEm.Should().Be(pagamento.ExpiraEm);
        dto.CriadoEm.Should().Be(pagamento.CriadoEm);
        dto.GatewayPaymentIdAprovado.Should().BeNull();
        dto.AprovadoEm.Should().BeNull();
        dto.AprovacaoTardia.Should().BeFalse();
        dto.Tentativas.Should().BeEmpty();
        dto.Estorno.Should().BeNull();
    }

    [Fact]
    public async Task Handle_PagamentoAprovadoComTentativas_DeveRetornarAsTentativasEOPagamentoDoGateway()
    {
        var pagamento = new PagamentoBuilder().ComGatewayPaymentId("999").Aprovado();
        pagamento.RegistrarTentativa("111", "rejected", "cc_rejected_other_reason", pagamento.Valor, RelogioFixo.InstanteBase);
        DadoPagamento(pagamento);

        var dto = await _handler.Handle(new ObterPagamentoPorOrdemDeServicoQuery(pagamento.OrdemDeServicoId), CancellationToken.None);

        dto!.Status.Should().Be("Aprovado");
        dto.GatewayPaymentIdAprovado.Should().Be("999");
        dto.AprovadoEm.Should().Be(RelogioFixo.InstanteBase);
        dto.Tentativas.Select(t => (t.GatewayPaymentId, t.StatusGateway)).Should().BeEquivalentTo(new[] { ("999", "approved"), ("111", "rejected") });
        dto.Tentativas.Single(t => t.GatewayPaymentId == "111").StatusDetalhe.Should().Be("cc_rejected_other_reason");
    }

    [Fact]
    public async Task Handle_PagamentoComEstornoFalho_DeveRetornarOEstornoComOErro()
    {
        var pagamento = new PagamentoBuilder().ComEstornoFalho("Gateway recusou o estorno");
        DadoPagamento(pagamento);

        var dto = await _handler.Handle(new ObterPagamentoPorOrdemDeServicoQuery(pagamento.OrdemDeServicoId), CancellationToken.None);

        dto!.Status.Should().Be("EstornoFalhou");
        dto.Estorno.Should().NotBeNull();
        dto.Estorno!.Status.Should().Be("Falhou");
        dto.Estorno.Tentativas.Should().Be(1);
        dto.Estorno.Erro.Should().Be("Gateway recusou o estorno");
        dto.Estorno.GatewayRefundId.Should().BeNull();
    }

    [Fact]
    public async Task Handle_PagamentoEstornadoAposAprovacaoTardia_DeveRetornarOReembolsoEAMarcaDeAprovacaoTardia()
    {
        var pagamento = new PagamentoBuilder().AprovadoAposCancelamento();
        pagamento.IniciarEstorno();
        pagamento.ConfirmarEstorno("refund-987");
        DadoPagamento(pagamento);

        var dto = await _handler.Handle(new ObterPagamentoPorOrdemDeServicoQuery(pagamento.OrdemDeServicoId), CancellationToken.None);

        dto!.Status.Should().Be("Estornado");
        dto.AprovacaoTardia.Should().BeTrue();
        dto.MotivoCancelamento.Should().NotBeNullOrEmpty();
        dto.Estorno!.GatewayRefundId.Should().Be("refund-987");
        dto.Estorno.Status.Should().Be("Concluido");
    }

    [Fact]
    public async Task Handle_PagamentoInexistente_DeveRetornarNulo()
    {
        var dto = await _handler.Handle(new ObterPagamentoPorOrdemDeServicoQuery(Guid.NewGuid()), CancellationToken.None);

        dto.Should().BeNull();
    }
}
