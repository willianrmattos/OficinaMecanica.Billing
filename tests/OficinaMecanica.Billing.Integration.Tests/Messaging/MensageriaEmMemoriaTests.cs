using System.Collections.Concurrent;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Domain.Exceptions;
using OficinaMecanica.Billing.Infrastructure.Messaging;
using OficinaMecanica.Contracts;
using OficinaMecanica.Contracts.Commands;
using OficinaMecanica.Contracts.Common;

namespace OficinaMecanica.Billing.Integration.Tests.Messaging;

// Exercita a composição real de AddMensageria sobre o transporte em memória (sem Docker): o roteamento dos cinco
// comandos para a fila do Billing e a política de retry (o que repete e o que não repete).
public class MensageriaEmMemoriaTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private const int RetriesConfigurados = 3;

    private readonly MediatorProgramavel _mediator = new();
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mensageria:Transporte"] = "EmMemoria",
            ["Mensageria:Persistencia"] = "Nenhuma",
            ["Mensageria:RetryIntervalosMs:0"] = "20",
            ["Mensageria:RetryIntervalosMs:1"] = "20",
            ["Mensageria:RetryIntervalosMs:2"] = "20",
        });
        builder.Services.AddSingleton<IMediator>(_mediator);
        builder.Services.AddMensageria(builder.Configuration);

        _host = builder.Build();
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task EnviarAsync<T>(T mensagem) where T : class
    {
        var endpoint = await _host.Services.GetRequiredService<IBus>().GetSendEndpoint(new Uri($"queue:{Filas.Billing}"));
        await endpoint.Send(mensagem);
    }

    private static GerarOrcamento NovoGerarOrcamento() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Agora, "OS-1", 100m, Agora.AddDays(3), null,
            new[] { new ItemDeServicoMensagem(Guid.NewGuid(), "Serviço", 100m, 1) }, Array.Empty<ItemDePecaMensagem>());

    private async Task<int> AguardarChamadasEstabilizaremAsync(int minimoEsperado)
    {
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (_mediator.Chamadas < minimoEsperado && DateTime.UtcNow < limite)
            await Task.Delay(20);

        // Dá tempo de uma tentativa extra indevida aparecer antes de congelar o número.
        await Task.Delay(400);
        return _mediator.Chamadas;
    }

    [Fact]
    public async Task OsCincoComandos_DevemChegarAoCommandMediatRCorrespondente()
    {
        await EnviarAsync(NovoGerarOrcamento());
        await EnviarAsync(new SolicitarPagamento(Guid.NewGuid(), Guid.NewGuid(), Agora, Guid.NewGuid(), Agora.AddDays(2)));
        await EnviarAsync(new CancelarOrcamento(Guid.NewGuid(), Guid.NewGuid(), Agora, null, "motivo"));
        await EnviarAsync(new CancelarPagamento(Guid.NewGuid(), Guid.NewGuid(), Agora, null, "motivo"));
        await EnviarAsync(new EstornarPagamento(Guid.NewGuid(), Guid.NewGuid(), Agora, Guid.NewGuid(), "motivo"));

        await AguardarChamadasEstabilizaremAsync(5);

        _mediator.TiposRecebidos.Select(t => t.Name).Should().BeEquivalentTo(
            "GerarOrcamentoCommand", "SolicitarPagamentoCommand", "CancelarOrcamentoCommand",
            "CancelarPagamentoCommand", "EstornarPagamentoCommand");
    }

    [Fact]
    public async Task ValidationException_NaoDeveSerRepetida()
    {
        _mediator.Falha = new ValidationException(new[] { new ValidationFailure("OrdemDeServicoId", "obrigatória") });

        await EnviarAsync(NovoGerarOrcamento());

        (await AguardarChamadasEstabilizaremAsync(1)).Should().Be(1);
    }

    [Fact]
    public async Task DomainException_NaoDeveSerRepetida()
    {
        _mediator.Falha = new DomainException("Não é possível cancelar um orçamento já pago.");

        await EnviarAsync(NovoGerarOrcamento());

        (await AguardarChamadasEstabilizaremAsync(1)).Should().Be(1);
    }

    [Fact]
    public async Task AssinaturaWebhookInvalidaException_NaoDeveSerRepetida()
    {
        _mediator.Falha = new AssinaturaWebhookInvalidaException();

        await EnviarAsync(NovoGerarOrcamento());

        (await AguardarChamadasEstabilizaremAsync(1)).Should().Be(1);
    }

    [Fact]
    public async Task PagamentoDoGatewayIndisponivelException_DeveSerRepetidaAteOLimite()
    {
        _mediator.Falha = new PagamentoDoGatewayIndisponivelException("123");

        await EnviarAsync(NovoGerarOrcamento());

        (await AguardarChamadasEstabilizaremAsync(1 + RetriesConfigurados)).Should().Be(1 + RetriesConfigurados);
    }

    [Fact]
    public async Task FalhaTecnicaDoGateway_DeveSerRepetidaAteOLimite()
    {
        _mediator.Falha = new HttpRequestException("gateway fora do ar");

        await EnviarAsync(NovoGerarOrcamento());

        (await AguardarChamadasEstabilizaremAsync(1 + RetriesConfigurados)).Should().Be(1 + RetriesConfigurados);
    }

    [Fact]
    public async Task PagamentoRecusadoPeloGateway_DeveSerTratadaComoFalhaTecnicaPoisOHandlerJaAConverteEmEvento()
    {
        _mediator.Falha = new PagamentoRecusadoPeloGatewayException("recusado");

        await EnviarAsync(NovoGerarOrcamento());

        (await AguardarChamadasEstabilizaremAsync(1 + RetriesConfigurados)).Should().Be(1 + RetriesConfigurados);
    }

    [Fact]
    public async Task FalhaTransitoriaSeguidaDeSucesso_DeveProcessarAMensagemNaTentativaSeguinte()
    {
        _mediator.FalhasAntesDoSucesso = 2;
        _mediator.Falha = new TimeoutException("timeout");

        await EnviarAsync(NovoGerarOrcamento());

        (await AguardarChamadasEstabilizaremAsync(3)).Should().Be(3);
        _mediator.Sucessos.Should().Be(1);
    }

    private sealed class MediatorProgramavel : IMediator
    {
        private int _chamadas;
        private int _sucessos;

        public Exception? Falha { get; set; }
        public int FalhasAntesDoSucesso { get; set; } = int.MaxValue;
        public int Chamadas => Volatile.Read(ref _chamadas);
        public int Sucessos => Volatile.Read(ref _sucessos);
        public ConcurrentBag<Type> TiposRecebidos { get; } = new();

        private Task Registrar(object request)
        {
            TiposRecebidos.Add(request.GetType());
            var numero = Interlocked.Increment(ref _chamadas);
            if (Falha is not null && numero <= FalhasAntesDoSucesso)
                return Task.FromException(Falha);

            Interlocked.Increment(ref _sucessos);
            return Task.CompletedTask;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Registrar(request!);

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            await Registrar(request);
            return default!;
        }

        public async Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            await Registrar(request);
            return null;
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
