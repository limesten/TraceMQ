using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TraceMQ.Api.Storage;

namespace TraceMQ.Tests;

/// <summary>
/// A database on disk in a throwaway directory. Not in-memory: WAL, file locking and the
/// journal pragmas all behave differently there, and those are what we are testing.
/// </summary>
public sealed class TempDb : IDisposable
{
    public string Directory { get; }
    public string Path { get; }
    public SqliteConnectionFactory Factory { get; }

    public TempDb()
    {
        Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tracemq-tests", Guid.NewGuid().ToString("n"));
        System.IO.Directory.CreateDirectory(Directory);
        Path = System.IO.Path.Combine(Directory, "data.db");
        Factory = new SqliteConnectionFactory(
            Options.Create(new StorageOptions { DbPath = Path }),
            new StubEnvironment(Directory));
    }

    public void Execute(string sql)
    {
        using var connection = Factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public T Scalar<T>(string sql)
    {
        using var connection = Factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return (T)Convert.ChangeType(value!, typeof(T));
    }

    public List<string> Strings(string sql)
    {
        using var connection = Factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
    }

    private sealed class StubEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "TraceMQ.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
