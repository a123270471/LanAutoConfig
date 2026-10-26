using LanAutoConfig.Common;
using Microsoft.Data.Sqlite;

namespace LanAutoConfig.Master
{
    /// <summary>控制端 SQLite 日志存储：OpLog 表（文档 7 节）。</summary>
    public class SqliteLogStore
    {
        private readonly string _dbPath;

        public SqliteLogStore(string dbPath)
        {
            _dbPath = dbPath;
            EnsureSchema();
        }

        private string ConnString => $"Data Source={_dbPath}";

        private void EnsureSchema()
        {
            try
            {
                var dir = Path.GetDirectoryName(_dbPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using var conn = new SqliteConnection(ConnString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS OpLog (
    LogId INTEGER PRIMARY KEY AUTOINCREMENT,
    SessionId TEXT,
    OpTime TEXT,
    Mac TEXT,
    SourceIp TEXT,
    TargetHostName TEXT,
    TargetIp TEXT,
    Result TEXT,
    ErrorMsg TEXT
);";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("SQLite init error: " + ex.Message);
            }
        }

        /// <summary>插入一条执行日志。</summary>
        public void Insert(OpLogRecord r)
        {
            try
            {
                using var conn = new SqliteConnection(ConnString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO OpLog (SessionId,OpTime,Mac,SourceIp,TargetHostName,TargetIp,Result,ErrorMsg)
                                    VALUES ($s,$t,$m,$si,$hn,$ip,$res,$err);";
                cmd.Parameters.AddWithValue("$s", (object)r.SessionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$t", r.OpTime.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("$m", (object)r.Mac ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$si", (object)r.SourceIp ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$hn", (object)r.TargetHostName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ip", (object)r.TargetIp ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$res", (object)r.Result ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$err", (object)r.ErrorMsg ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("SQLite insert error: " + ex.Message);
            }
        }

        /// <summary>查询日志（按时间倒序），用于报表展示/导出。</summary>
        public List<OpLogRecord> Query(int limit = 1000)
        {
            var list = new List<OpLogRecord>();
            try
            {
                using var conn = new SqliteConnection(ConnString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT LogId,SessionId,OpTime,Mac,SourceIp,TargetHostName,TargetIp,Result,ErrorMsg FROM OpLog ORDER BY LogId DESC LIMIT $n;";
                cmd.Parameters.AddWithValue("$n", limit);
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    list.Add(new OpLogRecord
                    {
                        LogId = rd.GetInt64(0),
                        SessionId = rd.IsDBNull(1) ? "" : rd.GetString(1),
                        OpTime = DateTime.TryParse(rd.IsDBNull(2) ? "" : rd.GetString(2), out var t) ? t : DateTime.MinValue,
                        Mac = rd.IsDBNull(3) ? "" : rd.GetString(3),
                        SourceIp = rd.IsDBNull(4) ? "" : rd.GetString(4),
                        TargetHostName = rd.IsDBNull(5) ? "" : rd.GetString(5),
                        TargetIp = rd.IsDBNull(6) ? "" : rd.GetString(6),
                        Result = rd.IsDBNull(7) ? "" : rd.GetString(7),
                        ErrorMsg = rd.IsDBNull(8) ? "" : rd.GetString(8)
                    });
                }
            }
            catch (Exception ex) { Debug.WriteLine("SQLite query error: " + ex.Message); }
            return list;
        }
    }
}
