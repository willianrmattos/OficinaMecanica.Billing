using MediatR;
using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Application.Mensagens;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;
using OficinaMecanica.Billing.Domain.Interfaces;
using OficinaMecanica.Contracts;

namespace OficinaMecanica.Billing.Application.Commands.ProcessarWebhookPagamento;

public class ProcessarWebhookPagamentoCommandHandler : IRequestHandler<ProcessarWebhookPagamentoCommand>
{
    private const string MotivoEstornoAutomatico = "Pagamento aprovado depois de cancelado ou expirado.";

    private readonly IAssinaturaWebhookValidator _assinaturaValidator;
    private readonly IWebhookRecebidoRepository _webhookRepository;
    private readonly IPagamentoRepository _pagamentoRepository;
    private readonly IOrcamentoRepository _orcamentoRepository;
    private readonly IPagamentoGateway _gateway;
    private readonly IIntegrationEventPublisher _publisher;
    private readonly IMetricasDoBilling _metricas;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProcessarWebhookPagamentoCommandHandler> _logger;

    public ProcessarWebhookPagamentoCommandHandler(
        IAssinaturaWebhookValidator assinaturaValidator,
        IWebhookRecebidoRepository webhookRepository,
        IPagamentoRepository pagamentoRepository,
        IOrcamentoRepository orcamentoRepository,
        IPagamentoGateway gateway,
        IIntegrationEventPublisher publisher,
        IMetricasDoBilling metricas,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<ProcessarWebhookPagamentoCommandHandler> logger)
    {
        _assinaturaValidator = assinaturaValidator;
        _webhookRepository = webhookRepository;
        _pagamentoRepository = pagamentoRepository;
        _orcamentoRepository = orcamentoRepository;
        _gateway = gateway;
        _publisher = publisher;
        _metricas = metricas;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task Handle(ProcessarWebhookPagamentoCommand request, CancellationToken cancellationToken)
    {
        if (!_assinaturaValidator.Validar(request.XSignature, request.XRequestId, request.DataId))
            throw new AssinaturaWebhookInvalidaException();

        var agora = _timeProvider.GetUtcNow();

        var recebido = await _webhookRepository.ObterPorRequestIdAsync(request.XRequestId, cancellationToken);
        if (recebido is { Processado: true })
        {
            _logger.LogInformation("Webhook {RequestId} já processado; ignorado.", request.XRequestId);
            return;
        }

        if (recebido is null)
        {
            recebido = new WebhookRecebido(request.XRequestId, request.DataId, agora);
            await _webhookRepository.AdicionarAsync(recebido, cancellationToken);
        }

        // O corpo do webhook nunca é confiável: o estado real do pagamento vem sempre da consulta ao gateway.
        // Optei por tratar a ausência do pagamento no gateway como falha transitória: logo após notificar, o
        // gateway pode ainda responder 404, e responder 200 aqui faria ele não reentregar nunca mais.
        var pagamentoGateway = await _gateway.ObterPagamentoAsync(request.DataId, cancellationToken)
            ?? throw new PagamentoDoGatewayIndisponivelException(request.DataId);

        var pagamento = await LocalizarPagamentoAsync(pagamentoGateway.ExternalReference, cancellationToken);
        if (pagamento is null)
        {
            _logger.LogWarning(
                "Webhook {RequestId}: o pagamento {GatewayPaymentId} do gateway não tem pagamento local (referência {ExternalReference}); ignorado.",
                request.XRequestId, pagamentoGateway.Id, pagamentoGateway.ExternalReference);
            await ConcluirAsync(recebido, cancellationToken);
            return;
        }

        pagamento.RegistrarTentativa(pagamentoGateway.Id, pagamentoGateway.Status, pagamentoGateway.StatusDetalhe, pagamentoGateway.Valor, agora);

        if (!TentativaPagamento.EhStatusAprovado(pagamentoGateway.Status))
        {
            await ConcluirAsync(recebido, cancellationToken);
            return;
        }

        var jaEraAprovacaoTardia = pagamento.AprovacaoTardia;

        ResultadoAprovacao resultado;
        try
        {
            resultado = pagamento.ConfirmarAprovacao(
                pagamentoGateway.Id, pagamentoGateway.Valor, pagamentoGateway.Moeda, pagamentoGateway.ExternalReference, agora);
        }
        catch (DomainException ex)
        {
            RegistrarDivergencia(pagamento, pagamentoGateway, ex);
            await ConcluirAsync(recebido, cancellationToken);
            return;
        }

        switch (resultado)
        {
            case ResultadoAprovacao.Aprovado:
                await ConfirmarPagamentoAsync(pagamento, recebido, agora, cancellationToken);
                break;

            case ResultadoAprovacao.AprovadoTardio:
                await EstornarAprovacaoTardiaAsync(pagamento, recebido, jaEraAprovacaoTardia, agora, cancellationToken);
                break;

            case ResultadoAprovacao.AprovacaoDuplicada:
                RegistrarAprovacaoDuplicada(pagamento, pagamentoGateway);
                await ConcluirAsync(recebido, cancellationToken);
                break;

            default:
                await ConcluirAsync(recebido, cancellationToken);
                break;
        }
    }

    private async Task<Pagamento?> LocalizarPagamentoAsync(string? externalReference, CancellationToken cancellationToken) =>
        Guid.TryParse(externalReference, out var pagamentoId)
            ? await _pagamentoRepository.ObterPorIdAsync(pagamentoId, cancellationToken)
            : null;

    private async Task ConfirmarPagamentoAsync(
        Pagamento pagamento,
        WebhookRecebido recebido,
        DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        var orcamento = await _orcamentoRepository.ObterPorIdAsync(pagamento.OrcamentoId, cancellationToken);
        if (orcamento is { Status: StatusOrcamento.Aprovado })
            orcamento.MarcarComoPago();

        await _publisher.PublishAsync(MensagensDeIntegracao.PagamentoAprovadoDe(pagamento, agora), cancellationToken);
        await ConcluirAsync(recebido, cancellationToken);
    }

    // Optei por mandar o estorno como comando para a fila do próprio Billing em vez de executá-lo aqui: a
    // aprovação tardia e o envio do comando entram no mesmo SaveChanges (outbox) e o gateway é chamado pelo
    // consumidor, que já tem retry. O envio se repete a cada reentrega enquanto o estorno não termina.
    private async Task EstornarAprovacaoTardiaAsync(
        Pagamento pagamento,
        WebhookRecebido recebido,
        bool jaEraAprovacaoTardia,
        DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        if (!jaEraAprovacaoTardia)
        {
            _logger.LogWarning("Pagamento {PagamentoId} aprovado no gateway depois de {Status}; estorno automático solicitado.", pagamento.Id, pagamento.Status);
            _metricas.RegistrarAprovacaoTardia();
        }

        await _publisher.SendAsync(
            Filas.Billing, MensagensDeIntegracao.EstornarPagamentoDe(pagamento, MotivoEstornoAutomatico, agora), cancellationToken);
        await ConcluirAsync(recebido, cancellationToken);
    }

    private void RegistrarDivergencia(Pagamento pagamento, PagamentoGatewayDto pagamentoGateway, DomainException ex)
    {
        _logger.LogError(
            ex,
            "Pagamento {PagamentoId} aprovado no gateway com dados divergentes; aprovação não confirmada. " +
            "Gateway: id {GatewayPaymentId}, valor {ValorGateway}, moeda {Moeda}, referência {ExternalReference}; esperado: valor {ValorEsperado}.",
            pagamento.Id, pagamentoGateway.Id, pagamentoGateway.Valor, pagamentoGateway.Moeda, pagamentoGateway.ExternalReference, pagamento.Valor);
        _metricas.RegistrarDivergenciaDePagamento();
    }

    private void RegistrarAprovacaoDuplicada(Pagamento pagamento, PagamentoGatewayDto pagamentoGateway)
    {
        _logger.LogWarning(
            "Pagamento {PagamentoId} já aprovado pelo pagamento {GatewayPaymentIdAprovado} do gateway; o pagamento {GatewayPaymentIdDuplicado} também foi aprovado e precisa de estorno manual.",
            pagamento.Id, pagamento.GatewayPaymentIdAprovado, pagamentoGateway.Id);
        _metricas.RegistrarAprovacaoDuplicada();
    }

    private async Task ConcluirAsync(WebhookRecebido recebido, CancellationToken cancellationToken)
    {
        recebido.MarcarComoProcessado();
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
