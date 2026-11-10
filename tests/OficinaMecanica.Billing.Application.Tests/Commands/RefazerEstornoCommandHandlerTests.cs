using FluentAssertions;
using MediatR;
using OficinaMecanica.Billing.Application.Commands.EstornarPagamento;
using OficinaMecanica.Billing.Application.Commands.RefazerEstorno;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;
using OficinaMecanica.Billing.Domain.Interfaces;

namespace OficinaMecanica.Billing.Application.Tests.Commands;

public class RefazerEstornoCommandHandlerTests
{
    private readonly Mock<IPagamentoRepository> _pagamentoRepositoryMock = new();
    private readonly Mock<IMediator> _mediatorMock = new();
    private readonly RefazerEstornoCommandHandler _handler;

    public RefazerEstornoCommandHandlerTests()
    {
        _handler = new RefazerEstornoCommandHandler(_pagamentoRepositoryMock.Object, _mediatorMock.Object);
    }

    private Pagamento DadoPagamento(Pagamento pagamento)
    {
        _pagamentoRepositoryMock
            .Setup(r => r.ObterPorIdAsync(pagamento.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagamento);
        return pagamento;
    }

    [Fact]
    public async Task Handle_PagamentoComEstornoFalho_DeveDelegarAoEstornoComOMotivoInformado()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComEstornoFalho());

        var dto = await _handler.Handle(new RefazerEstornoCommand(pagamento.Id, "Autorizado pelo financeiro"), CancellationToken.None);

        _mediatorMock.Verify(
            m => m.Send(
                It.Is<EstornarPagamentoCommand>(c =>
                    c.PagamentoId == pagamento.Id
                    && c.OrdemDeServicoId == pagamento.OrdemDeServicoId
                    && c.Motivo == "Autorizado pelo financeiro"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        dto.Id.Should().Be(pagamento.Id);
        dto.Status.Should().Be(nameof(StatusPagamento.EstornoFalhou));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_MotivoAusente_DeveUsarOMotivoPadrao(string? motivo)
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComEstornoFalho());

        await _handler.Handle(new RefazerEstornoCommand(pagamento.Id, motivo), CancellationToken.None);

        _mediatorMock.Verify(
            m => m.Send(It.Is<EstornarPagamentoCommand>(c => c.Motivo == "Estorno refeito manualmente pelo staff."), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_PagamentoInexistente_DeveLancarKeyNotFoundSemEstornar()
    {
        var act = () => _handler.Handle(new RefazerEstornoCommand(Guid.NewGuid(), null), CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();
        _mediatorMock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(StatusPagamento.Pendente)]
    [InlineData(StatusPagamento.Aprovado)]
    [InlineData(StatusPagamento.Estornando)]
    [InlineData(StatusPagamento.Estornado)]
    public async Task Handle_PagamentoForaDeEstornoFalhou_DeveLancarDomainExceptionSemEstornar(StatusPagamento status)
    {
        var pagamento = DadoPagamento(status switch
        {
            StatusPagamento.Pendente => new PagamentoBuilder().ComPreferencia(),
            StatusPagamento.Aprovado => new PagamentoBuilder().Aprovado(),
            StatusPagamento.Estornando => new PagamentoBuilder().Estornando(),
            _ => new PagamentoBuilder().Estornado()
        });

        var act = () => _handler.Handle(new RefazerEstornoCommand(pagamento.Id, null), CancellationToken.None);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage($"*{status}*");
        _mediatorMock.VerifyNoOtherCalls();
    }
}
