namespace OficinaMecanica.Billing.Application.Interfaces;

// Contadores dos casos que o webhook engole para responder 200 ao gateway: sem uma métrica o log é o único
// sinal, e um log se perde. A implementação real (System.Diagnostics.Metrics) entra na onda 2.
public interface IMetricasDoBilling
{
    void RegistrarDivergenciaDePagamento();
    void RegistrarAprovacaoTardia();
    void RegistrarAprovacaoDuplicada();
}
