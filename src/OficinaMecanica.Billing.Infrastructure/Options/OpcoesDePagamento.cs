using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace OficinaMecanica.Billing.Infrastructure.Options;

public enum ProvedorDePagamento
{
    Fake = 1,
    MercadoPago = 2
}

// Seção "Pagamento": escolhe o gateway. O padrão é MercadoPago de propósito: esquecer a configuração em um
// ambiente real falha na partida (credenciais obrigatórias) em vez de subir com um gateway de mentira.
public sealed class OpcoesDePagamento
{
    public const string Secao = "Pagamento";

    public ProvedorDePagamento Provider { get; set; } = ProvedorDePagamento.MercadoPago;
}

public sealed class OpcoesDoMercadoPago
{
    public const string Secao = "MercadoPago";
    public const int TimeoutPadraoEmSegundos = 10;
    public const int ToleranciaPadraoDaAssinaturaEmSegundos = 300;

    public string? AccessToken { get; set; }
    public string? WebhookSecret { get; set; }
    public string? NotificationBaseUrl { get; set; }

    // Poucos segundos: a chamada ao gateway roda com a transação do consumer aberta, e um timeout longo
    // seguraria conexão e locks do SQL enquanto o gateway não responde.
    public int TimeoutSegundos { get; set; } = TimeoutPadraoEmSegundos;
    public int ToleranciaDaAssinaturaSegundos { get; set; } = ToleranciaPadraoDaAssinaturaEmSegundos;

    public string? BackUrlSucesso { get; set; }
    public string? BackUrlPendente { get; set; }
    public string? BackUrlFalha { get; set; }
}

public sealed class OpcoesDoGatewayFake
{
    public const string Secao = "Fake";

    public string? WebhookSecret { get; set; }
}

// Falha a partida (ValidateOnStart) quando a configuração do provedor escolhido está incompleta ou quando o
// Fake é pedido fora de Development: o Fake aprova qualquer coisa que o endpoint de dev mandar.
public sealed class ValidadorDasOpcoesDePagamento : IValidateOptions<OpcoesDePagamento>
{
    private readonly IOptions<OpcoesDoMercadoPago> _mercadoPago;
    private readonly IOptions<OpcoesDoGatewayFake> _fake;
    private readonly IHostEnvironment _ambiente;

    public ValidadorDasOpcoesDePagamento(
        IOptions<OpcoesDoMercadoPago> mercadoPago,
        IOptions<OpcoesDoGatewayFake> fake,
        IHostEnvironment ambiente)
    {
        _mercadoPago = mercadoPago;
        _fake = fake;
        _ambiente = ambiente;
    }

    public ValidateOptionsResult Validate(string? name, OpcoesDePagamento options)
    {
        var falhas = new List<string>();

        if (options.Provider == ProvedorDePagamento.Fake)
        {
            if (!_ambiente.IsDevelopment())
                falhas.Add("Pagamento:Provider=Fake só é permitido em Development.");

            if (string.IsNullOrWhiteSpace(_fake.Value.WebhookSecret))
                falhas.Add("Fake:WebhookSecret é obrigatório com Pagamento:Provider=Fake.");
        }
        else
        {
            var mp = _mercadoPago.Value;
            if (string.IsNullOrWhiteSpace(mp.AccessToken))
                falhas.Add("MercadoPago:AccessToken é obrigatório com Pagamento:Provider=MercadoPago.");
            if (string.IsNullOrWhiteSpace(mp.WebhookSecret))
                falhas.Add("MercadoPago:WebhookSecret é obrigatório com Pagamento:Provider=MercadoPago.");
            if (!Uri.TryCreate(mp.NotificationBaseUrl, UriKind.Absolute, out _))
                falhas.Add("MercadoPago:NotificationBaseUrl deve ser uma URL absoluta com Pagamento:Provider=MercadoPago.");
        }

        if (_mercadoPago.Value.TimeoutSegundos <= 0)
            falhas.Add("MercadoPago:TimeoutSegundos deve ser maior que zero.");
        if (_mercadoPago.Value.ToleranciaDaAssinaturaSegundos <= 0)
            falhas.Add("MercadoPago:ToleranciaDaAssinaturaSegundos deve ser maior que zero.");

        return falhas.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(falhas);
    }
}
