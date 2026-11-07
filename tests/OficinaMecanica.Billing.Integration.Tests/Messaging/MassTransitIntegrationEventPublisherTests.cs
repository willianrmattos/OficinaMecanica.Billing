using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Infrastructure.Messaging;
using OficinaMecanica.Contracts;
using OficinaMecanica.Contracts.Commands;
using OficinaMecanica.Contracts.Events;

namespace OficinaMecanica.Billing.Integration.Tests.Messaging;

public class MassTransitIntegrationEventPublisherTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private ServiceProvider _provedor = null!;
    private ITestHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _provedor = new ServiceCollection()
            .AddMassTransitTestHarness()
            .AddScoped<IIntegrationEventPublisher, MassTransitIntegrationEventPublisher>()
            .BuildServiceProvider(validateScopes: true);
        _harness = _provedor.GetRequiredService<ITestHarness>();
        await _harness.Start();
    }

    public async Task DisposeAsync()
    {
        await _provedor.DisposeAsync();
    }

    private IIntegrationEventPublisher Publisher(IServiceScope escopo) =>
        escopo.ServiceProvider.GetRequiredService<IIntegrationEventPublisher>();

    [Fact]
    public async Task PublishAsync_DeveCopiarOMessageIdDoContratoParaOEnvelope()
    {
        var evento = new OrcamentoGerado(Guid.NewGuid(), Guid.NewGuid(), Agora, Guid.NewGuid(), 192.30m, Agora.AddDays(3));
        using var escopo = _provedor.CreateScope();

        await Publisher(escopo).PublishAsync(evento);

        var publicada = (await _harness.Published.SelectAsync<OrcamentoGerado>().First()).Context;
        publicada.MessageId.Should().Be(evento.MessageId);
        publicada.Message.Should().Be(evento);
    }

    [Fact]
    public async Task SendAsync_DeveEnviarParaAFilaIndicadaCopiandoOMessageIdDoContrato()
    {
        var comando = new EstornarPagamento(Guid.NewGuid(), Guid.NewGuid(), Agora, Guid.NewGuid(), "Pagamento tardio");
        using var escopo = _provedor.CreateScope();

        await Publisher(escopo).SendAsync(Filas.Billing, comando);

        var enviada = (await _harness.Sent.SelectAsync<EstornarPagamento>().First()).Context;
        enviada.MessageId.Should().Be(comando.MessageId);
        enviada.DestinationAddress!.AbsolutePath.Should().EndWith(Filas.Billing);
        enviada.Message.Should().Be(comando);
    }

    [Fact]
    public async Task PublishAsync_ContratoComMessageIdVazio_DeveDeixarOMassTransitGerarOIdDoEnvelope()
    {
        var evento = new PagamentoCancelado(Guid.Empty, Guid.NewGuid(), Agora, null);
        using var escopo = _provedor.CreateScope();

        await Publisher(escopo).PublishAsync(evento);

        var id = (await _harness.Published.SelectAsync<PagamentoCancelado>().First()).Context.MessageId;
        id.Should().NotBeNull().And.NotBe(Guid.Empty);
    }

    [Fact]
    public async Task PublishAsync_TipoSemMessageIdGuid_DeveDeixarOMassTransitGerarOIdDoEnvelope()
    {
        using var escopo = _provedor.CreateScope();

        await Publisher(escopo).PublishAsync(new MensagemSemIdentificador("texto"));
        await Publisher(escopo).PublishAsync(new MensagemComIdentificadorEmTexto("abc"));

        (await _harness.Published.SelectAsync<MensagemSemIdentificador>().First()).Context.MessageId.Should().NotBeNull();
        var comTexto = (await _harness.Published.SelectAsync<MensagemComIdentificadorEmTexto>().First()).Context;
        comTexto.MessageId.Should().NotBeNull().And.NotBe(Guid.Empty);
    }

    [Fact]
    public async Task SendAsync_ContratoSemMessageIdUtilizavel_DeveEnviarComIdGeradoPeloMassTransit()
    {
        using var escopo = _provedor.CreateScope();

        await Publisher(escopo).SendAsync(Filas.Billing, new MensagemSemIdentificador("comando"));

        (await _harness.Sent.SelectAsync<MensagemSemIdentificador>().First()).Context.MessageId.Should().NotBeNull();
    }

    public sealed record MensagemSemIdentificador(string Texto);

    public sealed record MensagemComIdentificadorEmTexto(string MessageId);
}
