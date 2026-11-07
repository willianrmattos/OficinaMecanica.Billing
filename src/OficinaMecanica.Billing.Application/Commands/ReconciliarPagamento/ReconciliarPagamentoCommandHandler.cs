using MediatR;
using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Application.Mensagens;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Interfaces;
using OficinaMecanica.Contracts;

namespace OficinaMecanica.Billing.Application.Commands.ReconciliarPagamento;

public class ReconciliarPagamentoCommandHandler : IRequestHandler<ReconciliarPagamentoCommand>
{
    private const string MotivoReconciliacao = "Estorno pendente reenviado pela reconciliação.";

    private readonly IPagamentoRepository _pagamentoRepository;
    private readonly IPagamentoGateway _gateway;
    private readonly IIntegrationEventPublisher _publisher;
    private readonly IMetricasDoBilling _metricas;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReconciliarPagamentoCommandHandler> _logger;

    public ReconciliarPagamentoCommandHandler(
        IPagamentoRepository pagamentoRepository,
        IPagamentoGateway gateway,
        IIntegrationEventPublisher publisher,
        IMetricasDoBilling metricas,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<ReconciliarPagamentoCommandHandler> logger)
    {
        _pagamentoRepository = pagamentoRepository;
        _gateway = gateway;
        _publisher = publisher;
        _metricas = metricas;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task Handle(ReconciliarPagamentoCommand request, CancellationToken cancellationToken)
    {
        var pagamento = await _pagamentoRepository.ObterPorIdAsync(request.PagamentoId, cancellationToken);
        if (pagamento is null)
        {
            _logger.LogWarning("Reconciliação do pagamento {PagamentoId} inexistente; ignorada.", request.PagamentoId);
            return;
        }

        // Log e métrica só saem depois do SaveChanges: se o save falhar (conflito de concorrência) o próximo ciclo refaz tudo e
        // o aviso não sai duas vezes. Os efeitos externos (envio ao outbox, estorno no gateway) já são idempotentes.
        var abandonou = false;
        if (pagamento.EstornoPendente && !pagamento.EstornoAbandonado)
            abandonou = await ReenviarEstornoAsync(pagamento, request.MaximoDeTentativasDeEstorno, cancellationToken);

        var duplicadasEstornadas = await EstornarAprovacoesDuplicadasAsync(pagamento, cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        if (abandonou)
        {
            _logger.LogError(
                "O estorno do pagamento {PagamentoId} atingiu {Tentativas} tentativas sem sucesso; exige intervenção manual.",
                pagamento.Id, pagamento.Estorno?.Tentativas);
            _metricas.RegistrarEstornoAbandonado();
        }

        foreach (var gatewayPaymentId in duplicadasEstornadas)
        {
            _metricas.RegistrarPagamentoEstornado();
            _logger.LogInformation(
                "A cobrança em duplicidade {GatewayPaymentId} do pagamento {PagamentoId} foi estornada.",
                gatewayPaymentId, pagamento.Id);
        }
    }

    // Reenvio pelo mesmo comando contratual do estorno automático: o MessageId deterministico faz o inbox descartar
    // a repetição enquanto a tentativa anterior não falhou, e depois de uma falha o id muda e o consumidor retoma o
    // estorno (a chave refund-{PagamentoId} é idempotente no gateway). O teto de tentativas evita martelar um
    // gateway que recusa para sempre: ao atingi-lo o caso é marcado como abandonado e fica para o staff, e o aviso
    // (log de erro e métrica) sai uma única vez, porque dali em diante o pagamento nem entra mais na reconciliação.
    // Devolve verdadeiro só na transição para abandonado, para quem chama avisar depois de persistir.
    private async Task<bool> ReenviarEstornoAsync(Pagamento pagamento, int maximoDeTentativas, CancellationToken cancellationToken)
    {
        var agora = _timeProvider.GetUtcNow();
        var tentativas = pagamento.Estorno?.Tentativas ?? 0;
        if (tentativas >= maximoDeTentativas)
            return pagamento.AbandonarEstorno(agora);

        await _publisher.SendAsync(
            Filas.Billing,
            MensagensDeIntegracao.EstornarPagamentoDe(pagamento, MotivoReconciliacao, agora),
            cancellationToken);
        return false;
    }

    // A cobrança em duplicidade não passa pela saga (ela só conhece o pagamento adotado): o estorno é feito aqui
    // mesmo, com chave de idempotência própria por pagamento do gateway. Se o gateway falhar, a tentativa continua
    // sem estorno e o próximo ciclo da reconciliação tenta de novo. Devolve os pagamentos do gateway estornados.
    private async Task<IReadOnlyList<string>> EstornarAprovacoesDuplicadasAsync(Pagamento pagamento, CancellationToken cancellationToken)
    {
        var estornadas = new List<string>();

        foreach (var duplicada in pagamento.AprovacoesDuplicadasSemEstorno())
        {
            try
            {
                var refundId = await _gateway.EstornarAsync(
                    duplicada.GatewayPaymentId, $"refund-duplicidade-{duplicada.GatewayPaymentId}", cancellationToken);
                pagamento.RegistrarEstornoDeAprovacaoDuplicada(duplicada.GatewayPaymentId, refundId);
                estornadas.Add(duplicada.GatewayPaymentId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex, "Falha ao estornar a cobrança em duplicidade {GatewayPaymentId} do pagamento {PagamentoId}.",
                    duplicada.GatewayPaymentId, pagamento.Id);
            }
        }

        return estornadas;
    }
}
