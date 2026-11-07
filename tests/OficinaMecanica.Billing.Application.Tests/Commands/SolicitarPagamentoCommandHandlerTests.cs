using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OficinaMecanica.Billing.Application.Commands.SolicitarPagamento;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;
using OficinaMecanica.Billing.Domain.Interfaces;
using OficinaMecanica.Contracts.Events;

namespace OficinaMecanica.Billing.Application.Tests.Commands;

public class SolicitarPagamentoCommandHandlerTests
{
    private const string LinkDoGateway = "https://mp.test/checkout?pref_id=pref-123";

    private readonly Mock<IOrcamentoRepository> _orcamentoRepositoryMock = new();
    private readonly Mock<IPagamentoRepository> _pagamentoRepositoryMock = new();
    private readonly Mock<ICancelamentoPrevioRepository> _cancelamentoPrevioRepositoryMock = new();
    private readonly Mock<IPagamentoGateway> _gatewayMock = new();
    private readonly Mock<IIntegrationEventPublisher> _publisherMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly RelogioFixo _relogio = new();
    private readonly GravadorDeChamadas _gravador;
    private readonly SolicitarPagamentoCommandHandler _handler;
    private Pagamento? _adicionado;

    public SolicitarPagamentoCommandHandlerTests()
    {
        _gravador = new GravadorDeChamadas(_publisherMock, _unitOfWorkMock);
        _pagamentoRepositoryMock
            .Setup(r => r.AdicionarAsync(It.IsAny<Pagamento>(), It.IsAny<CancellationToken>()))
            .Callback<Pagamento, CancellationToken>((p, _) => _adicionado = p);
        _gatewayMock
            .Setup(g => g.CriarPreferenciaAsync(It.IsAny<CriarPreferenciaGatewayRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => _gravador.Registrar("Gateway"))
            .ReturnsAsync(new PreferenciaGatewayDto("pref-123", LinkDoGateway));

        _handler = new SolicitarPagamentoCommandHandler(
            _orcamentoRepositoryMock.Object, _pagamentoRepositoryMock.Object, _cancelamentoPrevioRepositoryMock.Object,
            _gatewayMock.Object, _publisherMock.Object, _unitOfWorkMock.Object, _relogio,
            NullLogger<SolicitarPagamentoCommandHandler>.Instance);
    }

    private Orcamento DadoOrcamento(Orcamento orcamento)
    {
        _orcamentoRepositoryMock
            .Setup(r => r.ObterPorIdAsync(orcamento.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orcamento);
        return orcamento;
    }

    private void DadoPagamentoExistente(Pagamento pagamento) =>
        _pagamentoRepositoryMock
            .Setup(r => r.ObterPorOrdemDeServicoIdAsync(pagamento.OrdemDeServicoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagamento);

    private SolicitarPagamentoCommand CommandPara(Orcamento orcamento) =>
        new(orcamento.OrdemDeServicoId, orcamento.Id, _relogio.GetUtcNow().AddDays(2));

    [Fact]
    public async Task Handle_OrcamentoGerado_DeveAprovarOrcamentoCriarPagamentoEPublicarPagamentoSolicitado()
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().Gerado());
        var command = CommandPara(orcamento);

        await _handler.Handle(command, CancellationToken.None);

        orcamento.Status.Should().Be(StatusOrcamento.Aprovado);
        _adicionado.Should().NotBeNull();
        _adicionado!.OrcamentoId.Should().Be(orcamento.Id);
        _adicionado.OrdemDeServicoId.Should().Be(orcamento.OrdemDeServicoId);
        _adicionado.Valor.Should().Be(orcamento.ValorTotal);
        _adicionado.Status.Should().Be(StatusPagamento.Pendente);
        _adicionado.PreferenceId.Should().Be("pref-123");
        _adicionado.LinkPagamento.Should().Be(LinkDoGateway);

        var evento = _gravador.Publicadas<PagamentoSolicitado>().Should().ContainSingle().Subject;
        evento.OrdemDeServicoId.Should().Be(orcamento.OrdemDeServicoId);
        evento.PagamentoId.Should().Be(_adicionado.Id);
        evento.LinkPagamento.Should().Be(LinkDoGateway);
        evento.ExpiraEm.Should().Be(command.ExpiraEm);
        evento.OcorridoEm.Should().Be(_relogio.GetUtcNow());
    }

    [Fact]
    public async Task Handle_OrcamentoGerado_DeveSalvarOPagamentoPendenteAntesDeChamarOGatewayEPublicarAntesDoSaveFinal()
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().Gerado());

        await _handler.Handle(CommandPara(orcamento), CancellationToken.None);

        _gravador.Ordem.Should().Equal("Save", "Gateway", "Publish", "Save");
    }

    [Fact]
    public async Task Handle_OrcamentoGerado_DeveEnviarAoGatewayOsDadosDoPagamento()
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().ComNumero("OS-20261107-ABC123").Gerado());
        var command = CommandPara(orcamento);

        await _handler.Handle(command, CancellationToken.None);

        _gatewayMock.Verify(g => g.CriarPreferenciaAsync(
            It.Is<CriarPreferenciaGatewayRequest>(r =>
                r.PagamentoId == _adicionado!.Id &&
                r.Valor == orcamento.ValorTotal &&
                r.ExpiraEm == command.ExpiraEm &&
                r.Descricao.Contains("OS-20261107-ABC123")),
            It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task Handle_OrcamentoInexistente_DevePublicarPagamentoNaoSolicitadoSemChamarOGateway()
    {
        var command = new SolicitarPagamentoCommand(Guid.NewGuid(), Guid.NewGuid(), _relogio.GetUtcNow().AddDays(2));

        await _handler.Handle(command, CancellationToken.None);

        var evento = _gravador.Publicadas<PagamentoNaoSolicitado>().Should().ContainSingle().Subject;
        evento.OrdemDeServicoId.Should().Be(command.OrdemDeServicoId);
        evento.Motivo.Should().Contain("não encontrado");
        _gatewayMock.Verify(g => g.CriarPreferenciaAsync(It.IsAny<CriarPreferenciaGatewayRequest>(), It.IsAny<CancellationToken>()), Times.Never());
        _pagamentoRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<Pagamento>(), It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Fact]
    public async Task Handle_OrcamentoDeOutraOrdemDeServico_DevePublicarPagamentoNaoSolicitado()
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().Gerado());
        var command = new SolicitarPagamentoCommand(Guid.NewGuid(), orcamento.Id, _relogio.GetUtcNow().AddDays(2));

        await _handler.Handle(command, CancellationToken.None);

        _gravador.Publicadas<PagamentoNaoSolicitado>().Should().ContainSingle().Which.OrdemDeServicoId.Should().Be(command.OrdemDeServicoId);
        orcamento.Status.Should().Be(StatusOrcamento.Gerado);
    }

    [Fact]
    public async Task Handle_OrcamentoComPrazoVencido_DeveExpirarOOrcamentoEPublicarPagamentoNaoSolicitado()
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().Gerado());
        var command = CommandPara(orcamento);
        _relogio.Avancar(TimeSpan.FromDays(4));

        await _handler.Handle(command, CancellationToken.None);

        orcamento.Status.Should().Be(StatusOrcamento.Expirado);
        _gravador.Publicadas<PagamentoNaoSolicitado>().Should().ContainSingle().Which.Motivo.Should().Contain("expirou");
        _pagamentoRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<Pagamento>(), It.IsAny<CancellationToken>()), Times.Never());
        _gatewayMock.Verify(g => g.CriarPreferenciaAsync(It.IsAny<CriarPreferenciaGatewayRequest>(), It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Fact]
    public async Task Handle_OrcamentoCancelado_DevePublicarPagamentoNaoSolicitadoSemCriarPagamento()
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().Cancelado());

        await _handler.Handle(CommandPara(orcamento), CancellationToken.None);

        orcamento.Status.Should().Be(StatusOrcamento.Cancelado);
        _gravador.Publicadas<PagamentoNaoSolicitado>().Should().ContainSingle().Which.Motivo.Should().Contain("Cancelado");
        _pagamentoRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<Pagamento>(), It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Fact]
    public async Task Handle_OrcamentoAprovadoSemPagamento_DevePublicarPagamentoNaoSolicitado()
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().Aprovado());

        await _handler.Handle(CommandPara(orcamento), CancellationToken.None);

        _gravador.Publicadas<PagamentoNaoSolicitado>().Should().ContainSingle().Which.Motivo.Should().Contain("Aprovado");
        _pagamentoRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<Pagamento>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Handle_PrazoDoPagamentoNoPassado_DevePublicarPagamentoNaoSolicitadoSemAprovarOOrcamento()
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().Gerado());
        var command = CommandPara(orcamento) with { ExpiraEm = _relogio.GetUtcNow().AddMinutes(-1) };

        await _handler.Handle(command, CancellationToken.None);

        orcamento.Status.Should().Be(StatusOrcamento.Gerado);
        _gravador.Publicadas<PagamentoNaoSolicitado>().Should().ContainSingle().Which.Motivo.Should().Contain("prazo");
        _pagamentoRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<Pagamento>(), It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Fact]
    public async Task Handle_GatewayRecusaPermanentemente_DeveCancelarOPagamentoPendenteEPublicarPagamentoNaoSolicitado()
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().Gerado());
        _gatewayMock
            .Setup(g => g.CriarPreferenciaAsync(It.IsAny<CriarPreferenciaGatewayRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => _gravador.Registrar("Gateway"))
            .ThrowsAsync(new PagamentoRecusadoPeloGatewayException("400 invalid_items: dados do pagador"));

        var act = () => _handler.Handle(CommandPara(orcamento), CancellationToken.None);

        await act.Should().NotThrowAsync();
        _adicionado!.Status.Should().Be(StatusPagamento.Cancelado);
        _adicionado.PreferenceId.Should().BeNull();
        orcamento.Status.Should().Be(StatusOrcamento.Aprovado);
        _gravador.Publicadas<PagamentoSolicitado>().Should().BeEmpty();
        var evento = _gravador.Publicadas<PagamentoNaoSolicitado>().Should().ContainSingle().Subject;
        evento.OrdemDeServicoId.Should().Be(orcamento.OrdemDeServicoId);
        evento.Motivo.Should().Be("O gateway recusou a criação do pagamento.");
        _gravador.Ordem.Should().Equal("Save", "Gateway", "Publish", "Save");
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task Handle_FalhaTecnicaDoGateway_DevePropagarParaORetryMantendoOPagamentoPendenteSemPublicarNada(Type tipoDaExcecao)
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().Gerado());
        _gatewayMock
            .Setup(g => g.CriarPreferenciaAsync(It.IsAny<CriarPreferenciaGatewayRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => _gravador.Registrar("Gateway"))
            .ThrowsAsync((Exception)Activator.CreateInstance(tipoDaExcecao, "503 Service Unavailable")!);

        var act = () => _handler.Handle(CommandPara(orcamento), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>().Where(e => e.GetType() == tipoDaExcecao);
        _adicionado!.Status.Should().Be(StatusPagamento.Pendente);
        _adicionado.PreferenceId.Should().BeNull();
        orcamento.Status.Should().Be(StatusOrcamento.Aprovado);
        _gravador.Mensagens.Should().BeEmpty();
        _gravador.Ordem.Should().Equal("Save", "Gateway");
    }

    [Fact]
    public async Task Handle_ReentregaAposFalhaTecnicaDoGateway_DeveRetomarOPagamentoPreExistenteEPublicarPagamentoSolicitado()
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().Gerado());
        var command = CommandPara(orcamento);
        _gatewayMock
            .Setup(g => g.CriarPreferenciaAsync(It.IsAny<CriarPreferenciaGatewayRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => _gravador.Registrar("Gateway"))
            .ThrowsAsync(new HttpRequestException("503 Service Unavailable"));
        await FluentActions.Awaiting(() => _handler.Handle(command, CancellationToken.None)).Should().ThrowAsync<HttpRequestException>();
        var pagamentoPreExistente = _adicionado!;
        DadoPagamentoExistente(pagamentoPreExistente);
        _gatewayMock
            .Setup(g => g.CriarPreferenciaAsync(It.IsAny<CriarPreferenciaGatewayRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => _gravador.Registrar("Gateway"))
            .ReturnsAsync(new PreferenciaGatewayDto("pref-123", LinkDoGateway));

        await _handler.Handle(command, CancellationToken.None);

        _pagamentoRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<Pagamento>(), It.IsAny<CancellationToken>()), Times.Once());
        pagamentoPreExistente.PreferenceId.Should().Be("pref-123");
        pagamentoPreExistente.Status.Should().Be(StatusPagamento.Pendente);
        var evento = _gravador.Publicadas<PagamentoSolicitado>().Should().ContainSingle().Subject;
        evento.PagamentoId.Should().Be(pagamentoPreExistente.Id);
        evento.LinkPagamento.Should().Be(LinkDoGateway);
        _gravador.Ordem.Should().Equal("Save", "Gateway", "Gateway", "Publish", "Save");
    }

    [Fact]
    public async Task Handle_GatewayDevolveLinkVazio_DevePropagarDomainExceptionSemPublicarNemCancelarOPagamento()
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().Gerado());
        _gatewayMock
            .Setup(g => g.CriarPreferenciaAsync(It.IsAny<CriarPreferenciaGatewayRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PreferenciaGatewayDto("pref-123", ""));

        var act = () => _handler.Handle(CommandPara(orcamento), CancellationToken.None);

        await act.Should().ThrowAsync<DomainException>().WithMessage("*link de pagamento*");
        _adicionado!.Status.Should().Be(StatusPagamento.Pendente);
        _gravador.Mensagens.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_CancelamentoDaOperacaoDuranteOGateway_DevePropagarSemPublicarNada()
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().Gerado());
        _gatewayMock
            .Setup(g => g.CriarPreferenciaAsync(It.IsAny<CriarPreferenciaGatewayRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => _handler.Handle(CommandPara(orcamento), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _gravador.Mensagens.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ReentregaComPagamentoPendenteEPreferencia_DeveRepublicarOMesmoPagamentoSolicitadoSemNovoGateway()
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().Gerado());
        var command = CommandPara(orcamento);
        await _handler.Handle(command, CancellationToken.None);
        DadoPagamentoExistente(_adicionado!);

        await _handler.Handle(command, CancellationToken.None);

        var eventos = _gravador.Publicadas<PagamentoSolicitado>();
        eventos.Should().HaveCount(2);
        eventos[1].MessageId.Should().Be(eventos[0].MessageId);
        eventos[1].LinkPagamento.Should().Be(LinkDoGateway);
        eventos[1].PagamentoId.Should().Be(_adicionado!.Id);
        _gatewayMock.Verify(g => g.CriarPreferenciaAsync(It.IsAny<CriarPreferenciaGatewayRequest>(), It.IsAny<CancellationToken>()), Times.Once());
        _pagamentoRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<Pagamento>(), It.IsAny<CancellationToken>()), Times.Once());
        _gravador.Ordem.TakeLast(2).Should().Equal("Publish", "Save");
    }

    [Fact]
    public async Task Handle_ReentregaComPagamentoPendenteSemPreferencia_DeveRetomarOGatewaySemCriarOutroPagamento()
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().Aprovado());
        var pendente = new PagamentoBuilder().DoOrcamento(orcamento).Pendente();
        DadoPagamentoExistente(pendente);

        await _handler.Handle(CommandPara(orcamento), CancellationToken.None);

        _pagamentoRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<Pagamento>(), It.IsAny<CancellationToken>()), Times.Never());
        pendente.PreferenceId.Should().Be("pref-123");
        _gravador.Publicadas<PagamentoSolicitado>().Should().ContainSingle().Which.PagamentoId.Should().Be(pendente.Id);
        _gravador.Ordem.Should().Equal("Gateway", "Publish", "Save");
    }

    [Fact]
    public async Task Handle_PagamentoJaAprovado_DevePublicarPagamentoAprovadoSemChamarOGateway()
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().Aprovado());
        var aprovado = new PagamentoBuilder().DoOrcamento(orcamento).ComGatewayPaymentId("999").Aprovado();
        DadoPagamentoExistente(aprovado);

        await _handler.Handle(CommandPara(orcamento), CancellationToken.None);

        var evento = _gravador.Publicadas<PagamentoAprovado>().Should().ContainSingle().Subject;
        evento.PagamentoId.Should().Be(aprovado.Id);
        evento.GatewayPaymentId.Should().Be("999");
        evento.Valor.Should().Be(aprovado.Valor);
        _gravador.Publicadas<PagamentoSolicitado>().Should().BeEmpty();
        _gatewayMock.Verify(g => g.CriarPreferenciaAsync(It.IsAny<CriarPreferenciaGatewayRequest>(), It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Fact]
    public async Task Handle_PagamentoJaCancelado_DevePublicarPagamentoNaoSolicitadoComOStatusAtual()
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().Aprovado());
        DadoPagamentoExistente(new PagamentoBuilder().DoOrcamento(orcamento).Cancelado());

        await _handler.Handle(CommandPara(orcamento), CancellationToken.None);

        _gravador.Publicadas<PagamentoNaoSolicitado>().Should().ContainSingle().Which.Motivo.Should().Contain("Cancelado");
        _gatewayMock.Verify(g => g.CriarPreferenciaAsync(It.IsAny<CriarPreferenciaGatewayRequest>(), It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Fact]
    public async Task Handle_CancelamentoPrevioDoPagamento_NaoDeveCriarNadaEDevePublicarPagamentoNaoSolicitado()
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().Gerado());
        _cancelamentoPrevioRepositoryMock
            .Setup(r => r.ObterAsync(orcamento.OrdemDeServicoId, TipoCancelamentoPrevio.Pagamento, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CancelamentoPrevioBuilder().DaOrdemDeServico(orcamento.OrdemDeServicoId).DoTipo(TipoCancelamentoPrevio.Pagamento).Build());

        await _handler.Handle(CommandPara(orcamento), CancellationToken.None);

        orcamento.Status.Should().Be(StatusOrcamento.Gerado);
        _pagamentoRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<Pagamento>(), It.IsAny<CancellationToken>()), Times.Never());
        _gatewayMock.Verify(g => g.CriarPreferenciaAsync(It.IsAny<CriarPreferenciaGatewayRequest>(), It.IsAny<CancellationToken>()), Times.Never());
        var evento = _gravador.Publicadas<PagamentoNaoSolicitado>().Should().ContainSingle().Subject;
        evento.Motivo.Should().Be("Cancelado antes da solicitação do pagamento.");
        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Fact]
    public async Task Handle_ReentregaComCancelamentoPrevio_DevePublicarPagamentoNaoSolicitadoComOMesmoMessageId()
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().Gerado());
        _cancelamentoPrevioRepositoryMock
            .Setup(r => r.ObterAsync(orcamento.OrdemDeServicoId, TipoCancelamentoPrevio.Pagamento, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CancelamentoPrevioBuilder().DaOrdemDeServico(orcamento.OrdemDeServicoId).DoTipo(TipoCancelamentoPrevio.Pagamento).Build());

        await _handler.Handle(CommandPara(orcamento), CancellationToken.None);
        await _handler.Handle(CommandPara(orcamento), CancellationToken.None);

        var eventos = _gravador.Publicadas<PagamentoNaoSolicitado>();
        eventos.Should().HaveCount(2);
        eventos[1].MessageId.Should().Be(eventos[0].MessageId);
    }

    [Fact]
    public async Task Handle_CancelamentoPrevioSoDeOrcamento_NaoDeveImpedirOPagamento()
    {
        var orcamento = DadoOrcamento(new OrcamentoBuilder().Gerado());
        _cancelamentoPrevioRepositoryMock
            .Setup(r => r.ObterAsync(orcamento.OrdemDeServicoId, TipoCancelamentoPrevio.Orcamento, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CancelamentoPrevioBuilder().DaOrdemDeServico(orcamento.OrdemDeServicoId).Build());

        await _handler.Handle(CommandPara(orcamento), CancellationToken.None);

        _gravador.Publicadas<PagamentoSolicitado>().Should().ContainSingle();
    }
}
