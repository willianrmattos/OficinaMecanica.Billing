using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FluentValidation;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Domain.Exceptions;

namespace OficinaMecanica.Billing.API.Middleware;

public class ExceptionHandlingMiddleware
{
    private const int ViolacaoDeIndiceUnico = 2601;
    private const int ViolacaoDeChaveUnica = 2627;
    private const string RotaDesconhecida = "(rota desconhecida)";

    private static class TipoErro
    {
        public const string Negocio = "Erro de Negócio";
        public const string Validacao = "Erro de Validação";
        public const string NaoEncontrado = "Não Encontrado";
        public const string NaoAutorizado = "Não Autorizado";
        public const string Conflito = "Conflito";
        public const string Indisponivel = "Serviço Indisponível";
        public const string Interno = "Erro Interno";
    }

    // O escape relaxado mantém os acentos legíveis nas mensagens em português (o padrão os escapa em hexadecimal);
    // é seguro porque a resposta é JSON puro, nunca HTML.
    private static readonly JsonSerializerOptions OpcoesJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        // Quando o cliente fecha a conexão (timeout do chamador, do APIM ou do próprio usuário), o ASP.NET cancela o
        // RequestAborted e cada biblioteca que usa o token reage a seu modo: o HttpClient e o EF com o token já cancelado
        // lançam OperationCanceledException, mas o SqlClient, com um comando em voo, lança SqlException ("Operation cancelled
        // by user") e o SaveChangesAsync ainda a embrulha em DbUpdateException. CancelamentoDaRequisicao reconhece as duas
        // formas. Não há mais para quem responder, então não escrevo corpo e não trato como erro do servidor: um ERR por
        // timeout alheio geraria alerta sem haver defeito. Defino 499 (convenção do nginx para "cliente fechou a
        // requisição") para o log de acesso, a telemetria do OpenTelemetry e as métricas HTTP do Prometheus
        // (UseHttpMetrics fica por fora deste middleware), que leem o status final, não contarem a chamada como sucesso; é
        // inofensivo porque ninguém lê essa resposta. Cancelamento que não vem do RequestAborted (por exemplo, o timeout
        // interno de uma dependência), erro real de SQL com a requisição abortada (deadlock, timeout, chave duplicada) e
        // resposta já iniciada seguem para o tratamento geral abaixo.
        catch (Exception ex) when (context.RequestAborted.IsCancellationRequested
                                   && !context.Response.HasStarted
                                   && CancelamentoDaRequisicao.Eh(ex))
        {
            RegistrarRequisicaoAbandonada(context);
        }
        catch (Exception ex)
        {
            if (context.Response.HasStarted)
            {
                TratarRespostaIniciada(context, ex);
                return;
            }

            await TratarExcecaoAsync(context, ex);
        }
    }

    // Com a resposta já iniciada o status e os cabeçalhos estão congelados: no Kestrel, mexer neles lança
    // InvalidOperationException e a falha original se perderia dentro dela. Registro o erro e aborto a conexão: se eu só
    // retornasse, o Kestrel encerraria a resposta em chunks normalmente e o cliente receberia um 200 com o corpo truncado
    // como se estivesse completo; com o reset ele vê a falha. Quando foi o próprio cliente que fechou a conexão não há erro
    // do servidor nem o que abortar, então só registro a informação.
    private void TratarRespostaIniciada(HttpContext context, Exception exception)
    {
        if (context.RequestAborted.IsCancellationRequested && CancelamentoDaRequisicao.Eh(exception))
        {
            _logger.LogInformation(
                "Requisição cancelada pelo cliente com a resposta já iniciada: {Rota} (trace {TraceId}).",
                RotaDe(context),
                TraceIdDe(context));
            return;
        }

        _logger.LogError(exception, "Erro não tratado com a resposta já iniciada: {Message}", exception.Message);
        context.Abort();
    }

    private void RegistrarRequisicaoAbandonada(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;

        _logger.LogInformation(
            "Requisição cancelada pelo cliente antes da resposta: {Rota} (trace {TraceId}).",
            RotaDe(context),
            TraceIdDe(context));
    }

    // Registro só o template da rota e o id do trace, nunca o caminho nem a query: o caminho pode carregar dado pessoal ou
    // identificador (ids de orçamento e pagamento) e a query pode carregar token, e esta é a única linha de log da requisição.
    private static string RotaDe(HttpContext context) =>
        (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? RotaDesconhecida;

    private static string TraceIdDe(HttpContext context) =>
        Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

    private async Task TratarExcecaoAsync(HttpContext context, Exception exception)
    {
        var response = context.Response;
        response.ContentType = "application/json";

        var erro = new ErrorResponse();

        switch (exception)
        {
            case DomainException domainEx:
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                erro.Mensagem = domainEx.Message;
                erro.Tipo = TipoErro.Negocio;
                _logger.LogWarning("Erro de domínio: {Message}", domainEx.Message);
                break;

            case ValidationException validationEx:
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                erro.Mensagem = "Erro de validação.";
                erro.Tipo = TipoErro.Validacao;
                erro.Erros = validationEx.Errors.Select(e => new ErrorDetail(e.PropertyName, e.ErrorMessage)).ToList();
                _logger.LogWarning("Erro de validação: {Errors}", string.Join(", ", validationEx.Errors.Select(e => e.ErrorMessage)));
                break;

            case KeyNotFoundException:
                response.StatusCode = (int)HttpStatusCode.NotFound;
                erro.Mensagem = "Recurso não encontrado.";
                erro.Tipo = TipoErro.NaoEncontrado;
                break;

            case AssinaturaWebhookInvalidaException:
                // Sem detalhe do motivo: dizer qual parte da assinatura falhou ajudaria quem tenta forjá-la.
                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                erro.Mensagem = "Assinatura do webhook inválida.";
                erro.Tipo = TipoErro.NaoAutorizado;
                _logger.LogWarning("Webhook rejeitado: assinatura inválida.");
                break;

            case PagamentoDoGatewayIndisponivelException indisponivel:
                // 503 faz o Mercado Pago reentregar a notificação, que é o que se quer enquanto ele ainda não
                // enxerga o pagamento que acabou de avisar.
                response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                response.Headers.RetryAfter = "30";
                erro.Mensagem = "O pagamento ainda não está disponível no gateway. Tente novamente.";
                erro.Tipo = TipoErro.Indisponivel;
                _logger.LogWarning("{Message}", indisponivel.Message);
                break;

            case DbUpdateConcurrencyException:
            case DbUpdateException { InnerException: SqlException { Number: ViolacaoDeIndiceUnico or ViolacaoDeChaveUnica } }:
                // Duas requisições concorrentes sobre o mesmo registro: a que perdeu pode ser repetida e então
                // encontra o resultado da vencedora.
                response.StatusCode = (int)HttpStatusCode.Conflict;
                erro.Mensagem = "A operação conflitou com outra requisição concorrente. Tente novamente.";
                erro.Tipo = TipoErro.Conflito;
                _logger.LogWarning(exception, "Conflito de concorrência na persistência.");
                break;

            default:
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                erro.Mensagem = "Ocorreu um erro interno no servidor.";
                erro.Tipo = TipoErro.Interno;
                _logger.LogError(exception, "Erro não tratado: {Message}", exception.Message);
                break;
        }

        // Optei por não passar o RequestAborted: o ramo da requisição abandonada (499) já foi tratado antes, e este corpo é a
        // resposta de erro ao cliente. Com o token, a escrita lançaria OperationCanceledException de dentro do tratador de
        // erros se o cliente saísse neste instante, e a exceção original se perderia.
#pragma warning disable S8949 // Pass the 'context.RequestAborted' to this method
        await response.WriteAsync(JsonSerializer.Serialize(erro, OpcoesJson));
#pragma warning restore S8949
    }
}

public class ErrorResponse
{
    public string Tipo { get; set; } = string.Empty;
    public string Mensagem { get; set; } = string.Empty;
    public List<ErrorDetail>? Erros { get; set; }
}

public record ErrorDetail(string Campo, string Mensagem);
