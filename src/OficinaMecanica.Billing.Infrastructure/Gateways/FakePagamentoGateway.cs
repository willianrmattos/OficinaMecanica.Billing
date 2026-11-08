using System.Collections.Concurrent;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.Interfaces;

namespace OficinaMecanica.Billing.Infrastructure.Gateways;

// Gateway em memória para dev e testes: determinístico (ids crescentes, preferência derivada do pagamento) e com
// o estado dos pagamentos manipulável pelo endpoint de dev e pelos testes. Preferências, pagamentos e estornos não
// persistem: reiniciar o processo zera o gateway, como acontece com uma conta de teste nova. Os ids de pagamento são
// a exceção: o banco do Billing sobrevive ao processo e guarda o id do gateway com índice único
// (TentativasPagamento.GatewayPaymentId), então um contador que recomeça do mesmo valor a cada partida colidiria com
// as tentativas já gravadas e o endpoint de dev responderia 409.
public sealed class FakePagamentoGateway : IPagamentoGateway
{
    public const string StatusAprovado = "approved";
    public const string StatusRecusado = "rejected";
    public const string StatusReembolsado = "refunded";

    // O primeiro id de cada gateway é o instante da criação em milissegundos vezes mil, e cada pagamento soma um. Fica
    // numérico e longo como os do Mercado Pago (16 dígitos hoje; cabe em long e nos 64 caracteres da coluna), e a
    // partida seguinte começa acima de tudo que o processo anterior gerou, a menos que ele tenha passado de mil ids
    // por milissegundo de vida.
    private const long IdsPorMilissegundo = 1_000;

    // Dois gateways do mesmo processo criados no mesmo milissegundo (ou com relógios iguais) não podem repartir a
    // semente do relógio; por isso cada um reserva uma faixa e o próximo começa depois dela, como um gerador de ULID
    // monotônico. A faixa equivale a um segundo de relógio.
    private const long IdsPorFaixa = 1_000 * IdsPorMilissegundo;
    private static long s_proximoIdLivreDoProcesso;

    private readonly ConcurrentDictionary<string, PreferenciaFake> _preferencias = new();
    private readonly ConcurrentDictionary<string, PagamentoGatewayDto> _pagamentos = new();
    private readonly ConcurrentDictionary<string, string> _estornosPorChave = new();
    private long _ultimoIdDePagamento;
    private long _ultimoIdDeEstorno;

    public FakePagamentoGateway(TimeProvider relogio)
        : this(ReservarFaixa(relogio.GetUtcNow().ToUnixTimeMilliseconds() * IdsPorMilissegundo))
    {
    }

    // Semente explícita, sem relógio nem reserva: os testes que conferem ids exatos precisam de um ponto de partida fixo.
    internal FakePagamentoGateway(long primeiroIdDePagamento)
    {
        _ultimoIdDePagamento = primeiroIdDePagamento - 1;
    }

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

    // Zera o estado manipulável, mas não o contador de ids: um id já entregue não volta a ser gerado.
    public void Reiniciar()
    {
        _preferencias.Clear();
        _pagamentos.Clear();
        _estornosPorChave.Clear();
        PagamentosIndisponiveis = false;
        FalhaNoEstorno = null;
    }

    private static long ReservarFaixa(long inicioDesejado)
    {
        while (true)
        {
            var livre = Volatile.Read(ref s_proximoIdLivreDoProcesso);
            var inicio = Math.Max(inicioDesejado, livre);
            if (Interlocked.CompareExchange(ref s_proximoIdLivreDoProcesso, inicio + IdsPorFaixa, livre) == livre)
                return inicio;
        }
    }

    private sealed record PreferenciaFake(Guid PagamentoId, decimal Valor);
}
