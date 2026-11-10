namespace OficinaMecanica.Billing.Application.Interfaces;

// Contadores dos casos que o webhook engole para responder 200 ao gateway (divergência, aprovação tardia e
// duplicada) e dos fatos de negócio que a operação acompanha: sem uma métrica o log é o único sinal, e um log
// se perde. Os contadores contam fatos já persistidos; um reprocessamento por retry pode contar de novo.
public interface IMetricasDoBilling
{
    void RegistrarDivergenciaDePagamento();
    void RegistrarAprovacaoTardia();
    void RegistrarAprovacaoDuplicada();
    void RegistrarOrcamentoGerado();
    void RegistrarPagamentoAprovado();
    void RegistrarPagamentoRecusado();
    void RegistrarPagamentoRecusadoPeloGateway();
    void RegistrarPagamentoEstornado();
    void RegistrarEstornoAbandonado();
}
