using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Infrastructure.Data;
using OficinaMecanica.Billing.Infrastructure.Gateways;

namespace OficinaMecanica.Billing.Integration.Tests.Infra;

// Decora o gateway Fake para os testes provocarem falhas técnicas e observarem em que contexto o gateway foi chamado:
// dentro de um consumer (transação do MassTransit aberta) ou fora dele.
public sealed class ComportamentoDoGateway
{
    private int _falhasRestantes;
    private int _chamadasDeCriacao;

    public int FalhasDeCriacaoAntesDoSucesso { set => _falhasRestantes = value; }
    public bool SempreFalhaNaCriacao { get; set; }
    public int ChamadasDeCriacao => Volatile.Read(ref _chamadasDeCriacao);
    public List<bool> RetryDoEfObservadoNasChamadas { get; } = new();

    public bool ConsumirFalha()
    {
        Interlocked.Increment(ref _chamadasDeCriacao);
        if (SempreFalhaNaCriacao)
            return true;

        return Interlocked.Decrement(ref _falhasRestantes) >= 0;
    }
}

public sealed class GatewayDeTeste : IPagamentoGateway
{
    private readonly FakePagamentoGateway _interno;
    private readonly ComportamentoDoGateway _comportamento;
    private readonly IServiceProvider _escopo;

    public GatewayDeTeste(FakePagamentoGateway interno, ComportamentoDoGateway comportamento, IServiceProvider escopo)
    {
        _interno = interno;
        _comportamento = comportamento;
        _escopo = escopo;
    }

    public Task<PreferenciaGatewayDto> CriarPreferenciaAsync(CriarPreferenciaGatewayRequest request, CancellationToken cancellationToken = default)
    {
        var db = _escopo.GetRequiredService<BillingDbContext>();
        lock (_comportamento.RetryDoEfObservadoNasChamadas)
            _comportamento.RetryDoEfObservadoNasChamadas.Add(db.Database.CreateExecutionStrategy().RetriesOnFailure);

        if (_comportamento.ConsumirFalha())
            throw new HttpRequestException("O gateway não respondeu (falha técnica simulada).");

        return _interno.CriarPreferenciaAsync(request, cancellationToken);
    }

    public Task<PagamentoGatewayDto?> ObterPagamentoAsync(string gatewayPaymentId, CancellationToken cancellationToken = default) =>
        _interno.ObterPagamentoAsync(gatewayPaymentId, cancellationToken);

    public Task<string> EstornarAsync(string gatewayPaymentId, string chaveDeIdempotencia, CancellationToken cancellationToken = default) =>
        _interno.EstornarAsync(gatewayPaymentId, chaveDeIdempotencia, cancellationToken);
}
