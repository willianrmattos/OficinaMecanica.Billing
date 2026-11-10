using FluentAssertions;
using OficinaMecanica.Billing.Application.Commands.CancelarOrcamento;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;
using OficinaMecanica.Billing.Domain.Interfaces;

namespace OficinaMecanica.Billing.Application.Tests.Commands;

public class CancelarOrcamentoCommandHandlerTests
{
    private readonly Mock<IOrcamentoRepository> _orcamentoRepositoryMock = new();
    private readonly Mock<ICancelamentoPrevioRepository> _cancelamentoPrevioRepositoryMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly RelogioFixo _relogio = new();
    private readonly CancelarOrcamentoCommandHandler _handler;
    private CancelamentoPrevio? _registrado;

    public CancelarOrcamentoCommandHandlerTests()
    {
        _unitOfWorkMock.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _cancelamentoPrevioRepositoryMock
            .Setup(r => r.AdicionarAsync(It.IsAny<CancelamentoPrevio>(), It.IsAny<CancellationToken>()))
            .Callback<CancelamentoPrevio, CancellationToken>((c, _) => _registrado = c);
        _handler = new CancelarOrcamentoCommandHandler(
            _orcamentoRepositoryMock.Object, _cancelamentoPrevioRepositoryMock.Object, _unitOfWorkMock.Object, _relogio);
    }

    [Fact]
    public async Task Handle_ComOrcamentoId_DeveLocalizarPeloIdCancelarESalvar()
    {
        var orcamento = new OrcamentoBuilder().Gerado();
        _orcamentoRepositoryMock
            .Setup(r => r.ObterPorIdAsync(orcamento.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orcamento);

        await _handler.Handle(new CancelarOrcamentoCommand(orcamento.OrdemDeServicoId, orcamento.Id, "Cliente recusou"), CancellationToken.None);

        orcamento.Status.Should().Be(StatusOrcamento.Cancelado);
        orcamento.MotivoCancelamento.Should().Be("Cliente recusou");
        _orcamentoRepositoryMock.Verify(r => r.ObterPorOrdemDeServicoIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task Handle_SemOrcamentoId_DeveLocalizarPelaOrdemDeServico()
    {
        var orcamento = new OrcamentoBuilder().Gerado();
        _orcamentoRepositoryMock
            .Setup(r => r.ObterPorOrdemDeServicoIdAsync(orcamento.OrdemDeServicoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orcamento);

        await _handler.Handle(new CancelarOrcamentoCommand(orcamento.OrdemDeServicoId, null, "Falta de estoque"), CancellationToken.None);

        orcamento.Status.Should().Be(StatusOrcamento.Cancelado);
        _orcamentoRepositoryMock.Verify(r => r.ObterPorIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task Handle_OrcamentoAprovado_DeveCancelar()
    {
        var orcamento = new OrcamentoBuilder().Aprovado();
        _orcamentoRepositoryMock
            .Setup(r => r.ObterPorOrdemDeServicoIdAsync(orcamento.OrdemDeServicoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orcamento);

        await _handler.Handle(new CancelarOrcamentoCommand(orcamento.OrdemDeServicoId, null, "Pagamento não solicitado"), CancellationToken.None);

        orcamento.Status.Should().Be(StatusOrcamento.Cancelado);
    }

    [Fact]
    public async Task Handle_OrcamentoJaCancelado_DeveSerIdempotenteSemSobrescreverOMotivo()
    {
        var orcamento = new OrcamentoBuilder().Cancelado("Motivo original");
        _orcamentoRepositoryMock
            .Setup(r => r.ObterPorOrdemDeServicoIdAsync(orcamento.OrdemDeServicoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orcamento);

        var act = () => _handler.Handle(new CancelarOrcamentoCommand(orcamento.OrdemDeServicoId, null, "Reentrega"), CancellationToken.None);

        await act.Should().NotThrowAsync();
        orcamento.Status.Should().Be(StatusOrcamento.Cancelado);
        orcamento.MotivoCancelamento.Should().Be("Motivo original");
    }

    [Fact]
    public async Task Handle_OrcamentoPago_DeveLancarExcecaoSemSalvar()
    {
        var orcamento = new OrcamentoBuilder().Pago();
        _orcamentoRepositoryMock
            .Setup(r => r.ObterPorOrdemDeServicoIdAsync(orcamento.OrdemDeServicoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orcamento);

        var act = () => _handler.Handle(new CancelarOrcamentoCommand(orcamento.OrdemDeServicoId, null, "Tentativa tardia"), CancellationToken.None);

        await act.Should().ThrowAsync<DomainException>().WithMessage("*já pago*");
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Handle_OrcamentoAindaNaoExiste_DeveRegistrarCancelamentoPrevioPelaOrdemDeServico()
    {
        var ordemId = Guid.NewGuid();

        await _handler.Handle(new CancelarOrcamentoCommand(ordemId, null, "Cancelamento antes do orçamento"), CancellationToken.None);

        _registrado.Should().NotBeNull();
        _registrado!.OrdemDeServicoId.Should().Be(ordemId);
        _registrado.Tipo.Should().Be(TipoCancelamentoPrevio.Orcamento);
        _registrado.Motivo.Should().Be("Cancelamento antes do orçamento");
        _registrado.RegistradoEm.Should().Be(_relogio.GetUtcNow());
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task Handle_OrcamentoIdInformadoMasInexistente_DeveRegistrarCancelamentoPrevio()
    {
        var ordemId = Guid.NewGuid();

        await _handler.Handle(new CancelarOrcamentoCommand(ordemId, Guid.NewGuid(), "Cliente recusou"), CancellationToken.None);

        _registrado.Should().NotBeNull();
        _registrado!.OrdemDeServicoId.Should().Be(ordemId);
    }

    [Fact]
    public async Task Handle_CancelamentoPrevioJaRegistrado_DeveSerIdempotenteSemDuplicar()
    {
        var ordemId = Guid.NewGuid();
        _cancelamentoPrevioRepositoryMock
            .Setup(r => r.ObterAsync(ordemId, TipoCancelamentoPrevio.Orcamento, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CancelamentoPrevioBuilder().DaOrdemDeServico(ordemId).Build());

        await _handler.Handle(new CancelarOrcamentoCommand(ordemId, null, "Reentrega"), CancellationToken.None);

        _cancelamentoPrevioRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<CancelamentoPrevio>(), It.IsAny<CancellationToken>()), Times.Never());
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_MotivoVazio_DeveCancelarComOMotivoPadrao(string motivo)
    {
        var orcamento = new OrcamentoBuilder().Gerado();
        _orcamentoRepositoryMock
            .Setup(r => r.ObterPorOrdemDeServicoIdAsync(orcamento.OrdemDeServicoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orcamento);

        await _handler.Handle(new CancelarOrcamentoCommand(orcamento.OrdemDeServicoId, null, motivo), CancellationToken.None);

        orcamento.Status.Should().Be(StatusOrcamento.Cancelado);
        orcamento.MotivoCancelamento.Should().Be("Motivo não informado.");
    }

    [Fact]
    public async Task Handle_MotivoMaiorQueOLimite_DeveTruncarEm500Caracteres()
    {
        var orcamento = new OrcamentoBuilder().Gerado();
        _orcamentoRepositoryMock
            .Setup(r => r.ObterPorOrdemDeServicoIdAsync(orcamento.OrdemDeServicoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orcamento);

        await _handler.Handle(new CancelarOrcamentoCommand(orcamento.OrdemDeServicoId, null, new string('a', 900)), CancellationToken.None);

        orcamento.MotivoCancelamento.Should().HaveLength(500);
    }

    [Fact]
    public async Task Handle_CancelamentoPrevioSemMotivo_DeveRegistrarOMotivoPadrao()
    {
        var ordemId = Guid.NewGuid();

        await _handler.Handle(new CancelarOrcamentoCommand(ordemId, null, ""), CancellationToken.None);

        _registrado!.Motivo.Should().Be("Motivo não informado.");
    }

    [Fact]
    public async Task Handle_OrcamentoIdVazio_DeveTratarComoAusenteELocalizarPelaOrdemDeServico()
    {
        var orcamento = new OrcamentoBuilder().Gerado();
        _orcamentoRepositoryMock
            .Setup(r => r.ObterPorOrdemDeServicoIdAsync(orcamento.OrdemDeServicoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orcamento);

        await _handler.Handle(new CancelarOrcamentoCommand(orcamento.OrdemDeServicoId, Guid.Empty, "Cliente recusou"), CancellationToken.None);

        orcamento.Status.Should().Be(StatusOrcamento.Cancelado);
        _orcamentoRepositoryMock.Verify(r => r.ObterPorIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
    }
}
