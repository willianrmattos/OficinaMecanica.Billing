using FluentAssertions;
using OficinaMecanica.Billing.Application.Commands.CancelarPagamento;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Interfaces;
using OficinaMecanica.Contracts.Events;

namespace OficinaMecanica.Billing.Application.Tests.Commands;

public class CancelarPagamentoCommandHandlerTests
{
    private readonly Mock<IPagamentoRepository> _pagamentoRepositoryMock = new();
    private readonly Mock<IOrcamentoRepository> _orcamentoRepositoryMock = new();
    private readonly Mock<ICancelamentoPrevioRepository> _cancelamentoPrevioRepositoryMock = new();
    private readonly Mock<IIntegrationEventPublisher> _publisherMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly RelogioFixo _relogio = new();
    private readonly GravadorDeChamadas _gravador;
    private readonly CancelarPagamentoCommandHandler _handler;
    private CancelamentoPrevio? _registrado;

    public CancelarPagamentoCommandHandlerTests()
    {
        _gravador = new GravadorDeChamadas(_publisherMock, _unitOfWorkMock);
        _cancelamentoPrevioRepositoryMock
            .Setup(r => r.AdicionarAsync(It.IsAny<CancelamentoPrevio>(), It.IsAny<CancellationToken>()))
            .Callback<CancelamentoPrevio, CancellationToken>((c, _) => _registrado = c);
        _handler = new CancelarPagamentoCommandHandler(
            _pagamentoRepositoryMock.Object, _orcamentoRepositoryMock.Object, _cancelamentoPrevioRepositoryMock.Object,
            _publisherMock.Object, _unitOfWorkMock.Object, _relogio);
    }

    private void DadoPagamento(Pagamento pagamento) =>
        _pagamentoRepositoryMock
            .Setup(r => r.ObterPorIdAsync(pagamento.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagamento);

    private void DadoOrcamento(Orcamento orcamento) =>
        _orcamentoRepositoryMock
            .Setup(r => r.ObterPorIdAsync(orcamento.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orcamento);

    [Fact]
    public async Task Handle_PagamentoPendente_DeveCancelarEPublicarPagamentoCanceladoAntesDeSalvar()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();
        DadoPagamento(pagamento);

        await _handler.Handle(new CancelarPagamentoCommand(pagamento.OrdemDeServicoId, pagamento.Id, "Prazo expirado"), CancellationToken.None);

        pagamento.Status.Should().Be(StatusPagamento.Cancelado);
        pagamento.MotivoCancelamento.Should().Be("Prazo expirado");
        var evento = _gravador.Publicadas<PagamentoCancelado>().Should().ContainSingle().Subject;
        evento.PagamentoId.Should().Be(pagamento.Id);
        evento.OrdemDeServicoId.Should().Be(pagamento.OrdemDeServicoId);
        evento.OcorridoEm.Should().Be(_relogio.GetUtcNow());
        _gravador.Publicadas<PagamentoAprovado>().Should().BeEmpty();
        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Fact]
    public async Task Handle_SemPagamentoId_DeveLocalizarPelaOrdemDeServico()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();
        _pagamentoRepositoryMock
            .Setup(r => r.ObterPorOrdemDeServicoIdAsync(pagamento.OrdemDeServicoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagamento);

        await _handler.Handle(new CancelarPagamentoCommand(pagamento.OrdemDeServicoId, null, "Prazo expirado"), CancellationToken.None);

        pagamento.Status.Should().Be(StatusPagamento.Cancelado);
        _pagamentoRepositoryMock.Verify(r => r.ObterPorIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Publicadas<PagamentoCancelado>().Should().ContainSingle().Which.PagamentoId.Should().Be(pagamento.Id);
    }

    [Fact]
    public async Task Handle_PagamentoPendenteComOrcamentoAprovado_DeveCancelarTambemOOrcamento()
    {
        var orcamento = new OrcamentoBuilder().Aprovado();
        DadoOrcamento(orcamento);
        var pagamento = new PagamentoBuilder().DoOrcamento(orcamento).ComPreferencia();
        DadoPagamento(pagamento);

        await _handler.Handle(new CancelarPagamentoCommand(pagamento.OrdemDeServicoId, pagamento.Id, "Prazo expirado"), CancellationToken.None);

        orcamento.Status.Should().Be(StatusOrcamento.Cancelado);
        orcamento.MotivoCancelamento.Should().Be("Prazo expirado");
        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Fact]
    public async Task Handle_OrcamentoJaPago_NaoDeveCancelarOOrcamentoNemFalhar()
    {
        var orcamentoPago = new OrcamentoBuilder().Pago();
        DadoOrcamento(orcamentoPago);
        var pagamento = new PagamentoBuilder().DoOrcamento(orcamentoPago).ComPreferencia();
        DadoPagamento(pagamento);

        var act = () => _handler.Handle(new CancelarPagamentoCommand(pagamento.OrdemDeServicoId, pagamento.Id, "Prazo expirado"), CancellationToken.None);

        await act.Should().NotThrowAsync();
        orcamentoPago.Status.Should().Be(StatusOrcamento.Pago);
        _gravador.Publicadas<PagamentoCancelado>().Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_PagamentoJaAprovado_DevePublicarPagamentoAprovadoSemCancelarPagamentoNemOrcamento()
    {
        var orcamento = new OrcamentoBuilder().Aprovado();
        DadoOrcamento(orcamento);
        var pagamento = new PagamentoBuilder().DoOrcamento(orcamento).ComGatewayPaymentId("999").Aprovado();
        DadoPagamento(pagamento);

        await _handler.Handle(new CancelarPagamentoCommand(pagamento.OrdemDeServicoId, pagamento.Id, "Prazo expirado"), CancellationToken.None);

        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
        orcamento.Status.Should().Be(StatusOrcamento.Aprovado);
        var evento = _gravador.Publicadas<PagamentoAprovado>().Should().ContainSingle().Subject;
        evento.PagamentoId.Should().Be(pagamento.Id);
        evento.GatewayPaymentId.Should().Be("999");
        evento.Valor.Should().Be(pagamento.Valor);
        _gravador.Publicadas<PagamentoCancelado>().Should().BeEmpty();
        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Fact]
    public async Task Handle_PagamentoJaCancelado_DeveRepublicarPagamentoCanceladoComOMesmoMessageId()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();
        DadoPagamento(pagamento);
        var command = new CancelarPagamentoCommand(pagamento.OrdemDeServicoId, pagamento.Id, "Prazo expirado");

        await _handler.Handle(command, CancellationToken.None);
        await _handler.Handle(command with { Motivo = "Reentrega" }, CancellationToken.None);

        var eventos = _gravador.Publicadas<PagamentoCancelado>();
        eventos.Should().HaveCount(2);
        eventos[1].MessageId.Should().Be(eventos[0].MessageId);
        pagamento.MotivoCancelamento.Should().Be("Prazo expirado");
        _gravador.Ordem.Should().Equal("Publish", "Save", "Publish", "Save");
    }

    [Fact]
    public async Task Handle_PagamentoExpirado_DevePublicarPagamentoCancelado()
    {
        var pagamento = new PagamentoBuilder().Expirado();
        DadoPagamento(pagamento);

        await _handler.Handle(new CancelarPagamentoCommand(pagamento.OrdemDeServicoId, pagamento.Id, "Cancelamento da saga"), CancellationToken.None);

        pagamento.Status.Should().Be(StatusPagamento.Expirado);
        _gravador.Publicadas<PagamentoCancelado>().Should().ContainSingle().Which.PagamentoId.Should().Be(pagamento.Id);
    }

    [Fact]
    public async Task Handle_PagamentoComAprovacaoTardiaJaEstornando_DevePublicarPagamentoCanceladoENaoAprovado()
    {
        var pagamento = new PagamentoBuilder().AprovadoAposCancelamento();
        pagamento.IniciarEstorno();
        DadoPagamento(pagamento);

        await _handler.Handle(new CancelarPagamentoCommand(pagamento.OrdemDeServicoId, pagamento.Id, "Reentrega"), CancellationToken.None);

        _gravador.Publicadas<PagamentoCancelado>().Should().ContainSingle();
        _gravador.Publicadas<PagamentoAprovado>().Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_SemPagamentoExistente_DeveRegistrarCancelamentoPrevioEPublicarPagamentoCanceladoSemId()
    {
        var ordemId = Guid.NewGuid();

        await _handler.Handle(new CancelarPagamentoCommand(ordemId, null, "Cancelamento antes do pagamento"), CancellationToken.None);

        _registrado.Should().NotBeNull();
        _registrado!.OrdemDeServicoId.Should().Be(ordemId);
        _registrado.Tipo.Should().Be(TipoCancelamentoPrevio.Pagamento);
        _registrado.Motivo.Should().Be("Cancelamento antes do pagamento");
        _registrado.RegistradoEm.Should().Be(_relogio.GetUtcNow());
        var evento = _gravador.Publicadas<PagamentoCancelado>().Should().ContainSingle().Subject;
        evento.OrdemDeServicoId.Should().Be(ordemId);
        evento.PagamentoId.Should().BeNull();
        evento.OcorridoEm.Should().Be(_relogio.GetUtcNow());
        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Fact]
    public async Task Handle_PagamentoIdInformadoMasInexistente_DeveRegistrarCancelamentoPrevioEResponderSemId()
    {
        var ordemId = Guid.NewGuid();

        await _handler.Handle(new CancelarPagamentoCommand(ordemId, Guid.NewGuid(), "Prazo expirado"), CancellationToken.None);

        _registrado.Should().NotBeNull();
        _gravador.Publicadas<PagamentoCancelado>().Should().ContainSingle().Which.PagamentoId.Should().BeNull();
    }

    [Fact]
    public async Task Handle_CancelamentoPrevioJaRegistrado_DeveResponderDeNovoComOMesmoMessageIdSemDuplicarOTombstone()
    {
        var ordemId = Guid.NewGuid();
        var command = new CancelarPagamentoCommand(ordemId, null, "Cancelamento antes do pagamento");
        await _handler.Handle(command, CancellationToken.None);
        _cancelamentoPrevioRepositoryMock
            .Setup(r => r.ObterAsync(ordemId, TipoCancelamentoPrevio.Pagamento, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_registrado);

        await _handler.Handle(command, CancellationToken.None);

        _cancelamentoPrevioRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<CancelamentoPrevio>(), It.IsAny<CancellationToken>()), Times.Once());
        var eventos = _gravador.Publicadas<PagamentoCancelado>();
        eventos.Should().HaveCount(2);
        eventos[1].MessageId.Should().Be(eventos[0].MessageId);
        _gravador.Ordem.Should().Equal("Publish", "Save", "Publish", "Save");
    }

    [Fact]
    public async Task Handle_PagamentoPendenteComPrazoVencido_DeveExpirarEPublicarPagamentoCancelado()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();
        DadoPagamento(pagamento);
        _relogio.Avancar(TimeSpan.FromDays(3));

        await _handler.Handle(new CancelarPagamentoCommand(pagamento.OrdemDeServicoId, pagamento.Id, "Prazo de pagamento expirado"), CancellationToken.None);

        pagamento.Status.Should().Be(StatusPagamento.Expirado);
        _gravador.Publicadas<PagamentoCancelado>().Should().ContainSingle().Which.PagamentoId.Should().Be(pagamento.Id);
        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Fact]
    public async Task Handle_PagamentoExpiradoPorPrazoComOrcamentoAprovado_DeveCancelarOOrcamento()
    {
        var orcamento = new OrcamentoBuilder().Aprovado();
        DadoOrcamento(orcamento);
        var pagamento = new PagamentoBuilder().DoOrcamento(orcamento).ComPreferencia();
        DadoPagamento(pagamento);
        _relogio.Avancar(TimeSpan.FromDays(3));

        await _handler.Handle(new CancelarPagamentoCommand(pagamento.OrdemDeServicoId, pagamento.Id, "Prazo de pagamento expirado"), CancellationToken.None);

        orcamento.Status.Should().Be(StatusOrcamento.Cancelado);
    }

    [Fact]
    public async Task Handle_PagamentoAprovadoComPrazoVencido_NaoDeveExpirarEDevePublicarPagamentoAprovado()
    {
        var pagamento = new PagamentoBuilder().Aprovado();
        DadoPagamento(pagamento);
        _relogio.Avancar(TimeSpan.FromDays(3));

        await _handler.Handle(new CancelarPagamentoCommand(pagamento.OrdemDeServicoId, pagamento.Id, "Prazo de pagamento expirado"), CancellationToken.None);

        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
        _gravador.Publicadas<PagamentoAprovado>().Should().ContainSingle();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_MotivoVazio_DeveCancelarComOMotivoPadrao(string motivo)
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();
        DadoPagamento(pagamento);

        await _handler.Handle(new CancelarPagamentoCommand(pagamento.OrdemDeServicoId, pagamento.Id, motivo), CancellationToken.None);

        pagamento.MotivoCancelamento.Should().Be("Motivo não informado.");
        _gravador.Publicadas<PagamentoCancelado>().Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_MotivoMaiorQueOLimite_DeveTruncarEm500Caracteres()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();
        DadoPagamento(pagamento);

        await _handler.Handle(new CancelarPagamentoCommand(pagamento.OrdemDeServicoId, pagamento.Id, new string('a', 900)), CancellationToken.None);

        pagamento.MotivoCancelamento.Should().HaveLength(500);
    }

    [Fact]
    public async Task Handle_CancelamentoPrevioSemMotivo_DeveRegistrarOMotivoPadrao()
    {
        await _handler.Handle(new CancelarPagamentoCommand(Guid.NewGuid(), null, ""), CancellationToken.None);

        _registrado!.Motivo.Should().Be("Motivo não informado.");
    }

    [Fact]
    public async Task Handle_PagamentoIdVazio_DeveTratarComoAusenteELocalizarPelaOrdemDeServico()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();
        _pagamentoRepositoryMock
            .Setup(r => r.ObterPorOrdemDeServicoIdAsync(pagamento.OrdemDeServicoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagamento);

        await _handler.Handle(new CancelarPagamentoCommand(pagamento.OrdemDeServicoId, Guid.Empty, "Prazo expirado"), CancellationToken.None);

        pagamento.Status.Should().Be(StatusPagamento.Cancelado);
        _pagamentoRepositoryMock.Verify(r => r.ObterPorIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Publicadas<PagamentoCancelado>().Should().ContainSingle().Which.PagamentoId.Should().Be(pagamento.Id);
    }

    [Fact]
    public async Task Handle_NoInstanteExatoDeExpiraEm_DeveExpirarOPagamento()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();
        DadoPagamento(pagamento);
        _relogio.Avancar(pagamento.ExpiraEm - _relogio.GetUtcNow());

        await _handler.Handle(new CancelarPagamentoCommand(pagamento.OrdemDeServicoId, pagamento.Id, "Prazo de pagamento expirado"), CancellationToken.None);

        _relogio.GetUtcNow().Should().Be(pagamento.ExpiraEm);
        pagamento.Status.Should().Be(StatusPagamento.Expirado);
    }

    [Fact]
    public async Task Handle_UmSegundoAntesDeExpiraEm_DeveCancelarEManterOMotivo()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();
        DadoPagamento(pagamento);
        _relogio.Avancar(pagamento.ExpiraEm - _relogio.GetUtcNow() - TimeSpan.FromSeconds(1));

        await _handler.Handle(new CancelarPagamentoCommand(pagamento.OrdemDeServicoId, pagamento.Id, "Cliente desistiu"), CancellationToken.None);

        pagamento.Status.Should().Be(StatusPagamento.Cancelado);
        pagamento.MotivoCancelamento.Should().Be("Cliente desistiu");
    }
}
