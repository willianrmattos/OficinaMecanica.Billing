using MediatR;
using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.Application.Common;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Application.Mensagens;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;
using OficinaMecanica.Billing.Domain.Interfaces;

namespace OficinaMecanica.Billing.Application.Commands.EstornarPagamento;

public class EstornarPagamentoCommandHandler : IRequestHandler<EstornarPagamentoCommand>
{
    private const string ErroPagamentoNaoEncontrado = "Pagamento não encontrado.";
    private const string ErroPagamentoNaoEstornavel = "O pagamento não está em um estado que permita o estorno.";
    private const string ErroGatewayRecusou = "O gateway recusou o estorno.";
    private const string ErroGatewayIndisponivel = "Falha de comunicação com o gateway.";

    private readonly IPagamentoRepository _pagamentoRepository;
    private readonly IPagamentoGateway _gateway;
    private readonly IIntegrationEventPublisher _publisher;
    private readonly IMetricasDoBilling _metricas;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EstornarPagamentoCommandHandler> _logger;

    public EstornarPagamentoCommandHandler(
        IPagamentoRepository pagamentoRepository,
        IPagamentoGateway gateway,
        IIntegrationEventPublisher publisher,
        IMetricasDoBilling metricas,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<EstornarPagamentoCommandHandler> logger)
    {
        _pagamentoRepository = pagamentoRepository;
        _gateway = gateway;
        _publisher = publisher;
        _metricas = metricas;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task Handle(EstornarPagamentoCommand request, CancellationToken cancellationToken)
    {
        var agora = _timeProvider.GetUtcNow();
        var motivo = NormalizadorDeMotivo.Normalizar(request.Motivo);

        var pagamento = await _pagamentoRepository.ObterPorIdAsync(request.PagamentoId, cancellationToken);
        if (pagamento is null)
        {
            _logger.LogWarning("Estorno solicitado para o pagamento {PagamentoId} inexistente. Motivo: {Motivo}", request.PagamentoId, motivo);
            await PublicarEstornoImpossivelAsync(request, ErroPagamentoNaoEncontrado, agora, cancellationToken);
            return;
        }

        if (pagamento.Status == StatusPagamento.Estornado)
        {
            await _publisher.PublishAsync(MensagensDeIntegracao.PagamentoEstornadoDe(pagamento, agora), cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }

        string gatewayPaymentId;
        try
        {
            gatewayPaymentId = pagamento.IniciarEstorno();
        }
        catch (DomainException ex)
        {
            _logger.LogWarning(ex, "Estorno do pagamento {PagamentoId} não é possível. Motivo: {Motivo}", pagamento.Id, motivo);
            await PublicarEstornoImpossivelAsync(request, ErroPagamentoNaoEstornavel, agora, cancellationToken);
            return;
        }

        // Persisto a intenção de estornar antes de chamar o gateway. Fora de um consumer esse estado sobrevive a uma
        // queda e a reentrega retoma dali; dentro da transação do MassTransit este Save não é commit, e a segurança
        // vem da idempotência: a chave refund-{PagamentoId} faz o gateway devolver o mesmo estorno.
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        string gatewayRefundId;
        try
        {
            gatewayRefundId = await _gateway.EstornarAsync(gatewayPaymentId, $"refund-{pagamento.Id}", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Só um texto fixo vai para o Estorno e para o EstornoFalhou: a mensagem da exceção do SDK pode
            // embutir o corpo da resposta do gateway, com dados do pagador. A exceção completa fica no log.
            var erro = ex is PagamentoRecusadoPeloGatewayException ? ErroGatewayRecusou : ErroGatewayIndisponivel;
            _logger.LogError(ex, "Falha ao estornar o pagamento {PagamentoId} no gateway. Motivo: {Motivo}", pagamento.Id, motivo);
            pagamento.FalharEstorno(erro);
            await _publisher.PublishAsync(MensagensDeIntegracao.EstornoFalhouDe(pagamento, erro, agora), cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }

        pagamento.ConfirmarEstorno(gatewayRefundId);
        await _publisher.PublishAsync(MensagensDeIntegracao.PagamentoEstornadoDe(pagamento, agora), cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _metricas.RegistrarPagamentoEstornado();
    }

    // Um estorno que não pode acontecer ainda precisa de resposta: sem ela a saga ficaria esperando em Estornando.
    private async Task PublicarEstornoImpossivelAsync(EstornarPagamentoCommand request, string erro, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        await _publisher.PublishAsync(
            MensagensDeIntegracao.EstornoImpossivelDe(request.OrdemDeServicoId, request.PagamentoId, erro, agora), cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
