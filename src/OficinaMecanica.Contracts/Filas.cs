namespace OficinaMecanica.Contracts;

// Optei por manter os nomes de fila como constantes dentro do proprio contrato: produtor e
// consumidor leem o mesmo valor, entao um erro de digitacao vira erro de compilacao em vez de
// mensagem perdida numa fila que ninguem consome.
public static class Filas
{
    public const string Saga = "os-saga";
    public const string Billing = "billing-comandos";
    public const string Execucao = "execucao-comandos";
}
