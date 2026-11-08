using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Infrastructure.Options;
using RabbitMQ.Client;

namespace OficinaMecanica.Billing.Infrastructure.Messaging;

// Abre (e fecha) uma conexão AMQP de verdade. Optei por não confiar no check do bus do MassTransit para saber se o
// broker está de pé: ele pode continuar saudável com o RabbitMQ parado.
//
// O broker fora responde Degraded, e não Unhealthy: com o outbox transacional a API continua aceitando escrita (a
// mensagem espera no banco), então tirar o pod de rotação por causa do broker faria uma queda do RabbitMQ virar
// indisponibilidade total. Degraded mantém o /health/ready em 200 e aparece no corpo para quem monitora.
public sealed class RabbitMqHealthCheck : IHealthCheck
{
    private static readonly TimeSpan LimiteDaConexao = TimeSpan.FromSeconds(3);

    private readonly IOptions<OpcoesDoRabbitMq> _opcoes;
    private readonly Func<ConnectionFactory, CancellationToken, Task<IConnection>> _conectar;

    public RabbitMqHealthCheck(IOptions<OpcoesDoRabbitMq> opcoes)
        : this(opcoes, (fabrica, token) => fabrica.CreateConnectionAsync(token))
    {
    }

    // Ponto de troca para teste: permite simular uma conexão que só completa depois do limite, o que o cliente real não
    // reproduz de forma determinística.
    internal RabbitMqHealthCheck(IOptions<OpcoesDoRabbitMq> opcoes, Func<ConnectionFactory, CancellationToken, Task<IConnection>> conectar)
    {
        _opcoes = opcoes;
        _conectar = conectar;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var rabbit = _opcoes.Value;
        var fabrica = new ConnectionFactory
        {
            HostName = rabbit.Host,
            Port = rabbit.Port,
            VirtualHost = rabbit.VirtualHost,
            ClientProvidedName = "OficinaMecanica.Billing.health",
            RequestedConnectionTimeout = LimiteDaConexao
        };

        // Sem usuário informado vale o padrão do ConnectionFactory (guest), o mesmo que o MassTransit usa.
        if (!string.IsNullOrEmpty(rabbit.Username)) fabrica.UserName = rabbit.Username;
        if (!string.IsNullOrEmpty(rabbit.Password)) fabrica.Password = rabbit.Password;

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(LimiteDaConexao);

        var tentativa = _conectar(fabrica, limite.Token);

        try
        {
            // O WaitAsync garante o limite mesmo quando o cliente não o respeita: com o broker congelado (TCP aceito,
            // handshake sem resposta) o cancelamento não interrompe a espera e a desistência do cliente leva ~8 s
            // (medido), o que estouraria o timeout de 5 s da readiness do Kubernetes.
            await using var conexao = await tentativa.WaitAsync(LimiteDaConexao, cancellationToken);
            return HealthCheckResult.Healthy("Conexão AMQP com o RabbitMQ estabelecida.");
        }
        catch (Exception ex)
        {
            // A tentativa é entregue ao fechamento nos dois desfechos (limite estourado e cancelamento do chamador): um
            // broker congelado que completar o handshake depois não pode deixar uma conexão aberta.
            _ = FecharSeConectarDepoisAsync(tentativa);

            // Se quem cancelou foi o chamador (requisição abortada), a exceção sobe: virar Degraded gravaria uma medição
            // falsa no HealthCheckComCache. Só o limite próprio de 3 s, ou uma falha da conexão, é Degraded.
            if (cancellationToken.IsCancellationRequested) throw;

            return HealthCheckResult.Degraded("Não foi possível conectar ao RabbitMQ; as mensagens ficam no outbox até o broker voltar.", ex);
        }
    }

    // A tentativa abandonada (pelo limite ou pelo cancelamento do chamador) continua em segundo plano: se ainda conectar,
    // fecho a conexão para não vazar e, se falhar, observo a exceção para ela não ficar sem tratamento.
    private static async Task FecharSeConectarDepoisAsync(Task<IConnection> tentativa)
    {
        try
        {
            await using var conexao = await tentativa;
        }
        catch (Exception)
        {
            // Falhou ou foi cancelada: não há conexão a fechar.
        }
    }
}
