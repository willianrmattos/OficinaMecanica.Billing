using Microsoft.Data.SqlClient;

namespace OficinaMecanica.Billing.API.Middleware;

// Decide se uma exceção é só o efeito de o cliente ter fechado a conexão. Quem chama já exige que o RequestAborted esteja
// cancelado; aqui só se separa o cancelamento dos erros reais que podem acontecer ao mesmo tempo.
public static class CancelamentoDaRequisicao
{
    // Com um comando em voo, o SqlClient devolve uma SqlException com dois erros sintetizados no cliente, não vindos do
    // servidor, ambos com Number 0, Class 11 e State 0 ("A severe error occurred on the current command" e "Operation
    // cancelled by user"). O critério é numérico, sem depender do texto (que pode ser localizado), e deixa de fora os erros
    // reais que podem coincidir com o aborto: timeout de comando é -2, deadlock é 1205 (classe 13), chave duplicada é
    // 2601/2627 (classe 14), sessão derrubada é 596 (classe 21, com o segundo erro em classe 20) e login recusado é
    // 4060/18456.
    private const int NumeroDoErroGeradoNoCliente = 0;
    private const int ClasseDoCancelamento = 11;
    private const int EstadoDoCancelamento = 0;

    public static bool Eh(Exception exception)
    {
        // A cadeia é percorrida porque o EF embrulha a SqlException em DbUpdateException no SaveChangesAsync. O primeiro
        // elo que for cancelamento ou SqlException decide; uma SqlException que não é cancelamento encerra a busca, para
        // que um erro real de SQL nunca seja tomado por abandono.
        for (var atual = exception; atual is not null; atual = atual.InnerException)
        {
            switch (atual)
            {
                case OperationCanceledException:
                    return true;
                case SqlException sql:
                    return EhCancelamentoDeComando(sql);
            }
        }

        return false;
    }

    private static bool EhCancelamentoDeComando(SqlException sql) =>
        sql.Errors.Cast<SqlError>().All(erro =>
            erro.Number == NumeroDoErroGeradoNoCliente
            && erro.Class == ClasseDoCancelamento
            && erro.State == EstadoDoCancelamento);
}
