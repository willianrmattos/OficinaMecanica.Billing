using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Interfaces;
using OficinaMecanica.Billing.Infrastructure.Data;

namespace OficinaMecanica.Billing.Infrastructure.Repositories;

public class PagamentoRepository : IPagamentoRepository
{
    private readonly BillingDbContext _context;

    public PagamentoRepository(BillingDbContext context)
    {
        _context = context;
    }

    public Task<Pagamento?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        ComFilhos().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<Pagamento?> ObterPorOrdemDeServicoIdAsync(Guid ordemDeServicoId, CancellationToken cancellationToken = default) =>
        ComFilhos().FirstOrDefaultAsync(p => p.OrdemDeServicoId == ordemDeServicoId, cancellationToken);

    // Espelha em SQL Pagamento.EstornoPendente (sem o que já foi abandonado) e Pagamento.AprovacoesDuplicadasSemEstorno,
    // porque o EF não traduz propriedades calculadas do domínio; um teste de infraestrutura confere que as versões
    // concordam. O estorno abandonado sai da lista (o staff cuida dele) mas a cobrança em duplicidade do mesmo pagamento
    // continua nela. A ordem por tentativas de estorno põe por último os casos que já falharam muitas vezes, para um
    // conjunto grande deles não ocupar o lote inteiro e esconder os pagamentos que ainda têm chance de ser reconciliados.
    public async Task<IReadOnlyList<Guid>> ListarIdsParaReconciliacaoAsync(int limite, CancellationToken cancellationToken = default)
    {
        const string statusAprovado = TentativaPagamento.StatusGatewayAprovado;

        return await _context.Pagamentos
            .AsNoTracking()
            .Where(p =>
                ((p.Status == StatusPagamento.Estornando
                        || p.Status == StatusPagamento.EstornoFalhou
                        || (p.AprovacaoTardia && (p.Status == StatusPagamento.Cancelado || p.Status == StatusPagamento.Expirado)))
                    && (p.Estorno == null || p.Estorno.AbandonadoEm == null))
                || (p.GatewayPaymentIdAprovado != null
                    && p.Tentativas.Any(t =>
                        t.StatusGateway == statusAprovado
                        && t.GatewayRefundId == null
                        && t.GatewayPaymentId != p.GatewayPaymentIdAprovado)))
            .OrderBy(p => p.Estorno == null ? 0 : p.Estorno.Tentativas)
            .ThenBy(p => p.CriadoEm)
            .Select(p => p.Id)
            .Take(limite)
            .ToListAsync(cancellationToken);
    }

    public async Task AdicionarAsync(Pagamento pagamento, CancellationToken cancellationToken = default) =>
        await _context.Pagamentos.AddAsync(pagamento, cancellationToken);

    private IQueryable<Pagamento> ComFilhos() =>
        _context.Pagamentos.Include(p => p.Tentativas).Include(p => p.Estorno);
}
