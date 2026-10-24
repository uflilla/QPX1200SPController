using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QPX1200SPController.Models;

namespace QPX1200SPController.Services
{
    /// <summary>
    /// SQLite storage (powerlog.db next to the executable).
    /// Samples and activity records are queued in memory and written in batches by a
    /// background flush, so a slow or locked database never blocks polling. Rows that
    /// fail to write stay queued and are retried.
    /// </summary>
    public sealed class DatabaseService : IDisposable
    {
        public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";
        public const int MaxRowsPerQuery = 200000;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan RetentionCheckInterval = TimeSpan.FromHours(6);
        private const int SchemaVersion = 1;
        private const int MaxUnwrittenSamples = 200000;
        private const int MaxUnwrittenActivities = 20000;

        private readonly string _dbPath;
        private readonly string _connectionString;
        private readonly ConcurrentQueue<PowerSupplyStatus> _sampleQueue = new ConcurrentQueue<PowerSupplyStatus>();
        private readonly ConcurrentQueue<ActivityRecord> _activityQueue = new ConcurrentQueue<ActivityRecord>();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly Timer _flushTimer;

        // Only touched while holding _writeLock.
        private readonly List<PowerSupplyStatus> _unwrittenSamples = new List<PowerSupplyStatus>();
        private readonly List<ActivityRecord> _unwrittenActivities = new List<ActivityRecord>();
        private bool _initialized;
        private bool _writeFailing;
        private DateTime _nextRetentionRunUtc = DateTime.MinValue;

        public DatabaseService()
        {
            _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "powerlog.db");
            // Default Timeout caps System.Data.SQLite's own busy retries (30 s by default) so a locked
            // database fails fast and the rows are simply retried on the next flush.
            _connectionString = $"Data Source={_dbPath};Version=3;Default Timeout=5;";

            TryInitialize();
            _flushTimer = new Timer(_ => { var __ = FlushAsync(force: false); }, null, FlushInterval, FlushInterval);
        }

        public string DatabasePath => _dbPath;

        /// <summary>True while writes are failing (rows are kept in memory and retried).</summary>
        public bool IsWriteFailing => _writeFailing;

        #region Schema

        private SQLiteConnection Open()
        {
            var connection = new SQLiteConnection(_connectionString);
            try
            {
                connection.Open();
                Execute(connection, "PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL;");
                return connection;
            }
            catch
            {
                // Release the file handle, otherwise a damaged file cannot be set aside.
                connection.Dispose();
                throw;
            }
        }

        private bool TryInitialize()
        {
            try
            {
                // The app is always installed fresh, so there is no schema upgrade path: a database
                // file with any other layout (or a damaged file) is set aside and a new one started.
                if (File.Exists(_dbPath) && !HasCurrentSchema())
                    SetAsideIncompatibleDatabase();

                if (!File.Exists(_dbPath))
                    SQLiteConnection.CreateFile(_dbPath);

                using (var connection = Open())
                {
                    // WAL lets the Data Logs/plots read while samples are being written.
                    Execute(connection, "PRAGMA journal_mode = WAL;");

                    using (var transaction = connection.BeginTransaction())
                    {
                        Execute(connection, @"
                            CREATE TABLE IF NOT EXISTS PowerLogs (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                Timestamp TEXT NOT NULL,
                                Voltage REAL NOT NULL,
                                Current REAL NOT NULL,
                                Power REAL NOT NULL,
                                OutputEnabled INTEGER NOT NULL,
                                CvMode INTEGER NOT NULL,
                                CcMode INTEGER NOT NULL,
                                UnregulatedMode INTEGER NOT NULL,
                                OvpTrip INTEGER NOT NULL,
                                OcpTrip INTEGER NOT NULL,
                                OutputSenseTrip INTEGER NOT NULL,
                                FaultTrip INTEGER NOT NULL
                            );
                            CREATE INDEX IF NOT EXISTS IX_PowerLogs_Timestamp ON PowerLogs (Timestamp);

                            CREATE TABLE IF NOT EXISTS CommandLog (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                Timestamp TEXT NOT NULL,
                                Kind TEXT NOT NULL,
                                Source TEXT,
                                Command TEXT,
                                Response TEXT,
                                Result TEXT,
                                Requested REAL,
                                Readback REAL,
                                Detail TEXT,
                                DurationMs REAL
                            );
                            CREATE INDEX IF NOT EXISTS IX_CommandLog_Timestamp ON CommandLog (Timestamp);");
                        Execute(connection, $"PRAGMA user_version = {SchemaVersion};");
                        transaction.Commit();
                    }
                }

                _initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                _initialized = false;
                LoggingService.Instance.LogException(ex, nameof(DatabaseService), nameof(TryInitialize));
                return false;
            }
        }

        /// <summary>True for a database created by this version, or an empty file.</summary>
        private bool HasCurrentSchema()
        {
            try
            {
                using (var connection = Open())
                {
                    if (Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), Inv) == SchemaVersion)
                        return true;
                    return Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';"), Inv) == 0;
                }
            }
            catch
            {
                return false; // not a readable SQLite database
            }
        }

        private void SetAsideIncompatibleDatabase()
        {
            SQLiteConnection.ClearAllPools();
            string target = Path.Combine(Path.GetDirectoryName(_dbPath),
                $"powerlog_old_{DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv)}.db");

            File.Move(_dbPath, target);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(_dbPath + suffix))
                    File.Move(_dbPath + suffix, target + suffix);
            }

            LoggingService.Instance.LogWarning(
                $"powerlog.db was not created by this version of the app. It was renamed to {Path.GetFileName(target)} and a new database was started.",
                nameof(DatabaseService), nameof(SetAsideIncompatibleDatabase));
        }

        #endregion

        #region Writing

        /// <summary>Queues a measurement for the next batch write.</summary>
        public void EnqueueSample(PowerSupplyStatus status)
        {
            if (status != null)
                _sampleQueue.Enqueue(status);
        }

        /// <summary>Queues a command/connection/error record for the next batch write.</summary>
        public void EnqueueActivity(ActivityRecord record)
        {
            if (record != null)
                _activityQueue.Enqueue(record);
        }

        /// <summary>
        /// Writes queued rows. <paramref name="force"/> also writes command records still
        /// waiting for their read-back, and waits for a flush already in progress (used on exit).
        /// </summary>
        public async Task FlushAsync(bool force)
        {
            if (!await _writeLock.WaitAsync(force ? 15000 : 0))
                return;
            try
            {
                await Task.Run(() => FlushLocked(force));
            }
            catch (Exception ex)
            {
                LoggingService.Instance.LogException(ex, nameof(DatabaseService), nameof(FlushAsync));
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private void FlushLocked(bool force)
        {
            while (_sampleQueue.TryDequeue(out var sample))
                _unwrittenSamples.Add(sample);
            while (_activityQueue.TryDequeue(out var activity))
                _unwrittenActivities.Add(activity);
            TrimUnwritten();

            var activities = force
                ? _unwrittenActivities.ToList()
                : _unwrittenActivities.Where(a => a.IsReadyToWrite).ToList();

            if (_unwrittenSamples.Count > 0 || activities.Count > 0)
            {
                if (!_initialized && !TryInitialize())
                    return;

                try
                {
                    WriteBatch(_unwrittenSamples, activities);
                    _unwrittenSamples.Clear();
                    _unwrittenActivities.RemoveAll(activities.Contains);

                    if (_writeFailing)
                    {
                        _writeFailing = false;
                        LoggingService.Instance.LogInfo("Database writes recovered.", nameof(DatabaseService), nameof(FlushLocked));
                    }
                }
                catch (Exception ex)
                {
                    // Keep the rows and retry on the next flush; only log the first failure of a streak.
                    if (!_writeFailing)
                    {
                        _writeFailing = true;
                        LoggingService.Instance.LogError(
                            $"Database write failed, rows are kept in memory and retried: {ex.Message}",
                            nameof(DatabaseService), nameof(FlushLocked));
                    }
                    return;
                }
            }

            RunRetentionIfDue();
        }

        private void TrimUnwritten()
        {
            int excessSamples = _unwrittenSamples.Count - MaxUnwrittenSamples;
            if (excessSamples > 0)
            {
                _unwrittenSamples.RemoveRange(0, excessSamples);
                LoggingService.Instance.LogError($"Database unavailable: dropped {excessSamples} oldest unwritten samples.",
                    nameof(DatabaseService), nameof(TrimUnwritten));
            }

            int excessActivities = _unwrittenActivities.Count - MaxUnwrittenActivities;
            if (excessActivities > 0)
                _unwrittenActivities.RemoveRange(0, excessActivities);
        }

        private void WriteBatch(IReadOnlyList<PowerSupplyStatus> samples, IReadOnlyList<ActivityRecord> activities)
        {
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                if (samples.Count > 0)
                {
                    using (var command = new SQLiteCommand(@"
                        INSERT INTO PowerLogs (Timestamp, Voltage, Current, Power, OutputEnabled,
                            CvMode, CcMode, UnregulatedMode, OvpTrip, OcpTrip, OutputSenseTrip, FaultTrip)
                        VALUES (@Timestamp, @Voltage, @Current, @Power, @OutputEnabled,
                            @CvMode, @CcMode, @UnregulatedMode, @OvpTrip, @OcpTrip, @OutputSenseTrip, @FaultTrip);",
                        connection, transaction))
                    {
                        var p = command.Parameters;
                        foreach (var name in new[] { "@Timestamp", "@Voltage", "@Current", "@Power", "@OutputEnabled", "@CvMode",
                                     "@CcMode", "@UnregulatedMode", "@OvpTrip", "@OcpTrip", "@OutputSenseTrip", "@FaultTrip" })
                            p.Add(new SQLiteParameter(name));

                        foreach (var s in samples)
                        {
                            var e = s.EventStatus ?? new EventStatus();
                            p["@Timestamp"].Value = FormatTimestamp(s.Timestamp == default(DateTime) ? DateTime.Now : s.Timestamp);
                            p["@Voltage"].Value = s.Voltage;
                            p["@Current"].Value = s.Current;
                            p["@Power"].Value = s.Power;
                            p["@OutputEnabled"].Value = s.OutputEnabled ? 1 : 0;
                            p["@CvMode"].Value = e.ConstantVoltageMode;
                            p["@CcMode"].Value = e.ConstantCurrentMode;
                            p["@UnregulatedMode"].Value = e.UnregulatedMode;
                            p["@OvpTrip"].Value = e.OverVoltageTrip;
                            p["@OcpTrip"].Value = e.OverCurrentTrip;
                            p["@OutputSenseTrip"].Value = e.OutputSenseTrip;
                            p["@FaultTrip"].Value = e.FaultTrip;
                            command.ExecuteNonQuery();
                        }
                    }
                }

                if (activities.Count > 0)
                {
                    using (var command = new SQLiteCommand(@"
                        INSERT INTO CommandLog (Timestamp, Kind, Source, Command, Response, Result, Requested, Readback, Detail, DurationMs)
                        VALUES (@Timestamp, @Kind, @Source, @Command, @Response, @Result, @Requested, @Readback, @Detail, @DurationMs);
                        SELECT last_insert_rowid();",
                        connection, transaction))
                    {
                        var p = command.Parameters;
                        foreach (var name in new[] { "@Timestamp", "@Kind", "@Source", "@Command", "@Response", "@Result",
                                     "@Requested", "@Readback", "@Detail", "@DurationMs" })
                            p.Add(new SQLiteParameter(name));

                        foreach (var a in activities)
                        {
                            p["@Timestamp"].Value = FormatTimestamp(a.Timestamp);
                            p["@Kind"].Value = a.Kind;
                            p["@Source"].Value = (object)a.Source ?? DBNull.Value;
                            p["@Command"].Value = (object)a.Command ?? DBNull.Value;
                            p["@Response"].Value = (object)a.Response ?? DBNull.Value;
                            p["@Result"].Value = (object)a.Result ?? DBNull.Value;
                            p["@Requested"].Value = (object)a.Requested ?? DBNull.Value;
                            p["@Readback"].Value = (object)a.Readback ?? DBNull.Value;
                            p["@Detail"].Value = (object)a.Detail ?? DBNull.Value;
                            p["@DurationMs"].Value = (object)a.DurationMs ?? DBNull.Value;
                            a.Id = Convert.ToInt64(command.ExecuteScalar(), Inv);
                        }
                    }
                }

                transaction.Commit();
            }
        }

        private void RunRetentionIfDue()
        {
            int days = Settings.Current.RetentionDays;
            if (days <= 0 || DateTime.UtcNow < _nextRetentionRunUtc)
                return;
            _nextRetentionRunUtc = DateTime.UtcNow + RetentionCheckInterval;

            try
            {
                string cutoff = FormatTimestamp(DateTime.Now.AddDays(-days));
                int samples, activities;
                using (var connection = Open())
                {
                    samples = Execute(connection, "DELETE FROM PowerLogs WHERE Timestamp < @Cutoff;", cutoff);
                    activities = Execute(connection, "DELETE FROM CommandLog WHERE Timestamp < @Cutoff;", cutoff);
                }
                if (samples + activities > 0)
                    LoggingService.Instance.LogInfo(
                        $"Retention ({days} days): deleted {samples} samples and {activities} log rows older than {cutoff}.",
                        nameof(DatabaseService), nameof(RunRetentionIfDue));
            }
            catch (Exception ex)
            {
                LoggingService.Instance.LogException(ex, nameof(DatabaseService), nameof(RunRetentionIfDue));
            }
        }

        #endregion

        #region Reading

        public async Task<long> CountLogsAsync(DateTime startTime, DateTime endTime)
        {
            return await Task.Run(() =>
            {
                using (var connection = Open())
                using (var command = new SQLiteCommand(
                    "SELECT COUNT(*) FROM PowerLogs WHERE Timestamp BETWEEN @StartTime AND @EndTime;", connection))
                {
                    AddRange(command, startTime, endTime);
                    return Convert.ToInt64(command.ExecuteScalar(), Inv);
                }
            });
        }

        /// <summary>Returns at most <paramref name="maxRows"/> samples in the range, oldest first.</summary>
        public async Task<List<LogEntry>> GetLogsAsync(DateTime startTime, DateTime endTime, int maxRows = MaxRowsPerQuery)
        {
            return await Task.Run(() =>
            {
                var logs = new List<LogEntry>();
                using (var connection = Open())
                using (var command = new SQLiteCommand(@"
                    SELECT Timestamp, Voltage, Current, Power, OutputEnabled, CvMode, CcMode, UnregulatedMode,
                           OvpTrip, OcpTrip, OutputSenseTrip, FaultTrip
                    FROM PowerLogs
                    WHERE Timestamp BETWEEN @StartTime AND @EndTime
                    ORDER BY Timestamp ASC
                    LIMIT @MaxRows;", connection))
                {
                    AddRange(command, startTime, endTime);
                    command.Parameters.AddWithValue("@MaxRows", maxRows);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (!TryParseTimestamp(reader.GetString(0), out var timestamp))
                                continue;
                            double voltage = reader.GetDouble(1);
                            double current = reader.GetDouble(2);
                            logs.Add(new LogEntry
                            {
                                Timestamp = timestamp,
                                Voltage = voltage,
                                Current = current,
                                Power = reader.GetDouble(3),
                                OutputEnabled = reader.GetInt64(4) == 1,
                                CvMode = reader.GetInt64(5) == 1,
                                CcMode = reader.GetInt64(6) == 1,
                                UnregulatedMode = reader.GetInt64(7) == 1,
                                OvpTrip = reader.GetInt64(8) == 1,
                                OcpTrip = reader.GetInt64(9) == 1,
                                SenseTrip = reader.GetInt64(10) == 1,
                                FaultTrip = reader.GetInt64(11) == 1
                            });
                        }
                    }
                }
                return logs;
            });
        }

        /// <summary>Returns command, connection and error records in the range, oldest first.</summary>
        public async Task<List<ActivityRecord>> GetActivitiesAsync(DateTime startTime, DateTime endTime, int maxRows = MaxRowsPerQuery)
        {
            return await Task.Run(() =>
            {
                var records = new List<ActivityRecord>();
                using (var connection = Open())
                using (var command = new SQLiteCommand(@"
                    SELECT Id, Timestamp, Kind, Source, Command, Response, Result, Requested, Readback, Detail, DurationMs
                    FROM CommandLog
                    WHERE Timestamp BETWEEN @StartTime AND @EndTime
                    ORDER BY Timestamp ASC, Id ASC
                    LIMIT @MaxRows;", connection))
                {
                    AddRange(command, startTime, endTime);
                    command.Parameters.AddWithValue("@MaxRows", maxRows);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (!TryParseTimestamp(reader.GetString(1), out var timestamp))
                                continue;
                            var record = new ActivityRecord(reader.GetString(2))
                            {
                                Id = reader.GetInt64(0),
                                Timestamp = timestamp,
                                Source = NullableString(reader, 3),
                                Command = NullableString(reader, 4),
                                Response = NullableString(reader, 5),
                                Requested = NullableDouble(reader, 7),
                                DurationMs = NullableDouble(reader, 10)
                            };
                            record.Complete(NullableString(reader, 6), NullableDouble(reader, 8), NullableString(reader, 9));
                            records.Add(record);
                        }
                    }
                }
                return records;
            });
        }

        #endregion

        #region Helpers

        public static string FormatTimestamp(DateTime timestamp)
        {
            return timestamp.ToString(TimestampFormat, Inv);
        }

        private static bool TryParseTimestamp(string text, out DateTime timestamp)
        {
            return DateTime.TryParseExact(text, TimestampFormat, Inv, DateTimeStyles.None, out timestamp);
        }

        private static void AddRange(SQLiteCommand command, DateTime startTime, DateTime endTime)
        {
            command.Parameters.AddWithValue("@StartTime", FormatTimestamp(startTime));
            command.Parameters.AddWithValue("@EndTime", FormatTimestamp(endTime));
        }

        private static string NullableString(SQLiteDataReader reader, int index)
        {
            return reader.IsDBNull(index) ? null : reader.GetValue(index).ToString();
        }

        private static double? NullableDouble(SQLiteDataReader reader, int index)
        {
            return reader.IsDBNull(index) ? (double?)null : Convert.ToDouble(reader.GetValue(index), Inv);
        }

        private static int Execute(SQLiteConnection connection, string sql, string cutoff = null)
        {
            using (var command = new SQLiteCommand(sql, connection))
            {
                if (cutoff != null)
                    command.Parameters.AddWithValue("@Cutoff", cutoff);
                return command.ExecuteNonQuery();
            }
        }

        private static object Scalar(SQLiteConnection connection, string sql)
        {
            using (var command = new SQLiteCommand(sql, connection))
                return command.ExecuteScalar();
        }

        #endregion

        public void Dispose()
        {
            _flushTimer.Dispose();
        }
    }
}
