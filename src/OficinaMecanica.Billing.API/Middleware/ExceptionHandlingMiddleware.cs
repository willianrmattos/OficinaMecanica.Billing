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
        catch (Exception ex)
        {
            await TratarExcecaoAsync(context, ex);
        }
    }

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

        await response.WriteAsync(JsonSerializer.Serialize(erro, OpcoesJson));
    }
}

public class ErrorResponse
{
    public string Tipo { get; set; } = string.Empty;
    public string Mensagem { get; set; } = string.Empty;
    public List<ErrorDetail>? Erros { get; set; }
}

public record ErrorDetail(string Campo, string Mensagem);
