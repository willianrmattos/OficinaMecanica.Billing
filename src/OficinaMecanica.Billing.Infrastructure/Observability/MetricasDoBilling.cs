using System.Diagnostics.Metrics;
using OficinaMecanica.Billing.Application.Interfaces;

namespace OficinaMecanica.Billing.Infrastructure.Observability;

// O nome do Meter é registrado com AddMeter no Program.cs: sem esse registro no MeterProvider os instrumentos
// são criados normalmente mas não saem via OTLP.
public sealed class MetricasDoBilling : IMetricasDoBilling, IDisposable
{
    public const string NomeDoMeter = "OficinaMecanica.Billing";

    private readonly Meter _meter = new(NomeDoMeter);
    private readonly Counter<long> _divergencias;
    private readonly Counter<long> _aprovacoesTardias;
    private readonly Counter<long> _aprovacoesDuplicadas;
    private readonly Counter<long> _orcamentosGerados;
    private readonly Counter<long> _pagamentosAprovados;
    private readonly Counter<long> _pagamentosRecusados;
    private readonly Counter<long> _pagamentosRecusadosPeloGateway;
    private readonly Counter<long> _pagamentosEstornados;
    private readonly Counter<long> _estornosAbandonados;

    public MetricasDoBilling()
    {
        _divergencias = _meter.CreateCounter<long>(
            "divergencias_de_pagamento_total", description: "Pagamentos aprovados no gateway com valor, moeda ou referência divergentes.");
        _aprovacoesTardias = _meter.CreateCounter<long>(
            "aprovacoes_tardias_total", description: "Pagamentos aprovados no gateway depois de cancelados ou expirados.");
        _aprovacoesDuplicadas = _meter.CreateCounter<long>(
            "aprovacoes_duplicadas_total", description: "Segundos pagamentos aprovados no gateway para o mesmo pagamento.");
        _orcamentosGerados = _meter.CreateCounter<long>(
            "orcamentos_gerados_total", description: "Orçamentos gerados.");
        _pagamentosAprovados = _meter.CreateCounter<long>(
            "pagamentos_aprovados_total", description: "Pagamentos aprovados.");
        _pagamentosRecusados = _meter.CreateCounter<long>(
            "pagamentos_recusados_total", description: "Tentativas de pagamento recusadas pelo gateway.");
        _pagamentosRecusadosPeloGateway = _meter.CreateCounter<long>(
            "pagamentos_recusados_pelo_gateway_total",
            description: "Solicitações de pagamento canceladas porque o gateway recusou criar a preferência (4xx definitivo).");
        _pagamentosEstornados = _meter.CreateCounter<long>(
            "pagamentos_estornados_total", description: "Estornos concluídos no gateway.");
        _estornosAbandonados = _meter.CreateCounter<long>(
            "estornos_abandonados_total", description: "Estornos que esgotaram as tentativas automáticas e dependem do staff.");
    }

    public void RegistrarDivergenciaDePagamento() => _divergencias.Add(1);
    public void RegistrarAprovacaoTardia() => _aprovacoesTardias.Add(1);
    public void RegistrarAprovacaoDuplicada() => _aprovacoesDuplicadas.Add(1);
    public void RegistrarOrcamentoGerado() => _orcamentosGerados.Add(1);
    public void RegistrarPagamentoAprovado() => _pagamentosAprovados.Add(1);
    public void RegistrarPagamentoRecusado() => _pagamentosRecusados.Add(1);
    public void RegistrarPagamentoRecusadoPeloGateway() => _pagamentosRecusadosPeloGateway.Add(1);
    public void RegistrarPagamentoEstornado() => _pagamentosEstornados.Add(1);
    public void RegistrarEstornoAbandonado() => _estornosAbandonados.Add(1);

    public void Dispose() => _meter.Dispose();
}
