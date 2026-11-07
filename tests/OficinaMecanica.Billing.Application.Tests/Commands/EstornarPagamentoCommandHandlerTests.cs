using FluentAssertions;
using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.Application.Commands.EstornarPagamento;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Interfaces;
using OficinaMecanica.Contracts.Events;

namespace OficinaMecanica.Billing.Application.Tests.Commands;

public class EstornarPagamentoCommandHandlerTests
{
    private const string CorpoDoGateway = "{\"payer\":{\"email\":\"cliente@exemplo.com\",\"identification\":{\"number\":\"12345678909\"}}}";

    private readonly Mock<IPagamentoRepository> _pagamentoRepositoryMock = new();
    private readonly Mock<IPagamentoGateway> _gatewayMock = new();
    private readonly Mock<IIntegrationEventPublisher> _publisherMock = new();
    private readonly Mock<IMetricasDoBilling> _metricasMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<ILogger<EstornarPagamentoCommandHandler>> _loggerMock = new();
    private readonly RelogioFixo _relogio = new();
    private readonly GravadorDeChamadas _gravador;
    private readonly EstornarPagamentoCommandHandler _handler;

    public EstornarPagamentoCommandHandlerTests()
    {
        _gravador = new GravadorDeChamadas(_publisherMock, _unitOfWorkMock);
        _gatewayMock
            .Setup(g => g.EstornarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => _gravador.Registrar("Gateway"))
            .ReturnsAsync("refund-987");
        _handler = new EstornarPagamentoCommandHandler(
            _pagamentoRepositoryMock.Object, _gatewayMock.Object, _publisherMock.Object, _metricasMock.Object,
            _unitOfWorkMock.Object, _relogio, _loggerMock.Object);
    }

    private Pagamento DadoPagamento(Pagamento pagamento)
    {
        _pagamentoRepositoryMock
            .Setup(r => r.ObterPorIdAsync(pagamento.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagamento);
        return pagamento;
    }

    private void DadoGatewayFalhando(Exception excecao) =>
        _gatewayMock
            .Setup(g => g.EstornarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => _gravador.Registrar("Gateway"))
            .ThrowsAsync(excecao);

    private static EstornarPagamentoCommand ComandoPara(Pagamento pagamento, string motivo = "Execução inviável") =>
        new(pagamento.OrdemDeServicoId, pagamento.Id, motivo);

    [Fact]
    public async Task Handle_PagamentoAprovado_DeveEstornarNoGatewayEPublicarPagamentoEstornado()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComGatewayPaymentId("999").Aprovado());

        await _handler.Handle(ComandoPara(pagamento), CancellationToken.None);

        pagamento.Status.Should().Be(StatusPagamento.Estornado);
        pagamento.Estorno!.GatewayRefundId.Should().Be("refund-987");
        var evento = _gravador.Publicadas<PagamentoEstornado>().Should().ContainSingle().Subject;
        evento.PagamentoId.Should().Be(pagamento.Id);
        evento.OrdemDeServicoId.Should().Be(pagamento.OrdemDeServicoId);
        evento.GatewayRefundId.Should().Be("refund-987");
        evento.OcorridoEm.Should().Be(_relogio.GetUtcNow());
        _gravador.Publicadas<EstornoFalhou>().Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_EstornoConcluido_DeveRegistrarAMetricaDePagamentoEstornado()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().Aprovado());

        await _handler.Handle(ComandoPara(pagamento), CancellationToken.None);

        _metricasMock.Verify(m => m.RegistrarPagamentoEstornado(), Times.Once);
    }

    [Fact]
    public async Task Handle_GatewayFalhaNoEstorno_NaoDeveRegistrarAMetricaDePagamentoEstornado()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().Aprovado());
        DadoGatewayFalhando(new HttpRequestException("timeout"));

        await _handler.Handle(ComandoPara(pagamento), CancellationToken.None);

        _metricasMock.Verify(m => m.RegistrarPagamentoEstornado(), Times.Never);
    }

    [Fact]
    public async Task Handle_PagamentoAprovado_DeveUsarAChaveDeIdempotenciaDerivadaDoPagamento()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComGatewayPaymentId("999").Aprovado());

        await _handler.Handle(ComandoPara(pagamento), CancellationToken.None);

        _gatewayMock.Verify(g => g.EstornarAsync("999", $"refund-{pagamento.Id}", It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task Handle_PagamentoAprovado_DevePersistirAIntencaoAntesDoGatewayEPublicarAntesDoSaveFinal()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().Aprovado());

        await _handler.Handle(ComandoPara(pagamento), CancellationToken.None);

        _gravador.Ordem.Should().Equal("Save", "Gateway", "Publish", "Save");
    }

    [Fact]
    public async Task Handle_GatewayIndisponivel_DeveRegistrarTextoFixoSemVazarAMensagemDaExcecao()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().Aprovado());
        DadoGatewayFalhando(new HttpRequestException(CorpoDoGateway));

        var act = () => _handler.Handle(ComandoPara(pagamento), CancellationToken.None);

        await act.Should().NotThrowAsync();
        pagamento.Status.Should().Be(StatusPagamento.EstornoFalhou);
        pagamento.Estorno!.Tentativas.Should().Be(1);
        pagamento.Estorno.Erro.Should().Be("Falha de comunicação com o gateway.");
        var evento = _gravador.Publicadas<EstornoFalhou>().Should().ContainSingle().Subject;
        evento.PagamentoId.Should().Be(pagamento.Id);
        evento.OrdemDeServicoId.Should().Be(pagamento.OrdemDeServicoId);
        evento.Erro.Should().Be("Falha de comunicação com o gateway.");
        evento.Erro.Should().NotContain("cliente@exemplo.com");
        pagamento.Estorno.Erro.Should().NotContain("12345678909");
        _gravador.Publicadas<PagamentoEstornado>().Should().BeEmpty();
        _gravador.Ordem.Should().Equal("Save", "Gateway", "Publish", "Save");
    }

    [Fact]
    public async Task Handle_GatewayRecusaOEstorno_DeveRegistrarTextoFixoDeRecusaSemVazarAMensagemDaExcecao()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().Aprovado());
        DadoGatewayFalhando(new PagamentoRecusadoPeloGatewayException(CorpoDoGateway));

        await _handler.Handle(ComandoPara(pagamento), CancellationToken.None);

        pagamento.Estorno!.Erro.Should().Be("O gateway recusou o estorno.");
        _gravador.Publicadas<EstornoFalhou>().Should().ContainSingle().Which.Erro.Should().Be("O gateway recusou o estorno.");
    }

    [Fact]
    public async Task Handle_GatewayFalha_DeveEnviarAExcecaoCompletaApenasParaOLogComOMotivo()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().Aprovado());
        var excecao = new HttpRequestException(CorpoDoGateway);
        DadoGatewayFalhando(excecao);

        await _handler.Handle(ComandoPara(pagamento, "Execução inviável"), CancellationToken.None);

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((estado, _) => estado.ToString()!.Contains("Execução inviável") && estado.ToString()!.Contains(pagamento.Id.ToString())),
                excecao,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once());
    }

    [Fact]
    public async Task Handle_CancelamentoDaOperacaoDuranteOGateway_DevePropagarSemPublicarNada()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().Aprovado());
        DadoGatewayFalhando(new OperationCanceledException());

        var act = () => _handler.Handle(ComandoPara(pagamento), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _gravador.Mensagens.Should().BeEmpty();
        pagamento.Status.Should().Be(StatusPagamento.Estornando);
    }

    [Fact]
    public async Task Handle_ReentregaComPagamentoJaEstornado_DeveRepublicarOMesmoPagamentoEstornadoSemChamarOGateway()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().Estornado("refund-original"));
        var command = ComandoPara(pagamento);

        await _handler.Handle(command, CancellationToken.None);
        await _handler.Handle(command, CancellationToken.None);

        var eventos = _gravador.Publicadas<PagamentoEstornado>();
        eventos.Should().HaveCount(2);
        eventos.Should().OnlyContain(e => e.GatewayRefundId == "refund-original");
        eventos[1].MessageId.Should().Be(eventos[0].MessageId);
        _gatewayMock.Verify(g => g.EstornarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Ordem.Should().Equal("Publish", "Save", "Publish", "Save");
    }

    [Fact]
    public async Task Handle_EstornoInterrompidoEmEstornando_DeveRetomarComAMesmaChaveDeIdempotencia()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComGatewayPaymentId("999").Estornando());

        await _handler.Handle(ComandoPara(pagamento), CancellationToken.None);

        pagamento.Status.Should().Be(StatusPagamento.Estornado);
        _gatewayMock.Verify(g => g.EstornarAsync("999", $"refund-{pagamento.Id}", It.IsAny<CancellationToken>()), Times.Once());
        _gravador.Publicadas<PagamentoEstornado>().Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_NovaTentativaAposFalha_DevePermitirRetentarEContarAsTentativas()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComEstornoFalho("Falha de comunicação com o gateway."));

        await _handler.Handle(ComandoPara(pagamento, "Retentativa manual"), CancellationToken.None);

        pagamento.Status.Should().Be(StatusPagamento.Estornado);
        pagamento.Estorno!.Tentativas.Should().Be(1);
        pagamento.Estorno.Erro.Should().BeNull();
        _gravador.Publicadas<PagamentoEstornado>().Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_FalhasEmTentativasSeguidas_DevePublicarEstornoFalhouComMessageIdsDistintos()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().Aprovado());
        DadoGatewayFalhando(new HttpRequestException("Gateway fora do ar"));
        var command = ComandoPara(pagamento);

        await _handler.Handle(command, CancellationToken.None);
        await _handler.Handle(command, CancellationToken.None);

        var eventos = _gravador.Publicadas<EstornoFalhou>();
        eventos.Should().HaveCount(2);
        eventos[1].MessageId.Should().NotBe(eventos[0].MessageId);
        pagamento.Estorno!.Tentativas.Should().Be(2);
    }

    [Fact]
    public async Task Handle_AprovacaoTardia_DeveEstornarOPagamentoAprovadoDepoisDeCancelado()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComGatewayPaymentId("777").AprovadoAposCancelamento());

        await _handler.Handle(ComandoPara(pagamento, "Pagamento aprovado depois de cancelado"), CancellationToken.None);

        pagamento.Status.Should().Be(StatusPagamento.Estornado);
        _gatewayMock.Verify(g => g.EstornarAsync("777", $"refund-{pagamento.Id}", It.IsAny<CancellationToken>()), Times.Once());
        _gravador.Publicadas<PagamentoEstornado>().Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_PagamentoNaoEncontrado_DevePublicarEstornoFalhouComOIdDoComandoSemLancar()
    {
        var command = new EstornarPagamentoCommand(Guid.NewGuid(), Guid.NewGuid(), "Execução inviável");

        var act = () => _handler.Handle(command, CancellationToken.None);

        await act.Should().NotThrowAsync();
        var evento = _gravador.Publicadas<EstornoFalhou>().Should().ContainSingle().Subject;
        evento.OrdemDeServicoId.Should().Be(command.OrdemDeServicoId);
        evento.PagamentoId.Should().Be(command.PagamentoId);
        evento.Erro.Should().Be("Pagamento não encontrado.");
        evento.OcorridoEm.Should().Be(_relogio.GetUtcNow());
        _gatewayMock.Verify(g => g.EstornarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Ordem.Should().Equal("Publish", "Save");
        _loggerMock.VerificarLog(LogLevel.Warning, "Execução inviável");
    }

    [Fact]
    public async Task Handle_PagamentoNuncaAprovado_DevePublicarEstornoFalhouSemChamarOGatewayNemAlterarOPagamento()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComPreferencia());

        var act = () => _handler.Handle(ComandoPara(pagamento), CancellationToken.None);

        await act.Should().NotThrowAsync();
        var evento = _gravador.Publicadas<EstornoFalhou>().Should().ContainSingle().Subject;
        evento.PagamentoId.Should().Be(pagamento.Id);
        evento.Erro.Should().Be("O pagamento não está em um estado que permita o estorno.");
        pagamento.Status.Should().Be(StatusPagamento.Pendente);
        pagamento.Estorno.Should().BeNull();
        _gatewayMock.Verify(g => g.EstornarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Ordem.Should().Equal("Publish", "Save");
        _loggerMock.VerificarLog(LogLevel.Warning, "Execução inviável");
    }

    [Fact]
    public async Task Handle_ReentregaDeEstornoImpossivel_DevePublicarEstornoFalhouComOMesmoMessageId()
    {
        var command = new EstornarPagamentoCommand(Guid.NewGuid(), Guid.NewGuid(), "Execução inviável");

        await _handler.Handle(command, CancellationToken.None);
        await _handler.Handle(command, CancellationToken.None);

        var eventos = _gravador.Publicadas<EstornoFalhou>();
        eventos.Should().HaveCount(2);
        eventos[1].MessageId.Should().Be(eventos[0].MessageId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_MotivoVazio_DeveEstornarMesmoAssimUsandoOMotivoPadraoNoLog(string motivo)
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().Aprovado());
        DadoGatewayFalhando(new HttpRequestException("503"));

        await _handler.Handle(ComandoPara(pagamento, motivo), CancellationToken.None);

        _loggerMock.VerificarLog(LogLevel.Error, "Motivo não informado.");
        _gravador.Publicadas<EstornoFalhou>().Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_PagamentoIdVazio_DevePublicarEstornoFalhouComMessageIdDistintoPorOrdemDeServico()
    {
        var primeira = new EstornarPagamentoCommand(Guid.NewGuid(), Guid.Empty, "Execução inviável");
        var segunda = new EstornarPagamentoCommand(Guid.NewGuid(), Guid.Empty, "Execução inviável");

        await _handler.Handle(primeira, CancellationToken.None);
        await _handler.Handle(segunda, CancellationToken.None);

        var eventos = _gravador.Publicadas<EstornoFalhou>();
        eventos.Should().HaveCount(2);
        eventos.Should().OnlyContain(e => e.PagamentoId == Guid.Empty && e.Erro == "Pagamento não encontrado.");
        eventos[1].MessageId.Should().NotBe(eventos[0].MessageId);
    }
}
