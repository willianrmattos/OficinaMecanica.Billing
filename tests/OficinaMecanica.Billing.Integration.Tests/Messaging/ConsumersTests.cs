using FluentAssertions;
using MassTransit;
using MediatR;
using OficinaMecanica.Billing.Application.Commands.CancelarOrcamento;
using OficinaMecanica.Billing.Application.Commands.CancelarPagamento;
using OficinaMecanica.Billing.Application.Commands.EstornarPagamento;
using OficinaMecanica.Billing.Application.Commands.GerarOrcamento;
using OficinaMecanica.Billing.Application.Commands.SolicitarPagamento;
using OficinaMecanica.Billing.Infrastructure.Messaging;
using OficinaMecanica.Billing.Infrastructure.Messaging.Consumers;
using OficinaMecanica.Contracts.Commands;
using OficinaMecanica.Contracts.Common;

namespace OficinaMecanica.Billing.Integration.Tests.Messaging;

public class ConsumersTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IMediator> _mediatorMock = new();
    private readonly Mock<IBloqueioPorOrdemDeServico> _bloqueioMock = new();
    private readonly List<string> _ordem = new();
    private readonly CancellationTokenSource _cancelamento = new();

    public ConsumersTests()
    {
        _bloqueioMock
            .Setup(b => b.AdquirirAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback(() => _ordem.Add("bloqueio"))
            .Returns(Task.CompletedTask);
        _mediatorMock
            .Setup(m => m.Send(It.IsAny<GerarOrcamentoCommand>(), It.IsAny<CancellationToken>()))
            .Callback(() => _ordem.Add("handler")).Returns(Task.CompletedTask);
        _mediatorMock
            .Setup(m => m.Send(It.IsAny<SolicitarPagamentoCommand>(), It.IsAny<CancellationToken>()))
            .Callback(() => _ordem.Add("handler")).Returns(Task.CompletedTask);
        _mediatorMock
            .Setup(m => m.Send(It.IsAny<CancelarOrcamentoCommand>(), It.IsAny<CancellationToken>()))
            .Callback(() => _ordem.Add("handler")).Returns(Task.CompletedTask);
        _mediatorMock
            .Setup(m => m.Send(It.IsAny<CancelarPagamentoCommand>(), It.IsAny<CancellationToken>()))
            .Callback(() => _ordem.Add("handler")).Returns(Task.CompletedTask);
        _mediatorMock
            .Setup(m => m.Send(It.IsAny<EstornarPagamentoCommand>(), It.IsAny<CancellationToken>()))
            .Callback(() => _ordem.Add("handler")).Returns(Task.CompletedTask);
    }

    private ConsumeContext<T> Contexto<T>(T mensagem) where T : class
    {
        var contexto = new Mock<ConsumeContext<T>>();
        contexto.SetupGet(c => c.Message).Returns(mensagem);
        contexto.SetupGet(c => c.CancellationToken).Returns(_cancelamento.Token);
        return contexto.Object;
    }

    [Fact]
    public async Task GerarOrcamentoConsumer_DeveEnviarOCommandMapeadoComOTokenDoConsumo()
    {
        var mensagem = new GerarOrcamento(
            Guid.NewGuid(), Guid.NewGuid(), Agora, "OS-1", 100m, Agora.AddDays(3), "Laudo",
            new[] { new ItemDeServicoMensagem(Guid.NewGuid(), "Troca de óleo", 100m, 1) },
            Array.Empty<ItemDePecaMensagem>());

        await new GerarOrcamentoConsumer(_mediatorMock.Object, _bloqueioMock.Object).Consume(Contexto(mensagem));

        _mediatorMock.Verify(
            m => m.Send(
                It.Is<GerarOrcamentoCommand>(c =>
                    c.OrdemDeServicoId == mensagem.OrdemDeServicoId && c.Numero == "OS-1" && c.ValorTotal == 100m
                    && c.Servicos.Count == 1 && c.Pecas.Count == 0),
                _cancelamento.Token),
            Times.Once);
    }

    [Fact]
    public async Task SolicitarPagamentoConsumer_DeveEnviarOCommandMapeado()
    {
        var mensagem = new SolicitarPagamento(Guid.NewGuid(), Guid.NewGuid(), Agora, Guid.NewGuid(), Agora.AddDays(2));

        await new SolicitarPagamentoConsumer(_mediatorMock.Object, _bloqueioMock.Object).Consume(Contexto(mensagem));

        _mediatorMock.Verify(
            m => m.Send(
                It.Is<SolicitarPagamentoCommand>(c =>
                    c.OrdemDeServicoId == mensagem.OrdemDeServicoId && c.OrcamentoId == mensagem.OrcamentoId && c.ExpiraEm == mensagem.ExpiraEm),
                _cancelamento.Token),
            Times.Once);
    }

    [Fact]
    public async Task CancelarOrcamentoConsumer_DeveEnviarOCommandMapeado()
    {
        var mensagem = new CancelarOrcamento(Guid.NewGuid(), Guid.NewGuid(), Agora, null, "Cliente recusou");

        await new CancelarOrcamentoConsumer(_mediatorMock.Object, _bloqueioMock.Object).Consume(Contexto(mensagem));

        _mediatorMock.Verify(
            m => m.Send(
                It.Is<CancelarOrcamentoCommand>(c =>
                    c.OrdemDeServicoId == mensagem.OrdemDeServicoId && c.OrcamentoId == null && c.Motivo == "Cliente recusou"),
                _cancelamento.Token),
            Times.Once);
    }

    [Fact]
    public async Task CancelarPagamentoConsumer_DeveEnviarOCommandMapeado()
    {
        var mensagem = new CancelarPagamento(Guid.NewGuid(), Guid.NewGuid(), Agora, Guid.NewGuid(), "Prazo");

        await new CancelarPagamentoConsumer(_mediatorMock.Object, _bloqueioMock.Object).Consume(Contexto(mensagem));

        _mediatorMock.Verify(
            m => m.Send(
                It.Is<CancelarPagamentoCommand>(c =>
                    c.OrdemDeServicoId == mensagem.OrdemDeServicoId && c.PagamentoId == mensagem.PagamentoId && c.Motivo == "Prazo"),
                _cancelamento.Token),
            Times.Once);
    }

    [Fact]
    public async Task EstornarPagamentoConsumer_DeveEnviarOCommandComAOrdemDeServico()
    {
        var mensagem = new EstornarPagamento(Guid.NewGuid(), Guid.NewGuid(), Agora, Guid.NewGuid(), "Execução inviável");

        await new EstornarPagamentoConsumer(_mediatorMock.Object, _bloqueioMock.Object).Consume(Contexto(mensagem));

        _mediatorMock.Verify(
            m => m.Send(
                It.Is<EstornarPagamentoCommand>(c =>
                    c.OrdemDeServicoId == mensagem.OrdemDeServicoId && c.PagamentoId == mensagem.PagamentoId && c.Motivo == "Execução inviável"),
                _cancelamento.Token),
            Times.Once);
    }

    [Fact]
    public async Task Consumer_ExcecaoDoHandler_DeveSubirParaOMassTransitDecidirORetry()
    {
        var mensagem = new EstornarPagamento(Guid.NewGuid(), Guid.NewGuid(), Agora, Guid.NewGuid(), "Motivo");
        _mediatorMock
            .Setup(m => m.Send(It.IsAny<EstornarPagamentoCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("falha técnica"));

        var act = () => new EstornarPagamentoConsumer(_mediatorMock.Object, _bloqueioMock.Object).Consume(Contexto(mensagem));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("falha técnica");
    }

    [Fact]
    public async Task Consumers_DevemBloquearAOrdemDeServicoAntesDeChamarOHandler()
    {
        var ordem = Guid.NewGuid();

        await new GerarOrcamentoConsumer(_mediatorMock.Object, _bloqueioMock.Object).Consume(Contexto(
            new GerarOrcamento(Guid.NewGuid(), ordem, Agora, "OS-1", 1m, Agora.AddDays(1), null, Array.Empty<ItemDeServicoMensagem>(), Array.Empty<ItemDePecaMensagem>())));
        await new SolicitarPagamentoConsumer(_mediatorMock.Object, _bloqueioMock.Object).Consume(Contexto(
            new SolicitarPagamento(Guid.NewGuid(), ordem, Agora, Guid.NewGuid(), Agora.AddDays(1))));
        await new CancelarOrcamentoConsumer(_mediatorMock.Object, _bloqueioMock.Object).Consume(Contexto(
            new CancelarOrcamento(Guid.NewGuid(), ordem, Agora, null, "m")));
        await new CancelarPagamentoConsumer(_mediatorMock.Object, _bloqueioMock.Object).Consume(Contexto(
            new CancelarPagamento(Guid.NewGuid(), ordem, Agora, null, "m")));
        await new EstornarPagamentoConsumer(_mediatorMock.Object, _bloqueioMock.Object).Consume(Contexto(
            new EstornarPagamento(Guid.NewGuid(), ordem, Agora, Guid.NewGuid(), "m")));

        _ordem.Should().Equal(Enumerable.Repeat(new[] { "bloqueio", "handler" }, 5).SelectMany(p => p));
        _bloqueioMock.Verify(b => b.AdquirirAsync(ordem, _cancelamento.Token), Times.Exactly(5));
    }

    [Fact]
    public async Task Consumer_BloqueioQueFalha_NaoDeveChamarOHandlerEDevePropagarParaORetry()
    {
        _bloqueioMock
            .Setup(b => b.AdquirirAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("bloqueio"));
        var mensagem = new CancelarOrcamento(Guid.NewGuid(), Guid.NewGuid(), Agora, null, "m");

        var act = () => new CancelarOrcamentoConsumer(_mediatorMock.Object, _bloqueioMock.Object).Consume(Contexto(mensagem));

        await act.Should().ThrowAsync<TimeoutException>();
        _mediatorMock.Verify(m => m.Send(It.IsAny<CancelarOrcamentoCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
