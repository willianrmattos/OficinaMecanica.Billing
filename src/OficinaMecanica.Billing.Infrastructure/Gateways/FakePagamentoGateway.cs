using System.Collections.Concurrent;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.Interfaces;

namespace OficinaMecanica.Billing.Infrastructure.Gateways;

// Gateway em memória para dev e testes: determinístico (ids sequenciais, preferência derivada do pagamento) e com
// o estado dos pagamentos manipulável pelo endpoint de dev e pelos testes. Não persiste nada: reiniciar o processo
// zera o gateway, como acontece com uma conta de teste nova.
public sealed class FakePagamentoGateway : IPagamentoGateway
{
    public const string StatusAprovado = "approved";
    public const string StatusRecusado = "rejected";
    public const string StatusReembolsado = "refunded";
    public const long PrimeiroIdDePagamento = 100_000_000_001;

    private readonly ConcurrentDictionary<string, PreferenciaFake> _preferencias = new();
    private readonly ConcurrentDictionary<string, PagamentoGatewayDto> _pagamentos = new();
    private readonly ConcurrentDictionary<string, string> _estornosPorChave = new();
    private long _ultimoIdDePagamento = PrimeiroIdDePagamento - 1;
    private long _ultimoIdDeEstorno;

    // Quando verdadeiro, ObterPagamentoAsync responde como um gateway que ainda não conhece o pagamento (404).
    public bool PagamentosIndisponiveis { get; set; }

    // Quando preenchida, EstornarAsync lança esta exceção (indisponibilidade ou recusa do gateway).
    public Exception? FalhaNoEstorno { get; set; }

    public Task<PreferenciaGatewayDto> CriarPreferenciaAsync(CriarPreferenciaGatewayRequest request, CancellationToken cancellationToken = default)
    {
        var preferenceId = $"fake-pref-{request.PagamentoId:N}";
        _preferencias.TryAdd(preferenceId, new PreferenciaFake(request.PagamentoId, request.Valor));

        return Task.FromResult(new PreferenciaGatewayDto(preferenceId, $"https://fake.mercadopago.local/checkout/{preferenceId}"));
    }

    public Task<PagamentoGatewayDto?> ObterPagamentoAsync(string gatewayPaymentId, CancellationToken cancellationToken = default)
    {
        if (PagamentosIndisponiveis)
            return Task.FromResult<PagamentoGatewayDto?>(null);

        return Task.FromResult(_pagamentos.TryGetValue(gatewayPaymentId, out var pagamento) ? pagamento : null);
    }

    public Task<string> EstornarAsync(string gatewayPaymentId, string chaveDeIdempotencia, CancellationToken cancellationToken = default)
    {
        if (FalhaNoEstorno is not null)
            throw FalhaNoEstorno;

        if (_estornosPorChave.TryGetValue(chaveDeIdempotencia, out var estornoExistente))
            return Task.FromResult(estornoExistente);

        if (!_pagamentos.TryGetValue(gatewayPaymentId, out var pagamento) || pagamento.Status != StatusAprovado)
            throw new PagamentoRecusadoPeloGatewayException("O pagamento não existe ou não está aprovado no gateway.");

        var refundId = $"fake-refund-{Interlocked.Increment(ref _ultimoIdDeEstorno)}";
        _pagamentos[gatewayPaymentId] = pagamento with { Status = StatusReembolsado };
        _estornosPorChave[chaveDeIdempotencia] = refundId;
        return Task.FromResult(refundId);
    }

    public PagamentoGatewayDto AprovarPreferencia(string preferenceId) =>
        RegistrarPagamento(preferenceId, StatusAprovado, "accredited");

    public PagamentoGatewayDto RecusarPreferencia(string preferenceId) =>
        RegistrarPagamento(preferenceId, StatusRecusado, "cc_rejected_other_reason");

    // Cria um novo pagamento do gateway para a preferência (uma preferência aceita várias tentativas, cada uma com
    // payment.id próprio). Valor e referência saem da preferência, mas o valor pode ser forçado para simular divergência.
    public PagamentoGatewayDto RegistrarPagamento(string preferenceId, string status, string? statusDetalhe, decimal? valor = null)
    {
        if (!_preferencias.TryGetValue(preferenceId, out var preferencia))
            throw new KeyNotFoundException($"Preferência {preferenceId} não encontrada no gateway fake.");

        var id = Interlocked.Increment(ref _ultimoIdDePagamento).ToString();
        var pagamento = new PagamentoGatewayDto(
            id, status, statusDetalhe, valor ?? preferencia.Valor, "BRL", preferencia.PagamentoId.ToString());
        _pagamentos[id] = pagamento;
        return pagamento;
    }

    public void Reiniciar()
    {
        _preferencias.Clear();
        _pagamentos.Clear();
        _estornosPorChave.Clear();
        PagamentosIndisponiveis = false;
        FalhaNoEstorno = null;
    }

    private sealed record PreferenciaFake(Guid PagamentoId, decimal Valor);
}
