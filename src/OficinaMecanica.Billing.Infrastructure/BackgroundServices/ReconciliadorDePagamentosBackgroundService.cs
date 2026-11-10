using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Application.Commands.ReconciliarPagamento;
using OficinaMecanica.Billing.Application.Queries.ListarPagamentosParaReconciliacao;
using OficinaMecanica.Billing.Infrastructure.Options;

namespace OficinaMecanica.Billing.Infrastructure.BackgroundServices;

// Revisita periodicamente os pagamentos cujo dinheiro ainda precisa voltar (estorno parado, falho ou de aprovação
// tardia sem estorno, e cobrança em duplicidade) e reenvia o estorno. É seguro com mais de uma réplica: o comando
// reenviado tem MessageId determinístico (o inbox descarta a repetição) e o estorno no gateway é idempotente.
public sealed class ReconciliadorDePagamentosBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _escopos;
    private readonly OpcoesDoBilling _opcoes;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReconciliadorDePagamentosBackgroundService> _logger;

    public ReconciliadorDePagamentosBackgroundService(
        IServiceScopeFactory escopos,
        IOptions<OpcoesDoBilling> opcoes,
        TimeProvider timeProvider,
        ILogger<ReconciliadorDePagamentosBackgroundService> logger)
    {
        _escopos = escopos;
        _opcoes = opcoes.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opcoes.ReconciliadorHabilitado)
        {
            _logger.LogInformation("Reconciliador de pagamentos desabilitado (Billing:ReconciliadorHabilitado=false).");
            return;
        }

        using var temporizador = new PeriodicTimer(TimeSpan.FromSeconds(_opcoes.ReconciliadorIntervaloSegundos), _timeProvider);

        try
        {
            do
            {
                await ExecutarCicloAsync(stoppingToken);
            }
            while (await temporizador.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Parada normal do host.
        }
    }

    // Um ciclo nunca derruba o serviço: falha de banco ou do gateway vira log e o próximo ciclo tenta de novo.
    // Cada pagamento é reconciliado em um escopo próprio, para uma falha em um não contaminar o rastreamento
    // do DbContext nem impedir os demais.
    internal async Task<int> ExecutarCicloAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> ids;
        try
        {
            using var escopoDaListagem = _escopos.CreateScope();
            ids = await escopoDaListagem.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new ListarPagamentosParaReconciliacaoQuery(_opcoes.ReconciliadorTamanhoDoLote), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Falha ao listar os pagamentos para reconciliação.");
            return 0;
        }

        var reconciliados = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var escopo = _escopos.CreateScope();
                await escopo.ServiceProvider.GetRequiredService<IMediator>()
                    .Send(new ReconciliarPagamentoCommand(id, _opcoes.ReconciliadorMaximoDeTentativasDeEstorno), cancellationToken);
                reconciliados++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Falha ao reconciliar o pagamento {PagamentoId}.", id);
            }
        }

        if (ids.Count > 0)
            _logger.LogInformation("Reconciliação: {Reconciliados} de {Total} pagamentos revisados.", reconciliados, ids.Count);

        return reconciliados;
    }
}
