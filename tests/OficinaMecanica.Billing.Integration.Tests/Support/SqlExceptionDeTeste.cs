using System.Reflection;
using Microsoft.Data.SqlClient;

namespace OficinaMecanica.Billing.Integration.Tests.Support;

// O SqlClient não expõe construtor público para SqlException, SqlError nem SqlErrorCollection. Para montar uma exceção com
// os erros (número, classe, estado) que o servidor ou o próprio cliente produziram, uso os membros internos por reflexão.
// Os valores usados nos testes vêm de exceções reais capturadas de um SQL Server (ver CancelamentoDaRequisicaoSqlTests, que
// valida o critério contra a exceção verdadeira, não contra esta montada à mão).
public static class SqlExceptionDeTeste
{
    public sealed record ErroDeSql(int Numero, byte Classe, byte Estado, string Mensagem);

    public static SqlException Criar(params ErroDeSql[] erros) => CriarComCausa(null, erros);

    // Com a exceção interna que o SqlClient anexa quando o erro nasce de outra falha (por exemplo, um cancelamento).
    public static SqlException CriarComCausa(Exception? causa, params ErroDeSql[] erros)
    {
        var colecao = CriarInstancia(typeof(SqlErrorCollection));
        var adicionar = typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SqlErrorCollection.Add (interno) não encontrado nesta versão do SqlClient.");

        foreach (var erro in erros)
            adicionar.Invoke(colecao, new[] { CriarErro(erro) });

        var fabrica = typeof(SqlException)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(m => m.Name == "CreateException")
            .OrderBy(m => m.GetParameters().Length)
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(SqlErrorCollection)))
            .FirstOrDefault(m => causa is null || m.GetParameters().Any(p => p.ParameterType == typeof(Exception)))
            ?? throw new InvalidOperationException("SqlException.CreateException (interno) não encontrado nesta versão do SqlClient.");

        var argumentos = fabrica.GetParameters()
            .Select(p => p.ParameterType == typeof(SqlErrorCollection) ? colecao
                : p.ParameterType == typeof(string) ? "15.0.0"
                : p.ParameterType == typeof(Guid) ? Guid.NewGuid()
                : p.ParameterType == typeof(Exception) ? causa
                : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
            .ToArray();

        return (SqlException)fabrica.Invoke(null, argumentos)!;
    }

    private static object CriarErro(ErroDeSql erro)
    {
        var construtor = typeof(SqlError)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(c => c.GetParameters().Length >= 7
                        && c.GetParameters()[0].ParameterType == typeof(int)
                        && c.GetParameters()[1].ParameterType == typeof(byte)
                        && c.GetParameters()[2].ParameterType == typeof(byte))
            .OrderBy(c => c.GetParameters().Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Construtor interno de SqlError não encontrado nesta versão do SqlClient.");

        var parametros = construtor.GetParameters();
        var argumentos = new object?[parametros.Length];
        argumentos[0] = erro.Numero;
        argumentos[1] = erro.Estado;
        argumentos[2] = erro.Classe;
        for (var i = 3; i < parametros.Length; i++)
        {
            argumentos[i] = parametros[i].ParameterType == typeof(string) ? (parametros[i].Name == "errorMessage" ? erro.Mensagem : string.Empty)
                : parametros[i].ParameterType.IsValueType ? Activator.CreateInstance(parametros[i].ParameterType) : null;
        }

        return construtor.Invoke(argumentos);
    }

    private static object CriarInstancia(Type tipo) =>
        Activator.CreateInstance(tipo, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, null, null)!;

    // Os erros reais que o SqlClient 5.x devolveu ao cancelar um comando em voo (WAITFOR DELAY) e os demais erros de SQL
    // com que o critério não pode se confundir.
    public static SqlException CancelamentoDeComando() => Criar(
        new ErroDeSql(0, 11, 0, "A severe error occurred on the current command.  The results, if any, should be discarded."),
        new ErroDeSql(0, 11, 0, "Operation cancelled by user."));

    public static SqlException TimeoutDeComando() => Criar(
        new ErroDeSql(-2, 11, 0, "Execution Timeout Expired.  The timeout period elapsed prior to completion of the operation or the server is not responding."));

    public static SqlException Deadlock() => Criar(
        new ErroDeSql(1205, 13, 51, "Transaction (Process ID 55) was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Rerun the transaction."));

    public static SqlException IndiceUnicoDuplicado() => Criar(
        new ErroDeSql(2601, 14, 1, "Cannot insert duplicate key row in object 'dbo.X' with unique index 'IX_X'."),
        new ErroDeSql(3621, 0, 0, "The statement has been terminated."));

    public static SqlException SessaoDerrubada() => Criar(
        new ErroDeSql(596, 21, 1, "Cannot continue the execution because the session is in the kill state."),
        new ErroDeSql(0, 20, 0, "A severe error occurred on the current command.  The results, if any, should be discarded."));

    public static SqlException LoginRecusado() => Criar(
        new ErroDeSql(4060, 11, 1, "Cannot open database requested by the login. The login failed."),
        new ErroDeSql(18456, 14, 1, "Login failed for user."));
}
