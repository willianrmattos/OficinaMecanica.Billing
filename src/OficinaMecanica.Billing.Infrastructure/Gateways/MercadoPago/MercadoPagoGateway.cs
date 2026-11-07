using System.Globalization;
using MercadoPago.Client;
using MercadoPago.Client.Payment;
using MercadoPago.Client.Preference;
using MercadoPago.Error;
using MercadoPago.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Infrastructure.Options;

namespace OficinaMecanica.Billing.Infrastructure.Gateways.MercadoPago;

// Adaptador do Mercado Pago (Checkout Pro) atrás de IPagamentoGateway. O token vai por requisição (RequestOptions) e o
// HttpClient é injetado nos clientes do SDK, para não depender do estado estático global do MercadoPagoConfig.
public sealed class MercadoPagoGateway : IPagamentoGateway
{
    private const string CaminhoDoWebhook = "/webhooks/mercadopago";
    private const string TipoDePagamentoBoleto = "ticket";
    private const string Moeda = "BRL";
    private const string CabecalhoDeIdempotencia = "X-Idempotency-Key";
    private const int TamanhoMaximoDoTitulo = 250;

    private readonly PreferenceClient _preferencias;
    private readonly PaymentClient _pagamentos;
    private readonly OpcoesDoMercadoPago _opcoes;
    private readonly ILogger<MercadoPagoGateway> _logger;

    public MercadoPagoGateway(HttpClient httpClient, IOptions<OpcoesDoMercadoPago> opcoes, ILogger<MercadoPagoGateway> logger)
    {
        var cliente = new DefaultHttpClient(httpClient);
        _preferencias = new PreferenceClient(cliente);
        _pagamentos = new PaymentClient(cliente);
        _opcoes = opcoes.Value;
        _logger = logger;
    }

    public async Task<PreferenciaGatewayDto> CriarPreferenciaAsync(CriarPreferenciaGatewayRequest request, CancellationToken cancellationToken = default)
    {
        var preferencia = new PreferenceRequest
        {
            Items = new List<PreferenceItemRequest>
            {
                new()
                {
                    Id = request.PagamentoId.ToString(),
                    Title = Truncar(request.Descricao, TamanhoMaximoDoTitulo),
                    Quantity = 1,
                    CurrencyId = Moeda,
                    UnitPrice = Normalizar(request.Valor)
                }
            },
            ExternalReference = request.PagamentoId.ToString(),
            NotificationUrl = $"{_opcoes.NotificationBaseUrl!.TrimEnd('/')}{CaminhoDoWebhook}",
            Expires = true,
            ExpirationDateTo = request.ExpiraEm.UtcDateTime,
            // Boleto compensa em dias e não cabe no prazo curto de pagamento da saga.
            PaymentMethods = new PreferencePaymentMethodsRequest
            {
                ExcludedPaymentTypes = new List<PreferencePaymentTypeRequest> { new() { Id = TipoDePagamentoBoleto } }
            }
        };

        AplicarBackUrls(preferencia);

        try
        {
            var criada = await _preferencias.CreateAsync(
                preferencia, Opcoes($"preference-{request.PagamentoId}"), cancellationToken);

            return new PreferenciaGatewayDto(criada.Id, criada.InitPoint);
        }
        catch (MercadoPagoApiException ex) when (ex.StatusCode is { } status && EhRecusaDefinitiva(status))
        {
            _logger.LogError(ex, "O Mercado Pago recusou criar a preferência do pagamento {PagamentoId} (HTTP {Status}).", request.PagamentoId, ex.StatusCode);
            throw new PagamentoRecusadoPeloGatewayException("O gateway recusou a criação da preferência de pagamento.", ex);
        }
    }

    public async Task<PagamentoGatewayDto?> ObterPagamentoAsync(string gatewayPaymentId, CancellationToken cancellationToken = default)
    {
        // Um identificador que nem é numérico não existe no Mercado Pago: mesmo significado de um 404.
        if (!long.TryParse(gatewayPaymentId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return null;

        try
        {
            var pagamento = await _pagamentos.GetAsync(id, Opcoes(idempotencia: null), cancellationToken);

            return new PagamentoGatewayDto(
                pagamento.Id?.ToString(CultureInfo.InvariantCulture) ?? gatewayPaymentId,
                pagamento.Status ?? string.Empty,
                pagamento.StatusDetail,
                Normalizar(pagamento.TransactionAmount ?? 0m),
                pagamento.CurrencyId ?? string.Empty,
                pagamento.ExternalReference);
        }
        catch (MercadoPagoApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    public async Task<string> EstornarAsync(string gatewayPaymentId, string chaveDeIdempotencia, CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(gatewayPaymentId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            throw new PagamentoRecusadoPeloGatewayException("O identificador do pagamento no gateway é inválido para estorno.");

        try
        {
            // Sem valor, o SDK pede o estorno total (POST /v1/payments/{id}/refunds sem corpo).
            var estorno = await _pagamentos.RefundAsync(id, Opcoes(chaveDeIdempotencia), cancellationToken);
            return estorno.Id?.ToString(CultureInfo.InvariantCulture)
                ?? throw new InvalidOperationException("O Mercado Pago confirmou o estorno sem devolver o identificador.");
        }
        catch (MercadoPagoApiException ex) when (ex.StatusCode is { } status && EhRecusaDefinitiva(status))
        {
            _logger.LogWarning(ex, "O Mercado Pago recusou o estorno do pagamento {GatewayPaymentId} (HTTP {Status}).", gatewayPaymentId, ex.StatusCode);
            throw new PagamentoRecusadoPeloGatewayException("O gateway recusou o estorno.", ex);
        }
    }

    // 4xx é recusa definitiva (retentar não muda a resposta), exceto o que é transitório (timeout, conflito de
    // idempotência em andamento, recurso bloqueado, limite de taxa) e o que é problema de credencial: 401/403
    // significam token errado, e transformar isso em "pagamento recusado" cancelaria a ordem do cliente por um
    // erro de configuração nosso. Esses seguem como falha técnica, com retry e alerta.
    internal static bool EhRecusaDefinitiva(int statusCode) =>
        statusCode is >= 400 and < 500 and not (401 or 403 or 408 or 409 or 423 or 425 or 429);

    private void AplicarBackUrls(PreferenceRequest preferencia)
    {
        if (string.IsNullOrWhiteSpace(_opcoes.BackUrlSucesso)
            && string.IsNullOrWhiteSpace(_opcoes.BackUrlPendente)
            && string.IsNullOrWhiteSpace(_opcoes.BackUrlFalha))
            return;

        preferencia.BackUrls = new PreferenceBackUrlsRequest
        {
            Success = _opcoes.BackUrlSucesso,
            Pending = _opcoes.BackUrlPendente,
            Failure = _opcoes.BackUrlFalha
        };

        // auto_return exige back_urls.success; sem ele o Mercado Pago rejeita a preferência.
        if (!string.IsNullOrWhiteSpace(_opcoes.BackUrlSucesso))
            preferencia.AutoReturn = "approved";
    }

    private RequestOptions Opcoes(string? idempotencia)
    {
        var opcoes = new RequestOptions
        {
            AccessToken = _opcoes.AccessToken,
            RetryStrategy = SemNovasTentativas.Instancia
        };

        if (idempotencia is not null)
            opcoes.CustomHeaders[CabecalhoDeIdempotencia] = idempotencia;

        return opcoes;
    }

    // O domínio compara valores com igualdade exata, então o que o gateway devolve (e o que enviamos) vai com 2 casas.
    private static decimal Normalizar(decimal valor) => decimal.Round(valor, 2, MidpointRounding.AwayFromZero);

    private static string Truncar(string texto, int tamanho) => texto.Length <= tamanho ? texto : texto[..tamanho];
}
