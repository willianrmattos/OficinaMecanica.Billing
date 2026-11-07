using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace OficinaMecanica.Billing.Infrastructure.Options;

public enum TransporteDaMensageria
{
    RabbitMq = 1,
    EmMemoria = 2
}

public enum PersistenciaDaMensageria
{
    EntityFramework = 1,
    Nenhuma = 2
}

// Seção "Mensageria". Transporte e Persistência existem só para os testes trocarem o RabbitMQ pelo transporte
// em memória e o outbox do EF por nada (EF InMemory não suporta o outbox transacional); em produção ficam nos
// padrões.
public sealed class OpcoesDaMensageria
{
    public const string Secao = "Mensageria";

    public static readonly int[] IntervalosDeRetryPadraoEmMs = { 500, 2_000, 10_000, 30_000, 60_000 };

    public TransporteDaMensageria Transporte { get; set; } = TransporteDaMensageria.RabbitMq;
    public PersistenciaDaMensageria Persistencia { get; set; } = PersistenciaDaMensageria.EntityFramework;

    // Cada intervalo é uma nova tentativa da mensagem inteira, em transação nova; o último é o teto antes de a
    // mensagem ir para a fila _error. Começa vazio de propósito: o binder de configuração acrescenta os itens
    // configurados aos de um array já preenchido em vez de substituí-los, então o padrão fica em
    // IntervalosDeRetryPadraoEmMs e só vale quando nada foi configurado.
    public int[] RetryIntervalosMs { get; set; } = Array.Empty<int>();

    // Tempo entre as varreduras do outbox; o padrão do MassTransit é 1 segundo.
    public int OutboxQueryDelayMs { get; set; } = 1_000;

    public int TimeoutDeInicioSegundos { get; set; } = 60;

    public TimeSpan[] IntervalosDeRetry() =>
        (RetryIntervalosMs is { Length: > 0 } ? RetryIntervalosMs : IntervalosDeRetryPadraoEmMs)
            .Select(ms => TimeSpan.FromMilliseconds(ms))
            .ToArray();
}

// Falha a partida (ValidateOnStart) quando o transporte em memória ou a ausência de outbox/inbox são pedidos fora de
// Development e Testing: sem o RabbitMQ e o outbox do EF a API sobe "saudável" mas perde as mensagens em silêncio e
// ainda perde a exclusividade por ordem de serviço. Os ambientes reais (Staging, Production, o do k8s) exigem os dois.
public sealed class ValidadorDasOpcoesDaMensageria : IValidateOptions<OpcoesDaMensageria>
{
    private readonly IHostEnvironment _ambiente;

    public ValidadorDasOpcoesDaMensageria(IHostEnvironment ambiente)
    {
        _ambiente = ambiente;
    }

    public ValidateOptionsResult Validate(string? name, OpcoesDaMensageria options)
    {
        var falhas = new List<string>();
        var permiteAtalhos = _ambiente.IsDevelopment() || _ambiente.IsEnvironment("Testing");

        // O binder de configuração aceita qualquer número para um enum ("0", "9"). Um valor sem nome não é nem RabbitMq nem
        // EmMemoria: recuso em qualquer ambiente em vez de deixar o código tratar "o que não é RabbitMq" como memória.
        if (!Enum.IsDefined(options.Transporte))
            falhas.Add($"Mensageria:Transporte tem um valor inválido ({options.Transporte}); use RabbitMq ou EmMemoria.");

        if (!Enum.IsDefined(options.Persistencia))
            falhas.Add($"Mensageria:Persistencia tem um valor inválido ({options.Persistencia}); use EntityFramework ou Nenhuma.");

        if (options.Transporte == TransporteDaMensageria.EmMemoria && !permiteAtalhos)
            falhas.Add($"Mensageria:Transporte=EmMemoria só é permitido em Development ou Testing (ambiente atual: {_ambiente.EnvironmentName}); use RabbitMq.");

        if (options.Persistencia == PersistenciaDaMensageria.Nenhuma && !permiteAtalhos)
            falhas.Add($"Mensageria:Persistencia=Nenhuma só é permitido em Development ou Testing (ambiente atual: {_ambiente.EnvironmentName}); use EntityFramework.");

        if (options.TimeoutDeInicioSegundos <= 0)
            falhas.Add("Mensageria:TimeoutDeInicioSegundos deve ser maior que zero.");

        if (options.OutboxQueryDelayMs <= 0)
            falhas.Add("Mensageria:OutboxQueryDelayMs deve ser maior que zero.");

        if (options.RetryIntervalosMs.Any(ms => ms < 0))
            falhas.Add("Mensageria:RetryIntervalosMs não pode ter intervalos negativos.");

        return falhas.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(falhas);
    }
}

public sealed class OpcoesDoRabbitMq
{
    public const string Secao = "RabbitMq";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class OpcoesDoBilling
{
    public const string Secao = "Billing";

    public bool ReconciliadorHabilitado { get; set; } = true;
    public int ReconciliadorIntervaloSegundos { get; set; } = 300;
    public int ReconciliadorTamanhoDoLote { get; set; } = 50;
    public int ReconciliadorMaximoDeTentativasDeEstorno { get; set; } = 10;
}

// Um intervalo zero derrubaria o PeriodicTimer do reconciliador e um lote acima do limite da consulta faria todo ciclo
// falhar na validação; por isso a configuração é conferida na partida. Com o reconciliador desligado nada é exigido.
public sealed class ValidadorDasOpcoesDoBilling : IValidateOptions<OpcoesDoBilling>
{
    public const int LoteMaximo = 1000;

    public ValidateOptionsResult Validate(string? name, OpcoesDoBilling options)
    {
        if (!options.ReconciliadorHabilitado)
            return ValidateOptionsResult.Success;

        var falhas = new List<string>();

        if (options.ReconciliadorIntervaloSegundos <= 0)
            falhas.Add("Billing:ReconciliadorIntervaloSegundos deve ser maior que zero.");

        if (options.ReconciliadorTamanhoDoLote is <= 0 or > LoteMaximo)
            falhas.Add($"Billing:ReconciliadorTamanhoDoLote deve estar entre 1 e {LoteMaximo}.");

        if (options.ReconciliadorMaximoDeTentativasDeEstorno <= 0)
            falhas.Add("Billing:ReconciliadorMaximoDeTentativasDeEstorno deve ser maior que zero.");

        return falhas.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(falhas);
    }
}
