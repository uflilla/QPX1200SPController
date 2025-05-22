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
                            OutputEnabled INTEGER NOT NULL,
                            CvMode INTEGER NOT NULL,
                            CcMode INTEGER NOT NULL,
                            UnregulatedMode INTEGER NOT NULL,
                            OvpTrip INTEGER NOT NULL,
                            OcpTrip INTEGER NOT NULL
                            
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
                            Timestamp, Voltage, Current,  
                            OutputEnabled, CvMode, 
                            CcMode,UnregulatedMode,OvpTrip,OcpTrip
                        ) VALUES (
                            @Timestamp, @Voltage, @Current,
                            @OutputEnabled, @CvMode, 
                            @CcMode,@UnregulatedMode,@OvpTrip,@OcpTrip
                        );
                    ";
                    
                    using (var command = new SQLiteCommand(insertSql, connection))
                    {
                        command.Parameters.AddWithValue("@Timestamp", status.Time);
                        command.Parameters.AddWithValue("@Voltage", status.Voltage);
                        command.Parameters.AddWithValue("@Current", status.Current);
                        command.Parameters.AddWithValue("@OutputEnabled", status.OutputEnabled ? 1 : 0);
                        command.Parameters.AddWithValue("@CvMode", status.EventStatus?.ConstantVoltageMode);
                        command.Parameters.AddWithValue("@CcMode", status.EventStatus?.ConstantCurrentMode);
                        command.Parameters.AddWithValue("@UnregulatedMode", status.EventStatus?.UnregulatedMode);
                        command.Parameters.AddWithValue("@OvpTrip", status.EventStatus?.OverVoltageTrip);
                        command.Parameters.AddWithValue("@OcpTrip", status.EventStatus?.OverCurrentTrip);
                        
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
                                    //Id = Convert.ToInt32(reader["Id"]),
                                    Timestamp = DateTime.Parse(reader["Timestamp"].ToString()),
                                    Voltage = Convert.ToDouble(reader["Voltage"]),
                                    Current = Convert.ToDouble(reader["Current"]),
                                    OutputEnabled = Convert.ToInt32(reader["OutputEnabled"]) == 1,
                                    CvMode = Convert.ToInt32(reader["CvMode"]) == 1,
                                    CcMode = Convert.ToInt32(reader["CcMode"]) == 1,
                                    UnregulatedMode = Convert.ToInt32(reader["UnregulatedMode"]) == 1,
                                    OvpTrip = Convert.ToInt32(reader["OvpTrip"]) == 1,
                                    OcpTrip = Convert.ToInt32(reader["OcpTrip"]) == 1,
                                    
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
                        writer.WriteLine("Timestamp,Voltage,Current,OutputEnabled,CvMode,CcMode,UnregulatedMode,OvpTrip,OcpTrip");
                        
                        // Write data
                        foreach (var log in logs)
                        {
                            writer.WriteLine(
                                $"{log.Timestamp:yyyy-MM-dd HH:mm:ss},{log.Voltage},{log.Current}," +
                                $"{(log.OutputEnabled ? 1 : 0)},{(log.CvMode ? 1 : 0)},{(log.CcMode ? 1 : 0)},{(log.UnregulatedMode ? 1 : 0)},{(log.OvpTrip ? 1 : 0)},{(log.OcpTrip ? 1 : 0)}"
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