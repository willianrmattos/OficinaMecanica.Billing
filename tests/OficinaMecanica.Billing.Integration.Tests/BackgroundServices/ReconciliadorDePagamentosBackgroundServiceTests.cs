using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OficinaMecanica.Billing.Application.Commands.ReconciliarPagamento;
using OficinaMecanica.Billing.Application.Queries.ListarPagamentosParaReconciliacao;
using OficinaMecanica.Billing.Infrastructure.BackgroundServices;
using OficinaMecanica.Billing.Infrastructure.Options;

namespace OficinaMecanica.Billing.Integration.Tests.BackgroundServices;

public class ReconciliadorDePagamentosBackgroundServiceTests
{
    private readonly Mock<IMediator> _mediatorMock = new();
    private readonly FakeTimeProvider _relogio = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly List<ReconciliarPagamentoCommand> _comandos = new();
    private int _listagens;
    private IReadOnlyList<Guid> _ids = Array.Empty<Guid>();

    public ReconciliadorDePagamentosBackgroundServiceTests()
    {
        _mediatorMock
            .Setup(m => m.Send(It.IsAny<ListarPagamentosParaReconciliacaoQuery>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref _listagens);
                return Task.FromResult(_ids);
            });
        _mediatorMock
            .Setup(m => m.Send(It.IsAny<ReconciliarPagamentoCommand>(), It.IsAny<CancellationToken>()))
            .Callback<ReconciliarPagamentoCommand, CancellationToken>((c, _) => { lock (_comandos) _comandos.Add(c); })
            .Returns(Task.CompletedTask);
    }

    private ReconciliadorDePagamentosBackgroundService Criar(Action<OpcoesDoBilling>? ajustar = null, ILogger<ReconciliadorDePagamentosBackgroundService>? logger = null)
    {
        var opcoes = new OpcoesDoBilling { ReconciliadorIntervaloSegundos = 300, ReconciliadorTamanhoDoLote = 25, ReconciliadorMaximoDeTentativasDeEstorno = 7 };
        ajustar?.Invoke(opcoes);

        var provedor = new ServiceCollection().AddSingleton(_mediatorMock.Object).BuildServiceProvider();
        return new ReconciliadorDePagamentosBackgroundService(
            provedor.GetRequiredService<IServiceScopeFactory>(), Options.Create(opcoes), _relogio,
            logger ?? NullLogger<ReconciliadorDePagamentosBackgroundService>.Instance);
    }

    private static async Task AguardarAsync(Func<bool> condicao)
    {
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (!condicao() && DateTime.UtcNow < limite)
            await Task.Delay(10);

        condicao().Should().BeTrue("a condição deveria ter sido atingida em até 10 segundos");
    }

    [Fact]
    public async Task ExecutarCiclo_DeveListarEReconciliarCadaPagamentoComOTetoDeTentativasConfigurado()
    {
        _ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var reconciliados = await Criar().ExecutarCicloAsync(CancellationToken.None);

        reconciliados.Should().Be(3);
        _mediatorMock.Verify(m => m.Send(It.Is<ListarPagamentosParaReconciliacaoQuery>(q => q.Limite == 25), It.IsAny<CancellationToken>()), Times.Once);
        _comandos.Select(c => c.PagamentoId).Should().Equal(_ids);
        _comandos.Should().OnlyContain(c => c.MaximoDeTentativasDeEstorno == 7);
    }

    [Fact]
    public async Task ExecutarCiclo_SemPagamentosPendentes_NaoDeveReconciliarNada()
    {
        var reconciliados = await Criar().ExecutarCicloAsync(CancellationToken.None);

        reconciliados.Should().Be(0);
        _comandos.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecutarCiclo_FalhaNaListagem_DeveLogarEDevolverZeroSemLancar()
    {
        _mediatorMock
            .Setup(m => m.Send(It.IsAny<ListarPagamentosParaReconciliacaoQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("banco indisponível"));
        var logger = new Mock<ILogger<ReconciliadorDePagamentosBackgroundService>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var reconciliados = await Criar(logger: logger.Object).ExecutarCicloAsync(CancellationToken.None);

        reconciliados.Should().Be(0);
        logger.Verify(
            l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<InvalidOperationException>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecutarCiclo_FalhaEmUmPagamento_DeveSeguirComOsDemais()
    {
        var falho = Guid.NewGuid();
        _ids = new[] { Guid.NewGuid(), falho, Guid.NewGuid() };
        _mediatorMock
            .Setup(m => m.Send(It.Is<ReconciliarPagamentoCommand>(c => c.PagamentoId == falho), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("conflito de concorrência"));

        var reconciliados = await Criar().ExecutarCicloAsync(CancellationToken.None);

        reconciliados.Should().Be(2);
        _mediatorMock.Verify(m => m.Send(It.IsAny<ReconciliarPagamentoCommand>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task ExecutarCiclo_CancelamentoDuranteOCiclo_DevePropagarACancelamento()
    {
        _ids = new[] { Guid.NewGuid() };
        using var cancelamento = new CancellationTokenSource();
        await cancelamento.CancelAsync();

        var act = () => Criar().ExecutarCicloAsync(cancelamento.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Start_DeveRodarUmCicloImediatoEOutroACadaIntervalo()
    {
        _ids = new[] { Guid.NewGuid() };
        var servico = Criar();

        await servico.StartAsync(CancellationToken.None);
        await AguardarAsync(() => Volatile.Read(ref _listagens) == 1);

        _relogio.Advance(TimeSpan.FromSeconds(299));
        await Task.Delay(100);
        Volatile.Read(ref _listagens).Should().Be(1, "ainda não passou o intervalo");

        _relogio.Advance(TimeSpan.FromSeconds(1));
        await AguardarAsync(() => Volatile.Read(ref _listagens) == 2);

        await servico.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Start_IntervaloConfiguravel_DeveRespeitarOValorInformado()
    {
        var servico = Criar(o => o.ReconciliadorIntervaloSegundos = 10);

        await servico.StartAsync(CancellationToken.None);
        await AguardarAsync(() => Volatile.Read(ref _listagens) == 1);
        _relogio.Advance(TimeSpan.FromSeconds(10));
        await AguardarAsync(() => Volatile.Read(ref _listagens) == 2);

        await servico.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Start_Desabilitado_NaoDeveReconciliarNada()
    {
        var servico = Criar(o => o.ReconciliadorHabilitado = false);

        await servico.StartAsync(CancellationToken.None);
        _relogio.Advance(TimeSpan.FromHours(1));
        await Task.Delay(100);
        await servico.StopAsync(CancellationToken.None);

        Volatile.Read(ref _listagens).Should().Be(0);
    }

    [Fact]
    public async Task Stop_DuranteAEspera_DeveEncerrarSemLancar()
    {
        var servico = Criar();
        await servico.StartAsync(CancellationToken.None);
        await AguardarAsync(() => Volatile.Read(ref _listagens) == 1);

        var act = () => servico.StopAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
