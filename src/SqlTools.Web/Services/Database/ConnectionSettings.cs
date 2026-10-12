using Microsoft.Data.SqlClient;

namespace SqlTools.Web.Services;

/// <summary>Dados informados na tela de conexão (no estilo do SQL Server Management Studio).</summary>
public sealed record ConnectionSettings(
    string Server,
    bool WindowsAuthentication,
    string? UserName,
    string? Password,
    string? Database)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Server))
            return "Informe o servidor.";
        if (!WindowsAuthentication && string.IsNullOrWhiteSpace(UserName))
            return "Informe o usuário.";
        return null;
    }

    /// <summary>
    /// Monta a string de conexão. TrustServerCertificate é sempre ligado: servidores de desenvolvimento
    /// e instâncias locais costumam usar certificado autoassinado, e o usuário não precisa saber disso.
    /// </summary>
    public string BuildConnectionString(string? database = null)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Server.Trim(),
            TrustServerCertificate = true,
            ConnectTimeout = 30, // servidores remotos/VPN do ERP podem demorar a aceitar a conexão
            ApplicationName = "SqlTools",
        };

        if (WindowsAuthentication)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = UserName!.Trim();
            builder.Password = Password ?? "";
        }

        var db = database ?? Database;
        if (!string.IsNullOrWhiteSpace(db))
            builder.InitialCatalog = db;

        return builder.ConnectionString;
    }
}

public sealed record DatabaseEntry(string Name, bool IsSystem);
