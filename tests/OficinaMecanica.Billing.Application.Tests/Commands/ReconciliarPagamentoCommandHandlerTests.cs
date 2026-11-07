using FluentAssertions;
using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.Application.Commands.ReconciliarPagamento;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Interfaces;
using OficinaMecanica.Contracts;
using OficinaMecanica.Contracts.Commands;

namespace OficinaMecanica.Billing.Application.Tests.Commands;

public class ReconciliarPagamentoCommandHandlerTests
{
    private const int MaximoDeTentativas = 3;

    private readonly Mock<IPagamentoRepository> _pagamentoRepositoryMock = new();
    private readonly Mock<IPagamentoGateway> _gatewayMock = new();
    private readonly Mock<IIntegrationEventPublisher> _publisherMock = new();
    private readonly Mock<IMetricasDoBilling> _metricasMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<ILogger<ReconciliarPagamentoCommandHandler>> _loggerMock = new();
    private readonly RelogioFixo _relogio = new();
    private readonly GravadorDeChamadas _gravador;
    private readonly ReconciliarPagamentoCommandHandler _handler;

    public ReconciliarPagamentoCommandHandlerTests()
    {
        _gravador = new GravadorDeChamadas(_publisherMock, _unitOfWorkMock);
        _handler = new ReconciliarPagamentoCommandHandler(
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

    private Task Reconciliar(Pagamento pagamento) =>
        _handler.Handle(new ReconciliarPagamentoCommand(pagamento.Id, MaximoDeTentativas), CancellationToken.None);

    [Fact]
    public async Task Handle_PagamentoInexistente_DeveIgnorarSemEnviarNemSalvar()
    {
        var id = Guid.NewGuid();

        await _handler.Handle(new ReconciliarPagamentoCommand(id, MaximoDeTentativas), CancellationToken.None);

        _gravador.Ordem.Should().BeEmpty();
        _loggerMock.VerificarLog(LogLevel.Warning, id.ToString());
    }

    [Fact]
    public async Task Handle_PagamentoSemNadaPendente_NaoDeveEnviarNada()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().Aprovado());

        await Reconciliar(pagamento);

        _gravador.Mensagens.Should().BeEmpty();
        _gatewayMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Handle_AprovacaoTardiaSemEstornoIniciado_DeveReenviarOComandoDeEstornoParaAFilaDoBilling()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().AprovadoAposCancelamento());

        await Reconciliar(pagamento);

        var (fila, mensagem) = _gravador.Envios.Should().ContainSingle().Subject;
        fila.Should().Be(Filas.Billing);
        var comando = mensagem.Should().BeOfType<EstornarPagamento>().Subject;
        comando.PagamentoId.Should().Be(pagamento.Id);
        comando.OrdemDeServicoId.Should().Be(pagamento.OrdemDeServicoId);
        comando.OcorridoEm.Should().Be(_relogio.GetUtcNow());
    }

    [Fact]
    public async Task Handle_ReenvioDoMesmoEstadoDuasVezes_DeveUsarOMesmoMessageIdParaOInboxDeduplicar()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().AprovadoAposExpiracao());

        await Reconciliar(pagamento);
        _relogio.Avancar(TimeSpan.FromMinutes(5));
        await Reconciliar(pagamento);

        var comandos = _gravador.Enviadas<EstornarPagamento>();
        comandos.Should().HaveCount(2);
        comandos[1].MessageId.Should().Be(comandos[0].MessageId);
    }

    [Fact]
    public async Task Handle_EstornoEmAndamentoParado_DeveReenviarOComando()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().Estornando());

        await Reconciliar(pagamento);

        _gravador.Enviadas<EstornarPagamento>().Should().ContainSingle().Which.PagamentoId.Should().Be(pagamento.Id);
    }

    [Fact]
    public async Task Handle_EstornoFalhou_DeveReenviarComMessageIdNovoAposACadaFalha()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().Estornando());

        await Reconciliar(pagamento);
        pagamento.FalharEstorno("falha");
        await Reconciliar(pagamento);

        var comandos = _gravador.Enviadas<EstornarPagamento>();
        comandos.Should().HaveCount(2);
        comandos[1].MessageId.Should().NotBe(comandos[0].MessageId);
    }

    // Pagamento em EstornoFalhou com exatamente "tentativas" falhas registradas (0 = ainda Estornando).
    private static Pagamento ComTentativasDeEstorno(int tentativas)
    {
        var pagamento = new PagamentoBuilder().Estornando();
        for (var i = 0; i < tentativas; i++)
        {
            pagamento.FalharEstorno("falha");
            if (i < tentativas - 1)
                pagamento.IniciarEstorno();
        }

        return pagamento;
    }

    [Fact]
    public async Task Handle_UmaTentativaAbaixoDoTeto_DeveReenviarENaoAbandonar()
    {
        var pagamento = DadoPagamento(ComTentativasDeEstorno(MaximoDeTentativas - 1));

        await Reconciliar(pagamento);

        _gravador.Enviadas<EstornarPagamento>().Should().ContainSingle();
        pagamento.EstornoAbandonado.Should().BeFalse();
        _metricasMock.Verify(m => m.RegistrarEstornoAbandonado(), Times.Never);
    }

    [Fact]
    public async Task Handle_TentativasIguaisAoTeto_NaoDeveReenviarDeveAbandonarESinalizarPorMetricaELog()
    {
        var pagamento = DadoPagamento(ComTentativasDeEstorno(MaximoDeTentativas));

        await Reconciliar(pagamento);

        _gravador.Envios.Should().BeEmpty();
        pagamento.EstornoAbandonado.Should().BeTrue();
        pagamento.Estorno!.AbandonadoEm.Should().Be(_relogio.GetUtcNow());
        _metricasMock.Verify(m => m.RegistrarEstornoAbandonado(), Times.Once);
        _loggerMock.VerificarLog(LogLevel.Error, "intervenção manual");
        _gravador.Ordem.Should().Equal("Save");
    }

    [Fact]
    public async Task Handle_TentativasAcimaDoTeto_NaoDeveReenviarEDeveAbandonar()
    {
        var pagamento = DadoPagamento(ComTentativasDeEstorno(MaximoDeTentativas + 2));

        await Reconciliar(pagamento);

        _gravador.Envios.Should().BeEmpty();
        pagamento.EstornoAbandonado.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_CicloRepetidoSobreEstornoAbandonado_DeveAvisarUmaVezSoEnaoReenviar()
    {
        var pagamento = DadoPagamento(ComTentativasDeEstorno(MaximoDeTentativas));

        await Reconciliar(pagamento);
        _relogio.Avancar(TimeSpan.FromMinutes(5));
        await Reconciliar(pagamento);
        await Reconciliar(pagamento);

        _metricasMock.Verify(m => m.RegistrarEstornoAbandonado(), Times.Once);
        _loggerMock.VerificarLog(LogLevel.Error, "intervenção manual", Times.Once());
        _gravador.Envios.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_EstornoAbandonadoComCobrancaEmDuplicidade_DeveSeguirEstornandoADuplicidade()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComGatewayPaymentId("1000").AprovadoComAprovacaoDuplicada("2000"));
        pagamento.IniciarEstorno();
        for (var i = 0; i < MaximoDeTentativas; i++)
        {
            pagamento.FalharEstorno("falha");
            if (i < MaximoDeTentativas - 1)
                pagamento.IniciarEstorno();
        }

        pagamento.AbandonarEstorno(_relogio.GetUtcNow());
        _gatewayMock
            .Setup(g => g.EstornarAsync("2000", "refund-duplicidade-2000", It.IsAny<CancellationToken>()))
            .ReturnsAsync("refund-dup-1");

        await Reconciliar(pagamento);

        _gravador.Envios.Should().BeEmpty();
        pagamento.AprovacoesDuplicadasSemEstorno().Should().BeEmpty();
        _metricasMock.Verify(m => m.RegistrarEstornoAbandonado(), Times.Never);
    }

    [Fact]
    public async Task Handle_ReenvioDeEstorno_DeveEnviarAntesDeSalvar()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().AprovadoAposCancelamento());

        await Reconciliar(pagamento);

        _gravador.Ordem.Should().Equal("Send", "Save");
    }

    [Fact]
    public async Task Handle_AprovacaoDuplicada_DeveEstornarADuplicadaNoGatewayComChaveDeIdempotenciaPropria()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComGatewayPaymentId("1000").AprovadoComAprovacaoDuplicada("2000"));
        _gatewayMock
            .Setup(g => g.EstornarAsync("2000", "refund-duplicidade-2000", It.IsAny<CancellationToken>()))
            .ReturnsAsync("refund-dup-1");

        await Reconciliar(pagamento);

        pagamento.Tentativas.Single(t => t.GatewayPaymentId == "2000").GatewayRefundId.Should().Be("refund-dup-1");
        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
        _gravador.Mensagens.Should().BeEmpty();
        _metricasMock.Verify(m => m.RegistrarPagamentoEstornado(), Times.Once);
        _gravador.Ordem.Should().Equal("Save");
    }

    [Fact]
    public async Task Handle_AprovacaoDuplicadaJaEstornada_NaoDeveChamarOGatewayDeNovo()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().AprovadoComAprovacaoDuplicada("2000"));
        pagamento.RegistrarEstornoDeAprovacaoDuplicada("2000", "refund-dup-1");

        await Reconciliar(pagamento);

        _gatewayMock.Verify(g => g.EstornarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_GatewayFalhaNoEstornoDaDuplicada_DeveLogarSeguirESalvarParaTentarNoProximoCiclo()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().AprovadoComAprovacaoDuplicada("2000"));
        _gatewayMock
            .Setup(g => g.EstornarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PagamentoRecusadoPeloGatewayException("recusado"));

        await Reconciliar(pagamento);

        pagamento.AprovacoesDuplicadasSemEstorno().Should().ContainSingle();
        _metricasMock.Verify(m => m.RegistrarPagamentoEstornado(), Times.Never);
        _loggerMock.VerificarLog(LogLevel.Error, "2000");
        _gravador.Ordem.Should().Equal("Save");
    }

    [Fact]
    public async Task Handle_CancelamentoDuranteOEstornoDaDuplicada_DevePropagarSemSalvar()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().AprovadoComAprovacaoDuplicada("2000"));
        _gatewayMock
            .Setup(g => g.EstornarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => Reconciliar(pagamento);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _gravador.Ordem.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_EstornoPendenteEDuplicidade_DeveTratarOsDoisNoMesmoSave()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComGatewayPaymentId("1000").AprovadoComAprovacaoDuplicada("2000"));
        pagamento.IniciarEstorno();
        _gatewayMock
            .Setup(g => g.EstornarAsync("2000", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("refund-dup-1");

        await Reconciliar(pagamento);

        _gravador.Enviadas<EstornarPagamento>().Should().ContainSingle();
        pagamento.AprovacoesDuplicadasSemEstorno().Should().BeEmpty();
        _gravador.Ordem.Should().Equal("Send", "Save");
    }

    [Fact]
    public async Task Handle_Abandono_DeveContarAMetricaSoDepoisDoSaveChanges()
    {
        var pagamento = DadoPagamento(ComTentativasDeEstorno(MaximoDeTentativas));
        _metricasMock.Setup(m => m.RegistrarEstornoAbandonado()).Callback(() => _gravador.Registrar("Metrica"));

        await Reconciliar(pagamento);

        _gravador.Ordem.Should().Equal("Save", "Metrica");
    }

    [Fact]
    public async Task Handle_SaveChangesFalhaNoAbandono_NaoDeveAvisarNemContar()
    {
        var pagamento = DadoPagamento(ComTentativasDeEstorno(MaximoDeTentativas));
        _unitOfWorkMock
            .Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("conflito de concorrência"));

        var act = () => Reconciliar(pagamento);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _metricasMock.Verify(m => m.RegistrarEstornoAbandonado(), Times.Never);
        _loggerMock.VerificarLog(LogLevel.Error, "intervenção manual", Times.Never());
    }

    [Fact]
    public async Task Handle_SaveChangesFalhaNaDuplicidade_NaoDeveContarOEstornoDaDuplicidade()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().AprovadoComAprovacaoDuplicada("2000"));
        _gatewayMock
            .Setup(g => g.EstornarAsync("2000", "refund-duplicidade-2000", It.IsAny<CancellationToken>()))
            .ReturnsAsync("refund-dup-1");
        _unitOfWorkMock
            .Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("conflito de concorrência"));

        var act = () => Reconciliar(pagamento);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _metricasMock.Verify(m => m.RegistrarPagamentoEstornado(), Times.Never);
        _loggerMock.VerificarLog(LogLevel.Information, "foi estornada", Times.Never());
    }

    [Fact]
    public async Task Handle_EstornoDuplicadoConcluido_DeveContarAMetricaSoDepoisDoSaveChanges()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().AprovadoComAprovacaoDuplicada("2000"));
        _gatewayMock
            .Setup(g => g.EstornarAsync("2000", "refund-duplicidade-2000", It.IsAny<CancellationToken>()))
            .ReturnsAsync("refund-dup-1");
        _metricasMock.Setup(m => m.RegistrarPagamentoEstornado()).Callback(() => _gravador.Registrar("Metrica"));

        await Reconciliar(pagamento);

        _gravador.Ordem.Should().Equal("Save", "Metrica");
    }

    [Fact]
    public async Task Handle_EstornoJaAbandonadoEComTetoMaiorDepois_NaoDeveReenviarNemAvisarDeNovo()
    {
        var pagamento = DadoPagamento(ComTentativasDeEstorno(MaximoDeTentativas));
        await Reconciliar(pagamento);
        pagamento.EstornoAbandonado.Should().BeTrue();

        // O teto foi aumentado por configuração depois do abandono: o caso continua com o staff.
        await _handler.Handle(new ReconciliarPagamentoCommand(pagamento.Id, MaximoDeTentativas * 10), CancellationToken.None);

        _gravador.Envios.Should().BeEmpty();
        _metricasMock.Verify(m => m.RegistrarEstornoAbandonado(), Times.Once);
    }
}
