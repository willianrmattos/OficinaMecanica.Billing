using FluentAssertions;
using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.Application.Commands.ProcessarWebhookPagamento;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Interfaces;
using OficinaMecanica.Contracts;
using OficinaMecanica.Contracts.Commands;
using OficinaMecanica.Contracts.Events;

namespace OficinaMecanica.Billing.Application.Tests.Commands;

public class ProcessarWebhookPagamentoCommandHandlerTests
{
    private const string DataId = "1234567890";
    private const string RequestId = "req-abc-1";
    private const string Assinatura = "ts=1700000000,v1=assinatura";

    private readonly Mock<IAssinaturaWebhookValidator> _assinaturaValidatorMock = new();
    private readonly Mock<IWebhookRecebidoRepository> _webhookRepositoryMock = new();
    private readonly Mock<IPagamentoRepository> _pagamentoRepositoryMock = new();
    private readonly Mock<IOrcamentoRepository> _orcamentoRepositoryMock = new();
    private readonly Mock<IPagamentoGateway> _gatewayMock = new();
    private readonly Mock<IIntegrationEventPublisher> _publisherMock = new();
    private readonly Mock<IMetricasDoBilling> _metricasMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<ILogger<ProcessarWebhookPagamentoCommandHandler>> _loggerMock = new();
    private readonly RelogioFixo _relogio = new();
    private readonly GravadorDeChamadas _gravador;
    private readonly ProcessarWebhookPagamentoCommandHandler _handler;
    private WebhookRecebido? _adicionado;

    public ProcessarWebhookPagamentoCommandHandlerTests()
    {
        _gravador = new GravadorDeChamadas(_publisherMock, _unitOfWorkMock);
        _assinaturaValidatorMock
            .Setup(v => v.Validar(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>()))
            .Returns(true);
        _webhookRepositoryMock
            .Setup(r => r.AdicionarAsync(It.IsAny<WebhookRecebido>(), It.IsAny<CancellationToken>()))
            .Callback<WebhookRecebido, CancellationToken>((w, _) => _adicionado = w);

        _handler = new ProcessarWebhookPagamentoCommandHandler(
            _assinaturaValidatorMock.Object, _webhookRepositoryMock.Object, _pagamentoRepositoryMock.Object,
            _orcamentoRepositoryMock.Object, _gatewayMock.Object, _publisherMock.Object, _metricasMock.Object,
            _unitOfWorkMock.Object, _relogio, _loggerMock.Object);
    }

    private static ProcessarWebhookPagamentoCommand Comando(string requestId = RequestId) =>
        new(DataId, requestId, Assinatura);

    private Pagamento DadoPagamento(Pagamento pagamento)
    {
        _pagamentoRepositoryMock
            .Setup(r => r.ObterPorIdAsync(pagamento.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagamento);
        return pagamento;
    }

    private void DadoOrcamento(Orcamento orcamento) =>
        _orcamentoRepositoryMock
            .Setup(r => r.ObterPorIdAsync(orcamento.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orcamento);

    private void DadoGateway(PagamentoGatewayDto? dto) =>
        _gatewayMock
            .Setup(g => g.ObterPagamentoAsync(DataId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dto);

    private static PagamentoGatewayDto GatewayAprovadoPara(Pagamento pagamento) =>
        new PagamentoGatewayDtoBuilder().ComId(DataId).DoPagamento(pagamento).Build();

    [Fact]
    public async Task Handle_AssinaturaInvalida_DeveLancarExcecaoSemConsultarNemPersistirNada()
    {
        _assinaturaValidatorMock
            .Setup(v => v.Validar(Assinatura, RequestId, DataId))
            .Returns(false);

        var act = () => _handler.Handle(Comando(), CancellationToken.None);

        await act.Should().ThrowAsync<AssinaturaWebhookInvalidaException>().WithMessage("*assinatura*inválida*");
        _gatewayMock.Verify(g => g.ObterPagamentoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        _webhookRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<WebhookRecebido>(), It.IsAny<CancellationToken>()), Times.Never());
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Handle_SemAssinatura_DeveLancarExcecaoDeAssinaturaInvalida()
    {
        _assinaturaValidatorMock
            .Setup(v => v.Validar(null, RequestId, DataId))
            .Returns(false);

        var act = () => _handler.Handle(new ProcessarWebhookPagamentoCommand(DataId, RequestId, null), CancellationToken.None);

        await act.Should().ThrowAsync<AssinaturaWebhookInvalidaException>();
    }

    [Fact]
    public async Task Handle_WebhookJaProcessado_DeveIgnorarSemConsultarOGateway()
    {
        _webhookRepositoryMock
            .Setup(r => r.ObterPorRequestIdAsync(RequestId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebhookRecebidoBuilder().ComRequestId(RequestId).Processado());

        await _handler.Handle(Comando(), CancellationToken.None);

        _gatewayMock.Verify(g => g.ObterPagamentoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        _webhookRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<WebhookRecebido>(), It.IsAny<CancellationToken>()), Times.Never());
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Mensagens.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_PagamentoAprovadoNoGateway_DeveConfirmarMarcarOrcamentoComoPagoEPublicarPagamentoAprovado()
    {
        var orcamento = new OrcamentoBuilder().Aprovado();
        DadoOrcamento(orcamento);
        var pagamento = DadoPagamento(new PagamentoBuilder().DoOrcamento(orcamento).ComPreferencia());
        DadoGateway(GatewayAprovadoPara(pagamento));

        await _handler.Handle(Comando(), CancellationToken.None);

        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
        pagamento.GatewayPaymentIdAprovado.Should().Be(DataId);
        pagamento.Tentativas.Should().ContainSingle().Which.Aprovada.Should().BeTrue();
        orcamento.Status.Should().Be(StatusOrcamento.Pago);
        var evento = _gravador.Publicadas<PagamentoAprovado>().Should().ContainSingle().Subject;
        evento.PagamentoId.Should().Be(pagamento.Id);
        evento.OrdemDeServicoId.Should().Be(pagamento.OrdemDeServicoId);
        evento.GatewayPaymentId.Should().Be(DataId);
        evento.Valor.Should().Be(pagamento.Valor);
        evento.OcorridoEm.Should().Be(_relogio.GetUtcNow());
        _adicionado.Should().NotBeNull();
        _adicionado!.RequestId.Should().Be(RequestId);
        _adicionado.DataId.Should().Be(DataId);
        _adicionado.Processado.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_PagamentoAprovadoNoGateway_DeveRegistrarAMetricaDePagamentoAprovado()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComPreferencia());
        DadoGateway(GatewayAprovadoPara(pagamento));

        await _handler.Handle(Comando(), CancellationToken.None);

        _metricasMock.Verify(m => m.RegistrarPagamentoAprovado(), Times.Once);
        _metricasMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Handle_TentativaRecusadaPeloGateway_DeveRegistrarAMetricaDePagamentoRecusado()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComPreferencia());
        DadoGateway(new PagamentoGatewayDtoBuilder().ComId(DataId).DoPagamento(pagamento).ComStatus("rejected", "cc_rejected_other_reason").Build());

        await _handler.Handle(Comando(), CancellationToken.None);

        _metricasMock.Verify(m => m.RegistrarPagamentoRecusado(), Times.Once);
        _metricasMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Handle_TentativaPendente_NaoDeveRegistrarMetricaDePagamento()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComPreferencia());
        DadoGateway(new PagamentoGatewayDtoBuilder().ComId(DataId).DoPagamento(pagamento).ComStatus("pending").Build());

        await _handler.Handle(Comando(), CancellationToken.None);

        _metricasMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Handle_PagamentoAprovadoNoGateway_DeveConsultarOGatewayEPublicarAntesDeSalvar()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComPreferencia());
        DadoGateway(GatewayAprovadoPara(pagamento));

        await _handler.Handle(Comando(), CancellationToken.None);

        _gatewayMock.Verify(g => g.ObterPagamentoAsync(DataId, It.IsAny<CancellationToken>()), Times.Once());
        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Fact]
    public async Task Handle_OrcamentoInexistente_DevePublicarPagamentoAprovadoMesmoAssim()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComPreferencia());
        DadoGateway(GatewayAprovadoPara(pagamento));

        await _handler.Handle(Comando(), CancellationToken.None);

        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
        _gravador.Publicadas<PagamentoAprovado>().Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_OrcamentoJaCancelado_NaoDeveMarcarComoPagoMasDevePublicarPagamentoAprovado()
    {
        var orcamento = new OrcamentoBuilder().Cancelado();
        DadoOrcamento(orcamento);
        var pagamento = DadoPagamento(new PagamentoBuilder().DoOrcamento(orcamento).ComPreferencia());
        DadoGateway(GatewayAprovadoPara(pagamento));

        await _handler.Handle(Comando(), CancellationToken.None);

        orcamento.Status.Should().Be(StatusOrcamento.Cancelado);
        _gravador.Publicadas<PagamentoAprovado>().Should().ContainSingle();
    }

    [Theory]
    [InlineData("pending", "pending_waiting_payment")]
    [InlineData("in_process", "pending_review_manual")]
    [InlineData("rejected", "cc_rejected_insufficient_amount")]
    [InlineData("cancelled", "expired")]
    public async Task Handle_StatusNaoAprovado_DeveSoRegistrarATentativaSemMudarOPagamento(string status, string detalhe)
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComPreferencia());
        DadoGateway(new PagamentoGatewayDtoBuilder().ComId(DataId).DoPagamento(pagamento).ComStatus(status, detalhe).Build());

        await _handler.Handle(Comando(), CancellationToken.None);

        pagamento.Status.Should().Be(StatusPagamento.Pendente);
        var tentativa = pagamento.Tentativas.Should().ContainSingle().Subject;
        tentativa.GatewayPaymentId.Should().Be(DataId);
        tentativa.StatusGateway.Should().Be(status);
        tentativa.StatusDetalhe.Should().Be(detalhe);
        _gravador.Mensagens.Should().BeEmpty();
        _adicionado!.Processado.Should().BeTrue();
        _gravador.Ordem.Should().Equal("Save");
    }

    [Fact]
    public async Task Handle_RejeicaoSeguidaDeAprovacaoEmOutroPagamentoDoGateway_DeveAprovarNaSegundaTentativa()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComPreferencia());
        _gatewayMock
            .Setup(g => g.ObterPagamentoAsync("111", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagamentoGatewayDtoBuilder().ComId("111").DoPagamento(pagamento).ComStatus("rejected", "cc_rejected_other_reason").Build());
        _gatewayMock
            .Setup(g => g.ObterPagamentoAsync("222", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagamentoGatewayDtoBuilder().ComId("222").DoPagamento(pagamento).Build());

        await _handler.Handle(new ProcessarWebhookPagamentoCommand("111", "req-1", Assinatura), CancellationToken.None);
        pagamento.Status.Should().Be(StatusPagamento.Pendente);
        await _handler.Handle(new ProcessarWebhookPagamentoCommand("222", "req-2", Assinatura), CancellationToken.None);

        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
        pagamento.GatewayPaymentIdAprovado.Should().Be("222");
        pagamento.Tentativas.Select(t => t.GatewayPaymentId).Should().BeEquivalentTo(new[] { "111", "222" });
        _gravador.Publicadas<PagamentoAprovado>().Should().ContainSingle().Which.GatewayPaymentId.Should().Be("222");
    }

    [Fact]
    public async Task Handle_NotificacaoAtrasadaDePendingDepoisDeAprovado_NaoDeveRebaixarAAprovacao()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComGatewayPaymentId(DataId).Aprovado());
        DadoGateway(new PagamentoGatewayDtoBuilder().ComId(DataId).DoPagamento(pagamento).ComStatus("pending").Build());

        await _handler.Handle(Comando(), CancellationToken.None);

        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
        pagamento.Tentativas.Should().ContainSingle().Which.Aprovada.Should().BeTrue();
        _gravador.Mensagens.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_AprovadoReentregueComOutroRequestId_NaoDevePublicarDeNovo()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComGatewayPaymentId(DataId).Aprovado());
        DadoGateway(GatewayAprovadoPara(pagamento));

        await _handler.Handle(Comando("req-reentrega"), CancellationToken.None);

        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
        _gravador.Mensagens.Should().BeEmpty();
        _metricasMock.VerifyNoOtherCalls();
        _adicionado!.Processado.Should().BeTrue();
        _gravador.Ordem.Should().Equal("Save");
    }

    [Fact]
    public async Task Handle_GatewayAindaNaoConheceOPagamento_DeveLancarExcecaoTransitoriaParaOGatewayReentregar()
    {
        DadoGateway(null);

        var act = () => _handler.Handle(Comando(), CancellationToken.None);

        await act.Should().ThrowAsync<PagamentoDoGatewayIndisponivelException>().WithMessage($"*{DataId}*");
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Mensagens.Should().BeEmpty();
        _adicionado!.Processado.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_PagamentoDoGatewayExisteMasSemPagamentoLocal_DeveIgnorarComLogEMarcarProcessado()
    {
        var referencia = Guid.NewGuid().ToString();
        DadoGateway(new PagamentoGatewayDtoBuilder().ComId(DataId).ComReferenciaExterna(referencia).Build());

        var act = () => _handler.Handle(Comando(), CancellationToken.None);

        await act.Should().NotThrowAsync();
        _loggerMock.VerificarLog(LogLevel.Warning, referencia);
        _gravador.Mensagens.Should().BeEmpty();
        _adicionado!.Processado.Should().BeTrue();
        _gravador.Ordem.Should().Equal("Save");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nao-e-um-guid")]
    public async Task Handle_ReferenciaExternaAusenteOuInvalida_DeveIgnorarSemBuscarPagamentoLocal(string? referencia)
    {
        DadoGateway(new PagamentoGatewayDtoBuilder().ComId(DataId).ComReferenciaExterna(referencia).Build());

        await _handler.Handle(Comando(), CancellationToken.None);

        _pagamentoRepositoryMock.Verify(r => r.ObterPorIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Mensagens.Should().BeEmpty();
        _adicionado!.Processado.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_ValorAprovadoDiferenteDoPagamento_NaoDeveAprovarMasDeveRegistrarATentativaELogEMetricaDeDivergencia()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComValor(100m).ComPreferencia());
        DadoGateway(new PagamentoGatewayDtoBuilder().ComId(DataId).DoPagamento(pagamento).ComValor(150m).Build());

        var act = () => _handler.Handle(Comando(), CancellationToken.None);

        await act.Should().NotThrowAsync();
        pagamento.Status.Should().Be(StatusPagamento.Pendente);
        pagamento.Tentativas.Should().ContainSingle();
        _gravador.Mensagens.Should().BeEmpty();
        _adicionado!.Processado.Should().BeTrue();
        _metricasMock.Verify(m => m.RegistrarDivergenciaDePagamento(), Times.Once());
        _loggerMock.VerificarLog(LogLevel.Error, "valor 150");
        _loggerMock.VerificarLog(LogLevel.Error, "esperado: valor 100");
    }

    [Fact]
    public async Task Handle_MoedaDiferenteDeBrl_NaoDeveAprovarERegistrarADivergencia()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComPreferencia());
        DadoGateway(new PagamentoGatewayDtoBuilder().ComId(DataId).DoPagamento(pagamento).ComMoeda("USD").Build());

        await _handler.Handle(Comando(), CancellationToken.None);

        pagamento.Status.Should().Be(StatusPagamento.Pendente);
        _gravador.Mensagens.Should().BeEmpty();
        _metricasMock.Verify(m => m.RegistrarDivergenciaDePagamento(), Times.Once());
        _loggerMock.VerificarLog(LogLevel.Error, "moeda USD");
    }

    [Fact]
    public async Task Handle_AprovacaoSemDivergencia_NaoDeveRegistrarMetricaDeExcecao()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComPreferencia());
        DadoGateway(GatewayAprovadoPara(pagamento));

        await _handler.Handle(Comando(), CancellationToken.None);

        _metricasMock.Verify(m => m.RegistrarPagamentoAprovado(), Times.Once());
        _metricasMock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handle_AprovacaoTardia_DeveEnviarOEstornoParaAFilaDoBillingNoMesmoSaveDoWebhook(bool pagamentoExpirado)
    {
        var pagamento = DadoPagamento(pagamentoExpirado ? new PagamentoBuilder().Expirado() : new PagamentoBuilder().Cancelado());
        DadoGateway(GatewayAprovadoPara(pagamento));

        await _handler.Handle(Comando(), CancellationToken.None);

        pagamento.AprovacaoTardia.Should().BeTrue();
        pagamento.GatewayPaymentIdAprovado.Should().Be(DataId);
        var envio = _gravador.Envios.Should().ContainSingle().Subject;
        envio.Fila.Should().Be(Filas.Billing);
        var comando = envio.Mensagem.Should().BeOfType<EstornarPagamento>().Subject;
        comando.PagamentoId.Should().Be(pagamento.Id);
        comando.OrdemDeServicoId.Should().Be(pagamento.OrdemDeServicoId);
        comando.Motivo.Should().Contain("depois de cancelado ou expirado");
        comando.OcorridoEm.Should().Be(_relogio.GetUtcNow());
        comando.MessageId.Should().NotBe(Guid.Empty);
        _gravador.Publicadas<PagamentoAprovado>().Should().BeEmpty();
        _adicionado!.Processado.Should().BeTrue();
        _gravador.Ordem.Should().Equal("Send", "Save");
    }

    [Fact]
    public async Task Handle_AprovacaoTardia_DeveRegistrarAMetricaELogApenasNaPrimeiraVez()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().Cancelado());
        DadoGateway(GatewayAprovadoPara(pagamento));

        await _handler.Handle(Comando("req-1"), CancellationToken.None);
        await _handler.Handle(Comando("req-2"), CancellationToken.None);

        _metricasMock.Verify(m => m.RegistrarAprovacaoTardia(), Times.Once());
        _loggerMock.VerificarLog(LogLevel.Warning, "estorno automático solicitado");
    }

    [Fact]
    public async Task Handle_ReentregaDeAprovacaoTardiaAntesDeQualquerTentativaDeEstorno_DeveEnviarOComandoComOMesmoMessageId()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().Cancelado());
        DadoGateway(GatewayAprovadoPara(pagamento));

        await _handler.Handle(Comando("req-1"), CancellationToken.None);
        await _handler.Handle(Comando("req-2"), CancellationToken.None);

        var comandos = _gravador.Enviadas<EstornarPagamento>();
        comandos.Should().HaveCount(2);
        comandos[1].MessageId.Should().Be(comandos[0].MessageId);
        _gravador.Ordem.Should().Equal("Send", "Save", "Send", "Save");
    }

    [Fact]
    public async Task Handle_AprovacaoTardiaComEstornoEmAndamento_DeveReenviarOComandoParaRetomarOEstorno()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComGatewayPaymentId(DataId).AprovadoAposCancelamento());
        pagamento.IniciarEstorno();
        DadoGateway(GatewayAprovadoPara(pagamento));

        await _handler.Handle(Comando(), CancellationToken.None);

        _gravador.Enviadas<EstornarPagamento>().Should().ContainSingle().Which.PagamentoId.Should().Be(pagamento.Id);
        _gravador.Ordem.Should().Equal("Send", "Save");
        _metricasMock.Verify(m => m.RegistrarAprovacaoTardia(), Times.Never());
    }

    [Fact]
    public async Task Handle_AprovacaoTardiaComEstornoFalho_DeveReenviarOComandoComMessageIdDiferenteDoAnterior()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComGatewayPaymentId(DataId).AprovadoAposExpiracao());
        DadoGateway(GatewayAprovadoPara(pagamento));
        await _handler.Handle(Comando("req-1"), CancellationToken.None);
        pagamento.IniciarEstorno();
        pagamento.FalharEstorno("Falha de comunicação com o gateway.");

        await _handler.Handle(Comando("req-2"), CancellationToken.None);

        var comandos = _gravador.Enviadas<EstornarPagamento>();
        comandos.Should().HaveCount(2);
        comandos[1].MessageId.Should().NotBe(comandos[0].MessageId);
    }

    [Fact]
    public async Task Handle_AprovacaoTardiaJaEstornada_NaoDeveEnviarNada()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComGatewayPaymentId(DataId).AprovadoAposCancelamento());
        pagamento.IniciarEstorno();
        pagamento.ConfirmarEstorno("refund-1");
        DadoGateway(GatewayAprovadoPara(pagamento));

        await _handler.Handle(Comando(), CancellationToken.None);

        _gravador.Mensagens.Should().BeEmpty();
        _gravador.Ordem.Should().Equal("Save");
    }

    [Fact]
    public async Task Handle_SegundoPagamentoAprovadoNoGateway_DeveRegistrarATentativaSinalizarDuplicidadeSemPublicar()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComGatewayPaymentId("999").Aprovado());
        DadoGateway(new PagamentoGatewayDtoBuilder().ComId(DataId).DoPagamento(pagamento).Build());

        await _handler.Handle(Comando(), CancellationToken.None);

        pagamento.GatewayPaymentIdAprovado.Should().Be("999");
        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
        pagamento.Tentativas.Select(t => t.GatewayPaymentId).Should().BeEquivalentTo(new[] { "999", DataId });
        _gravador.Mensagens.Should().BeEmpty();
        _metricasMock.Verify(m => m.RegistrarAprovacaoDuplicada(), Times.Once());
        _loggerMock.VerificarLog(LogLevel.Warning, "999");
        _loggerMock.VerificarLog(LogLevel.Warning, DataId);
        _adicionado!.Processado.Should().BeTrue();
        _gravador.Ordem.Should().Equal("Save");
    }

    [Fact]
    public async Task Handle_WebhookRecebidoMasNaoProcessado_DeveReaproveitarORegistroEProcessar()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComPreferencia());
        DadoGateway(GatewayAprovadoPara(pagamento));
        var anterior = new WebhookRecebidoBuilder().ComRequestId(RequestId).ComDataId(DataId).Recebido();
        _webhookRepositoryMock
            .Setup(r => r.ObterPorRequestIdAsync(RequestId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(anterior);

        await _handler.Handle(Comando(), CancellationToken.None);

        _webhookRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<WebhookRecebido>(), It.IsAny<CancellationToken>()), Times.Never());
        anterior.Processado.Should().BeTrue();
        _gravador.Publicadas<PagamentoAprovado>().Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_GatewayIndisponivelNaConsulta_DevePropagarSemPersistirParaOGatewayReentregar()
    {
        _gatewayMock
            .Setup(g => g.ObterPagamentoAsync(DataId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("503"));

        var act = () => _handler.Handle(Comando(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Mensagens.Should().BeEmpty();
    }

    private void DadoSaveChangesQueFalha() =>
        _unitOfWorkMock
            .Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("conflito de concorrência"));

    [Fact]
    public async Task Handle_Divergencia_DeveContarELogarSoDepoisDoSaveChanges()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComValor(100m).ComPreferencia());
        DadoGateway(new PagamentoGatewayDtoBuilder().ComId(DataId).DoPagamento(pagamento).ComValor(150m).Build());
        _metricasMock.Setup(m => m.RegistrarDivergenciaDePagamento()).Callback(() => _gravador.Registrar("Metrica"));

        await _handler.Handle(Comando(), CancellationToken.None);

        _gravador.Ordem.Should().Equal("Save", "Metrica");
    }

    [Fact]
    public async Task Handle_SaveChangesFalhaNaDivergencia_NaoDeveContarNemLogar()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComValor(100m).ComPreferencia());
        DadoGateway(new PagamentoGatewayDtoBuilder().ComId(DataId).DoPagamento(pagamento).ComValor(150m).Build());
        DadoSaveChangesQueFalha();

        var act = () => _handler.Handle(Comando(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _metricasMock.Verify(m => m.RegistrarDivergenciaDePagamento(), Times.Never());
        _loggerMock.VerificarLog(LogLevel.Error, "dados divergentes", Times.Never());
    }

    [Fact]
    public async Task Handle_AprovacaoTardia_DeveContarELogarSoDepoisDoSaveChanges()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().Cancelado());
        DadoGateway(GatewayAprovadoPara(pagamento));
        _metricasMock.Setup(m => m.RegistrarAprovacaoTardia()).Callback(() => _gravador.Registrar("Metrica"));

        await _handler.Handle(Comando(), CancellationToken.None);

        _gravador.Ordem.Should().Equal("Send", "Save", "Metrica");
    }

    [Fact]
    public async Task Handle_SaveChangesFalhaNaAprovacaoTardia_NaoDeveContarNemLogar()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().Cancelado());
        DadoGateway(GatewayAprovadoPara(pagamento));
        DadoSaveChangesQueFalha();

        var act = () => _handler.Handle(Comando(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _metricasMock.Verify(m => m.RegistrarAprovacaoTardia(), Times.Never());
        _loggerMock.VerificarLog(LogLevel.Warning, "estorno automático solicitado", Times.Never());
    }

    [Fact]
    public async Task Handle_AprovacaoDuplicada_DeveContarELogarSoDepoisDoSaveChanges()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComGatewayPaymentId("999").Aprovado());
        DadoGateway(new PagamentoGatewayDtoBuilder().ComId(DataId).DoPagamento(pagamento).Build());
        _metricasMock.Setup(m => m.RegistrarAprovacaoDuplicada()).Callback(() => _gravador.Registrar("Metrica"));

        await _handler.Handle(Comando(), CancellationToken.None);

        _gravador.Ordem.Should().Equal("Save", "Metrica");
    }

    [Fact]
    public async Task Handle_SaveChangesFalhaNaAprovacaoDuplicada_NaoDeveContarNemLogar()
    {
        var pagamento = DadoPagamento(new PagamentoBuilder().ComGatewayPaymentId("999").Aprovado());
        DadoGateway(new PagamentoGatewayDtoBuilder().ComId(DataId).DoPagamento(pagamento).Build());
        DadoSaveChangesQueFalha();

        var act = () => _handler.Handle(Comando(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _metricasMock.Verify(m => m.RegistrarAprovacaoDuplicada(), Times.Never());
        _loggerMock.VerificarLog(LogLevel.Warning, "também foi aprovado", Times.Never());
    }
}
