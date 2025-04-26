using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Threading.Tasks;
using QPX1200SPController.Models;

namespace QPX1200SPController.Services
{
    public class DatabaseService
    {
        private readonly string _dbPath;
        private readonly string _connectionString;

        public DatabaseService()
        {
            _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "powerlog.db");
            _connectionString = $"Data Source={_dbPath};Version=3;";
            
            // Ensure database exists
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            if (!File.Exists(_dbPath))
            {
                SQLiteConnection.CreateFile(_dbPath);
                
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    connection.Open();
                    
                    string createTableSql = @"
                        CREATE TABLE IF NOT EXISTS PowerLogs (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            Timestamp TEXT NOT NULL,
                            Voltage REAL NOT NULL,
                            Current REAL NOT NULL,
                            Power REAL NOT NULL,
                            OutputEnabled INTEGER NOT NULL,
                            OverVoltageProtection REAL NOT NULL,
                            OverCurrentProtection REAL NOT NULL
                        );
                    ";
                    
                    using (var command = new SQLiteCommand(createTableSql, connection))
                    {
                        command.ExecuteNonQuery();
                    }
                }
            }
        }

        public async Task LogPowerSupplyStatusAsync(PowerSupplyStatus status)
        {
            await Task.Run(() =>
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    connection.Open();
                    
                    string insertSql = @"
                        INSERT INTO PowerLogs (
                            Timestamp, Voltage, Current, Power, 
                            OutputEnabled, OverVoltageProtection, 
                            OverCurrentProtection
                        ) VALUES (
                            @Timestamp, @Voltage, @Current, @Power, 
                            @OutputEnabled, @OverVoltageProtection, 
                            @OverCurrentProtection
                        );
                    ";
                    
                    using (var command = new SQLiteCommand(insertSql, connection))
                    {
                        command.Parameters.AddWithValue("@Timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        command.Parameters.AddWithValue("@Voltage", status.Voltage);
                        command.Parameters.AddWithValue("@Current", status.Current);
                        command.Parameters.AddWithValue("@Power", status.Power);
                        command.Parameters.AddWithValue("@OutputEnabled", status.OutputEnabled ? 1 : 0);
                        command.Parameters.AddWithValue("@OverVoltageProtection", status.OverVoltageProtection);
                        command.Parameters.AddWithValue("@OverCurrentProtection", status.OverCurrentProtection);
                        
                        command.ExecuteNonQuery();
                    }
                }
            });
        }

        public async Task<List<LogEntry>> GetLogsAsync(DateTime startTime, DateTime endTime)
        {
            return await Task.Run(() =>
            {
                var logs = new List<LogEntry>();
                
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    connection.Open();
                    
                    string selectSql = @"
                        SELECT * FROM PowerLogs 
                        WHERE Timestamp BETWEEN @StartTime AND @EndTime
                        ORDER BY Timestamp ASC;
                    ";
                    
                    using (var command = new SQLiteCommand(selectSql, connection))
                    {
                        command.Parameters.AddWithValue("@StartTime", startTime.ToString("yyyy-MM-dd HH:mm:ss"));
                        command.Parameters.AddWithValue("@EndTime", endTime.ToString("yyyy-MM-dd HH:mm:ss"));
                        
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                logs.Add(new LogEntry
                                {
                                    Id = Convert.ToInt32(reader["Id"]),
                                    Timestamp = DateTime.Parse(reader["Timestamp"].ToString()),
                                    Voltage = Convert.ToDouble(reader["Voltage"]),
                                    Current = Convert.ToDouble(reader["Current"]),
                                    Power = Convert.ToDouble(reader["Power"]),
                                    OutputEnabled = Convert.ToInt32(reader["OutputEnabled"]) == 1,
                                    OverVoltageProtection = Convert.ToDouble(reader["OverVoltageProtection"]),
                                    OverCurrentProtection = Convert.ToDouble(reader["OverCurrentProtection"])
                                });
                            }
                        }
                    }
                }
                
                return logs;
            });
        }

        public async Task<bool> ExportToCsvAsync(List<LogEntry> logs, string filePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using (var writer = new StreamWriter(filePath))
                    {
                        // Write header
                        writer.WriteLine("Timestamp,Voltage,Current,Power,OutputEnabled,OverVoltageProtection,OverCurrentProtection");
                        
                        // Write data
                        foreach (var log in logs)
                        {
                            writer.WriteLine(
                                $"{log.Timestamp:yyyy-MM-dd HH:mm:ss},{log.Voltage},{log.Current},{log.Power}," +
                                $"{(log.OutputEnabled ? 1 : 0)},{log.OverVoltageProtection},{log.OverCurrentProtection}"
                            );
                        }
                    }
                    
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            });
        }
    }
}